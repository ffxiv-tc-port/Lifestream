using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 傳送面板按下目的地之後的整條流程。
/// </summary>
/// <remarks>傳送本身一律走既有的安全路徑：
///   - 主水晶  → <see cref="Services.TeleportService.TeleportToAetheryte"/>(純 <c>Telepo::Teleport</c>)
///   - 城內乙太之光 → <see cref="TaskAetheryteAethernetTeleport"/>(傳送到主水晶 → 實際互動 → 選單)
/// 🔴 DailyRoutines 原版在這一段會自己組封包送出來開乙太之光選單 —— **封包偽造是紅線，沒有移植**，
/// 這裡走的是「真的走過去互動」的合法流程。
/// <para>
/// 抵達後的「傳送到座標」是可選的，而且分成兩層開關(見 <see cref="Data.Config.EnableAetheryteLanding"/>
/// 與 <see cref="Data.Config.AetheryteLandingDirectWrite"/>)：
///   第一層(預設關)：走 vnavmesh 走過去 —— 跟 <c>/li goto</c> 同一套機制，沒有任何記憶體寫入。
///   第二層(預設關、需先開第一層)：直接寫座標瞬移。這是使用者自行承擔風險的選項。
/// </para>
/// <para>第一層底下還有一個純最佳化的旁支(<see cref="TryEnqueueLandingAethernetRelay"/>)：落點離同城某座
/// 城內乙太之光比離剛傳送到的主水晶近很多時，先搭一段都市傳送網再走完最後一段，
/// 免得在九號解決方案這種大城裡橫跨整張地圖用走的。判斷不成立就照第一層從主水晶走過去。</para>
/// </remarks>
public static unsafe partial class TaskTeleportPanelGo
{
    /// <summary>
    /// 入口包裝：真的排了任務就在尾端補「抵達提醒」哨兵。
    /// 三個入口（最愛視窗/傳送面板/IPC TeleportToFavorite）
    /// 都收斂到這裡，一處包三處生效。哨兵自帶設定開關、去重與
    /// 「沒排任務就不響」判斷，早退路徑（忙碌/無玩家）自然靜默。
    /// 用 try/finally 而不是在每個 return 前呼叫：未來新增早退路徑也自動正確。
    /// </summary>
    public static void Enqueue(TeleportPanelEntry entry)
    {
        var queuedBefore = P.TaskManager.NumQueuedTasks;
        try
        {
            EnqueueCore(entry);
        }
        finally
        {
            TaskAnnounceArrival.EnqueueIfChainStarted(queuedBefore);
        }
    }

