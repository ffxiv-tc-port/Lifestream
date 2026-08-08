using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 傳送面板按下目的地之後的整條流程。
///
/// 傳送本身一律走既有的安全路徑：
///   - 主水晶  → <see cref="Services.TeleportService.TeleportToAetheryte"/>(純 <c>Telepo::Teleport</c>)
///   - 城內乙太之光 → <see cref="TaskAetheryteAethernetTeleport"/>(傳送到主水晶 → 實際互動 → 選單)
/// 🔴 DailyRoutines 原版在這一段會自己組 <c>EventStartPackt</c> 送出封包來開乙太之光選單 ——
/// **封包偽造是紅線，沒有移植**。Lifestream 本來就有「真的走過去互動」的合法流程，直接用它。
///
/// 抵達後的「傳送到座標」是可選的，而且分成兩層開關(見 <see cref="Data.Config.EnableAetheryteLanding"/>
/// 與 <see cref="Data.Config.AetheryteLandingDirectWrite"/>)：
///   第一層(預設關)：走 vnavmesh 走過去 —— 跟 <c>/li goto</c> 同一套機制，沒有任何記憶體寫入。
///   第二層(預設關、需先開第一層)：直接寫座標瞬移。這是使用者自行承擔風險的選項。
/// </summary>
public static unsafe class TaskTeleportPanelGo
{
    public static void Enqueue(TeleportPanelEntry entry)
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
            DuoLog.Warning("You are already at this aethernet shard - the game refuses this teleport. See the Teleport Panel settings if you want to allow it.".Loc());
            return;
        }

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
            // 城內乙太之光。TaskAetheryteAethernetTeleport.Enqueue 是排到佇列尾端的，
            // 而落點步驟是在這行之後才排進去的,所以順序正確(這正是 TaskGotoDestination
            // 註解裡提醒過的 Enqueue/InsertMulti 陷阱,這裡是「可以用 Enqueue」的那一側)。
            //
            // 它在找不到主水晶/子節點時會丟例外。我們的 MasterId 直接來自 DataStore 的鍵，
            // 照理不會發生;但這是從 ImGui 的繪製回呼呼叫進來的 —— 這裡丟例外會打斷整個視窗的繪製,
            // 所以降級成聊天欄錯誤,並中止已經排進去的步驟,不要留下半條佇列。
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
    ///
    /// 它們與一般城內乙太之光的差別在於 <see cref="TeleportPanelEntry.Id"/> 是 Lifestream 的偽 id
    /// (69420000 起)，<c>Aetheryte</c> 表裡查無此列，所以
    /// <see cref="TaskAetheryteAethernetTeleport.Enqueue"/> 那條一般路線走不通 ——
    /// 🔴 而且它的失敗方式是**靜默走錯**:找不到子節點時它會退化成「只傳送到母城主水晶」，
    /// 人被丟在伊修加爾德下層，蒼天街那一段完全沒發生，畫面上不會有任何錯誤。
    ///
    /// ⚠️ 用 <see cref="TeleportPanelEntry.Id"/> 而不是只看區域來判斷:玄關區域本身那一列
    /// (蒼天街/渴望灣，id 是 <c>uint.MaxValue</c> 往下數)的 Territory 也是玄關區域，
    /// 但它走的是一般路線(<c>TaskAetheryteAethernetTeleport</c> 認得那兩個偽 id)，不能混進來。
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
    /// 前往蒼天街的某一座城內乙太之光。分兩段：先用玄關路線進到該區域，再用區域內的乙太之光網路跳過去。
    ///
    /// 兩段都是既有的、已經上線過的流程，沒有新的原生層操作：
    ///   第一段 = <see cref="TaskAetheryteAethernetTeleport.Enqueue"/>(浮動視窗的「蒼天街」按鈕走的就是它)
    ///   第二段 = 與 <see cref="SameWorld.TaskAethernetTeleport"/> 相同的四步(鎖定→互動→選單→選目的地)，
    ///            只是改用 <c>InsertMulti</c> 排在當下這一步的後面，因為它必須在**抵達之後**才決定要不要做。
    /// </summary>
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
            P.TaskManager.InsertMulti(
                new(WorldChange.TargetValidAetheryte),
                new(WorldChange.InteractWithTargetedAetheryte),
                new(WorldChange.SelectAethernetIfNeeded),
                // ⚠️ 用 Name 不是 DisplayName：這個字串要跟遊戲選單上的項目比對，
                // 使用者的備註在這裡會讓它一個都對不上。
                new(() => WorldChange.TeleportToAethernetDestination(entry.Name),
                    nameof(WorldChange.TeleportToAethernetDestination))
                );
        }, "TeleportPanelGatewayZoneHop");
        P.TaskManager.Enqueue(Utils.WaitForScreen);
    }

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

    /// <summary>
    /// 若該乙太之光設了自訂落點而且功能已啟用，抵達後再前往落點。
    /// 沒設、或功能關著，就什麼都不做 —— 傳送本身完全不受影響。
    /// </summary>
    private static void EnqueueLanding(TeleportPanelEntry entry)
    {
        if(!C.EnableAetheryteLanding) return;
        if(!C.AetheryteLandings.TryGetValue(entry.Id, out var landing)) return;

        // 到了對的區域、能動了，才開始處理落點。逾時中止整條佇列：
        // 沒到對的地圖就開始導航(或更糟，直接寫座標)會在錯的地方亂跑。
        P.TaskManager.Enqueue(
            () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
            "TeleportPanelWaitArrival", new(timeLimitMS: 120000));
        P.TaskManager.Enqueue(Utils.WaitForScreen);

        P.TaskManager.Enqueue(() =>
        {
            if(C.AetheryteLandingDirectWrite)
            {
                if(TryDirectWrite(landing)) return true;
                // 直接寫失敗(例如在戰鬥中/副本裡)就退回安全路徑，不要什麼都不做。
                ChatPrinter.Red($"[Lifestream] {"Direct position write refused - falling back to walking.".Loc()}");
            }
            EnqueueWalkTo(entry, landing);
            return true;
        }, "TeleportPanelLanding");
    }

    private static void EnqueueWalkTo(TeleportPanelEntry entry, Vector3 landing)
    {
        if(Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded))
        {
            TaskGotoDestination.EnqueueNavTo(landing);
        }
        else
        {
            var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(entry.Territory)?.Map.RowId ?? 0;
            if(mapId != 0)
            {
                AgentMap.Instance()->SetFlagMapMarker(entry.Territory, mapId, landing);
                AgentMap.Instance()->OpenMap(mapId, entry.Territory);
            }
            ChatPrinter.Green($"[Lifestream] {"vnavmesh is not installed - destination flagged on map, please walk there manually:".Loc()} {entry.DisplayName}");
        }
    }

    /// <summary>
    /// 🔴 直接把玩家座標寫進遊戲記憶體(瞬移)。使用者自行承擔風險的功能，預設關閉。
    ///
    /// 安全性設計(針對「崩潰」而非「被偵測」)：
    ///   - 指標是**當下這一幀重新取得**的(<see cref="Player.GameObject"/> 每次都從
    ///     <c>Svc.ClientState.LocalPlayer.Address</c> 重解析)，絕不跨幀保存，也不存 IGameObject。
    ///   - 呼叫的是 FFXIVClientStructs 的 <c>GameObject.SetPosition</c>。該特徵碼
    ///     <c>E8 ?? ?? ?? ?? 83 4B 70 01</c> 已離線在台服 7.20 客戶端驗過：.text 唯一命中
    ///     0x1416F3C7C，跟隨 E8 後為 0x140853C50，函式本體就是把 X/Y/Z 寫進 [rcx+0xB0/B4/B8]
    ///     並更新 [rcx+0x9C] 的旗標 —— 與目前 CS 的 GameObject.Position 偏移一致。
    ///   - 沒有 hook、沒有每幀常駐、沒有封包。
    ///
    /// 前置條件擋掉最容易出事的情境(副本、戰鬥、轉場、坐騎飛行中)。
    /// </summary>
    private static bool TryDirectWrite(Vector3 target)
    {
        if(!Player.Interactable) return false;
        if(Player.IsInDuty) return false;
        if(Svc.Condition[ConditionFlag.InCombat]) return false;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
        if(Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Occupied]) return false;

        var go = Player.GameObject;
        if(go == null) return false;

        go->SetPosition(target.X, target.Y, target.Z);
        PluginLog.Information($"[TeleportPanel] Direct position write to {target.X:F1}, {target.Y:F1}, {target.Z:F1}");
        return true;
    }
}