    private static void EnqueueCore(TeleportPanelEntry entry)
    {
        if(entry == null) return;
        if(P.TaskManager.IsBusy)
        {
            DuoLog.Error("Lifestream is busy");
            return;
        }
        if(!Player.Interactable)
        {
            DuoLog.Error("Can't teleport - no player");
            return;
        }

        // DailyRoutines 把 BetterTeleport 宣告成依賴 SameAethernetTeleport。我們的版本**不是硬依賴**
        // (面板不開修補也完全能用)，但確實有一個情境會撞到：站在目標乙太之光旁邊時，遊戲會用
        // LogMessage 1478「此處為目前所在地。」拒絕傳送 —— 而那正是搭配自訂落點時最常做的動作
        // (重新傳送一次讓自己回到儲存的位置)。沒開修補就先講清楚，不要排一條註定沉默失敗的佇列。
        if(!entry.IsAetheryte && !SameAethernetTeleportPatch.IsApplied && IsStandingAt(entry))
        {
            DuoLog.Warning("You are already at this aethernet shard, so the game will not teleport you to the same place. Just walk to where you want to go.".Loc());
            return;
        }

        var landing = !C.AetheryteLandings.ContainsKey(entry.Id) ? "無" : C.EnableAetheryteLanding ? "有" : "有，但功能關著（不會前往）";
        PluginLog.Information($"[TeleportPanel] 出發：「{entry.DisplayName}」（{(entry.IsAetheryte ? "乙太之光" : "城內乙太之晶")} {entry.Id}、sub {entry.SubIndex}、區域 {entry.Territory}）；自訂落點＝{landing}；直接寫入座標＝{C.AetheryteLandingDirectWrite}。");
        TravelDiag.Record($"傳送面板出發：「{entry.DisplayName}」({entry.Id})");
        TaskRemoveAfkStatus.Enqueue();

        if(entry.IsAetheryte)
        {
            P.TaskManager.Enqueue(() => S.TeleportService.TeleportToAetheryte(entry.Id, entry.SubIndex),
                "TeleportPanelTeleport", new(timeLimitMS: 30000));
            P.TaskManager.Enqueue(Utils.WaitForScreenFalse);
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else if(IsGatewayZoneShard(entry, out var gateway))
        {
            EnqueueGatewayZoneShardRoute(entry, gateway);
        }
        else
        {
            // 城內乙太之光。TaskAetheryteAethernetTeleport.Enqueue 是排到佇列尾端的，落點步驟在這行之後才排，所以順序正確。
            // 它在找不到主水晶/子節點時會丟例外；這是從 ImGui 的繪製回呼呼叫進來的，丟例外會打斷整個視窗的繪製，
            // 所以降級成聊天欄錯誤，並中止已經排進去的步驟，不要留下半條佇列。
            try
            {
                TaskAetheryteAethernetTeleport.Enqueue(entry.MasterId, entry.Id);
            }
            catch(Exception e)
            {
                PluginLog.Error($"[TeleportPanel] Could not enqueue aethernet teleport to {entry.Id} via {entry.MasterId}: {e.Message}");
                DuoLog.Error($"Could not reach {entry.DisplayName}");
                P.TaskManager.Abort();
                return;
            }
            // ⚠️ 路線若決定「用走的」(TaskAethernetRoute.RouteUsesAethernet=false)就根本不會有讀取畫面,
            // 直接放行,否則這裡會空轉滿 15 秒才往下走。
            P.TaskManager.Enqueue(
                () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                "TeleportPanelWaitTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }

        EnqueueLanding(entry);
    }

    /// <summary>
    /// 這一列是不是「玄關區域**內部**的城內乙太之光」——目前只有蒼天街的那八座。
    /// 它們的 <see cref="TeleportPanelEntry.Id"/> 是 Lifestream 的偽 id(69420000 起)，<c>Aetheryte</c> 表裡查無此列，
    /// <see cref="TaskAetheryteAethernetTeleport.Enqueue"/> 那條一般路線走不通 ——
    /// 🔴 而且失敗方式是**靜默走錯**:找不到子節點時它會退化成「只傳送到母城主水晶」，畫面上不會有任何錯誤。
    /// ⚠️ 用 <see cref="TeleportPanelEntry.Id"/> 而不是只看區域來判斷:玄關區域本身那一列
    /// (蒼天街/渴望灣，id 是 <c>uint.MaxValue</c> 往下數)的 Territory 也是玄關區域，但它走的是一般路線，不能混進來。
    /// </summary>
    private static bool IsGatewayZoneShard(TeleportPanelEntry entry,
        out TaskAetheryteAethernetTeleport.GatewayRoute gateway)
    {
        gateway = null;
        if(!TaskAetheryteAethernetTeleport.TryGetGatewayRouteByTerritory(entry.Territory, out var route)) return false;
        if(S.Data.CustomAethernet?.CustomAetheryteNames.ContainsKey(entry.Id) != true) return false;
        gateway = route;
        return true;
    }

    /// <summary>
    /// 前往蒼天街的某一座城內乙太之光。分三段：先用玄關路線進到該區域，落地後**需要的話走到最近的
    /// 那一座乙太之光**，再用區域內的乙太之光網路跳過去。
    /// </summary>
    /// <remarks>每一段都是既有的流程：
    ///   第一段 = <see cref="TaskAetheryteAethernetTeleport.Enqueue"/>(浮動視窗的「蒼天街」按鈕走的就是它)
    ///   第二段 = vnavmesh IPC 尋路 + <see cref="Movement.FollowPath"/>，與 <c>/li goto</c>、
    ///            自訂落點用的是同一套(<see cref="TaskGotoDestination.EnqueueNavTo"/>)
    ///   第三段 = 與 <see cref="SameWorld.TaskAethernetTeleport"/> 相同的四步(鎖定→互動→選單→選目的地)，
    ///            只是改用 <c>InsertMulti</c> 排在當下這一步的後面，因為它必須在**抵達之後**才決定要不要做。
    /// <para>
    /// 🔴 第二段不可省：蒼天街的玄關落點離最近的「無名眾人廣場」超出互動範圍
    /// (該區域的 <see cref="Data.ZoneDetail.MaxInteractionDistance"/> 只有 4.56 碼)，
    /// 直接排互動步驟的話 <see cref="WorldChange.TargetValidAetheryte"/> 永遠找不到東西可鎖定，
    /// 整條佇列就在原地空轉到逾時 —— **畫面上一行訊息都沒有**。</para>
    /// </remarks>
    private static void EnqueueGatewayZoneShardRoute(TeleportPanelEntry entry,
        TaskAetheryteAethernetTeleport.GatewayRoute gateway)
    {
        if(P.Territory != entry.Territory)
        {
            TaskAetheryteAethernetTeleport.Enqueue(gateway.RootAetheryteId, gateway.AethernetId);
            // 進去是一整段區域轉場。⚠️ 這一步刻意讓逾時中止整條佇列：沒到對的區域就開始找乙太之光，
            // 會在錯的地圖上鎖定錯的東西然後對它開選單。
            P.TaskManager.Enqueue(
                () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
                "TeleportPanelWaitGatewayArrival", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }

        // 🔑 「還要不要再跳一次」**必須在執行期判斷**，不能在排佇列的當下決定：
        // 玄關傳送的落點就是該區域的入口乙太之光，使用者要去的如果正好是它，這時再開選單選自己
        // 會被遊戲以 LogMessage 1478「此處為目前所在地。」拒絕 —— 與本檔開頭那個前置檢查同一個理由，
        // 只是那個檢查是在出發前做的，管不到「傳送過去之後才站到目的地上」這種情形。
        P.TaskManager.Enqueue(() =>
        {
            if(IsStandingAt(entry))
            {
                PluginLog.Information($"[TeleportPanel] Arrived directly at {entry.Name} - no aethernet hop needed.");
                return;
            }

            // 落地就在某座乙太之光的互動範圍內 —— 直接跳。
            // 判準刻意用 GetValidAetheryte 而不是自己算距離：那正是下一步 TargetValidAetheryte /
            // InteractWithTargetedAetheryte 用的同一個函式(含該區域專屬的 MaxInteractionDistance)，
            // 所以它非 null 就代表互動那幾步一定摸得到東西。
            if(IsAetheryteInReach())
            {
                InsertAethernetHop(entry);
                return;
            }

            InsertWalkToNearestShard(entry);
        }, "TeleportPanelGatewayZoneHop");
        P.TaskManager.Enqueue(Utils.WaitForScreen);
    }

    /// <summary>
    /// 「互動 → 選單 → 選目的地」。抽成獨立方法是因為有兩個進入點：落地就在乙太之光旁邊，
    /// 以及走過去之後。兩邊必須排出**完全相同**的四步，不要各留一份。
    /// </summary>
    private static void InsertAethernetHop(TeleportPanelEntry entry)
    {
        P.TaskManager.InsertMulti(
            new(WorldChange.TargetValidAetheryte),
            new(WorldChange.InteractWithTargetedAetheryte),
            new(WorldChange.SelectAethernetIfNeeded),
            // ⚠️ 用 Name 不是 DisplayName：這個字串要跟遊戲選單上的項目比對，
            // 使用者的備註在這裡會讓它一個都對不上。
            new(() => WorldChange.TeleportToAethernetDestination(entry.Name),
                nameof(WorldChange.TeleportToAethernetDestination))
            );
    }

    /// <summary>
    /// 走去乙太之光的時限。蒼天街玄關落點到最近的那一座只有幾十碼，正常步行遠低於這個數字，
    /// 逾時多半是卡地形或路徑本身有問題。
    /// ⚠️ 這一步刻意 <c>abortOnTimeout: false</c>：逾時即中止會讓後面的抵達檢查**跑不到**，
    /// 使用者就又回到「什麼都沒發生」。改成讓檢查那一步一定跑得到，由它報錯並中止。
    /// </summary>
    private const int GatewayZoneWalkTimeLimitMS = 90000;

    /// <summary><c>Level</c> 表裡「城內乙太之光」的 <c>Type</c>。</summary>
    private const byte AethernetShardLevelType = 45;

    /// <summary>
    /// 每個區域掃一次就快取。<c>Level</c> 是大表，而 <see cref="TryGetNearestShard"/> 一次會問到
    /// 八座 —— 逐座各掃一次全表會在同一幀掉影格。**空清單也要快取**(新月島就是空的)。
    /// </summary>
    private static readonly Dictionary<uint, List<Vector3>> LevelShardPositions = [];

    private static void FlagOnMap(uint territory, Vector3 position)
    {
        var agent = AgentMap.Instance();
        if(agent == null) return;
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.RowId ?? 0;
        if(mapId == 0) return;
        agent->SetFlagMapMarker(territory, mapId, position);
        agent->OpenMap(mapId, territory);
    }

    /// <summary>
    /// 只比水平距離：乙太之光座標多半是從地圖標記換算來的，Y 恆為 0，
    /// 把 Y 算進去會讓不同來源的座標無法公平比較。
    /// </summary>
    private static float DistanceXZ(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// 人是不是就站在這座乙太之光旁邊。只比水平距離：乙太之光座標多半是從地圖標記換算來的，
    /// Y 恆為 0，把 Y 算進去會讓不同來源的座標無法公平比較。
    /// </summary>
    private static bool IsStandingAt(TeleportPanelEntry entry)
    {
        if(entry.Position == null || !Player.Available) return false;
        if(P.Territory != entry.Territory) return false;
        var pos = entry.Position.Value;
        return new Vector2(Player.Position.X - pos.X, Player.Position.Z - pos.Z).Length() < 12f;
    }
}
