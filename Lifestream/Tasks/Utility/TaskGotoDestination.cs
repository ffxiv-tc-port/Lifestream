using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Data;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.Legacy;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 自訂座標傳送(安全版,參考 DR BetterTeleport 的自訂落點功能):
/// 1) 先找出「離目標點最近的傳送落點」——優先考慮城內以太之光(aethernet shard),
///    沒有比主水晶近的城內以太之光時才直接傳送到主水晶;
/// 2) vnavmesh IPC 尋路走過去(軟依賴);
/// 3) 未安裝 vnavmesh 時改為標記地圖旗點並開地圖,提示手動前往。
/// 本流程不做記憶體改座標瞬移(DR 版的 TPSmart/TPPlayerAddress 不抄);
/// 唯一的直接寫座標是傳送面板落點的使用者選項,見 TaskTeleportPanelGo.TryDirectWrite。
/// </summary>
/// <remarks>📌 兩個進入點共用同一段「抵達目的區域」的編排(<see cref="EnqueueTravelToZone"/>):
///    <list type="bullet">
///    <item><see cref="Enqueue(CustomDestination)"/> —— 使用者存下來的自訂落點(有真實三維座標)。</item>
///    <item><see cref="EnqueueToMapPoint(MapPointDestination, bool)"/> —— 別的外掛透過
///          <c>Lifestream.GoToMapPoint</c> IPC 丟進來的地圖上一點(只有 XZ,高度抵達後才問 vnavmesh)。</item>
///    </list>
/// </remarks>
public static unsafe partial class TaskGotoDestination
{
    /// <summary>
    /// 多繞一次城內以太之光要多花十幾秒(互動→開選單→讀取畫面),
    /// 所以要比「直接傳送到主水晶再走」近上這個距離才值得。
    /// 這是取捨用的經驗值,不是實測出來的數字;同時也吸收地圖標記座標本身的誤差。
    /// </summary>
    private const float AethernetGainThreshold = 30f;

    /// <summary>
    /// 問 vnavmesh「這個 XZ 底下的地板在哪」時的探測高度。
    /// vnavmesh 的 <c>FindPointOnFloor</c> 是「在縱向 ±2048 的範圍內,找 Y 不高於探測點的最高面」,
    /// 所以探測點要放在地形之上;1024 對遊戲內的地圖都夠高。
    /// </summary>
    private const float FloorProbeAltitude = 1024f;

    /// <summary>地板探測的水平容差。先用嚴格值,失敗再放寬一次(地圖點擊本來就有幾碼誤差)。</summary>
    private const float FloorProbeHalfExtentXZ = 5f;
    private const float FloorProbeHalfExtentXZWide = 20f;

    /// <summary>
    /// 轉場後「等 vnavmesh 自己把上一張網格清掉」的觀察窗與退化等待。
    /// 🔴 vnavmesh 是在它自己的 Update 才 ClearState,讀取畫面剛結束的那幾幀 <c>Nav.IsReady</c>
    ///    可能還是 true,但指著**上一個區域**的網格 —— 拿它去尋路會得到穿牆/摔落的路線。
    ///    所以轉場過的這一趟要先觀察到一次 IsReady==false,才相信之後的 true。
    /// ⚠️ 「一定抓得到那次 false」無法離線證明(窗口可能落在兩次 framework update 之間,或 vnavmesh 重用同一張網格而不清),因此有退化路徑:
    ///    觀察窗過完仍沒看到 false 時,再固定等 <see cref="NavStaleFallbackMs"/> 就放行,並留一行 Information。
    /// </summary>
    private const int NavStaleObserveMs = 3000;
    private const int NavStaleFallbackMs = 2000;

    public static void Enqueue(CustomDestination dest)
    {
        PluginLog.Information($"[Goto] 自訂地點「{dest.Name}」⇒ {ExcelTerritoryHelper.GetName(dest.Territory)}({dest.Territory}) {dest.Position:F1}；目前區域 {P.Territory}、vnavmesh={IsVnavmeshLoaded()}。");
        TravelDiag.Record($"/li goto 自訂地點「{dest.Name}」");
        if(!EnqueueTravelToZone(dest.Territory, dest.Position, dest.Name, out _)) return;
        P.TaskManager.Enqueue(() =>
        {
            if(IsVnavmeshLoaded())
            {
                EnqueueFieldNavTo(dest.Position, $"自訂地點「{dest.Name}」");
            }
            else
            {
                PluginLog.Information($"[Goto] {dest.Name}: vnavmesh 沒有載入，改成在地圖插旗。");
                SetFlag(dest.Territory, dest.Position, dest.Name);
            }
            return true;
        }, "GotoNavOrFlag");
    }

    /// <summary>
    /// 前往「地圖上的一個點」(<c>Lifestream.GoToMapPoint</c> IPC 的實作)。
    /// 與 <see cref="Enqueue(CustomDestination)"/> 的差別只有兩處:
    /// <list type="number">
    /// <item>目標**沒有高度** —— 抵達目的區域、navmesh 就緒之後才向 vnavmesh 問地板;</item>
    /// <item>可選擇用飛行坐騎跑最後一段。</item>
    /// </list>
    /// </summary>
    /// <returns>false = 走不到,已在聊天欄講明原因,**什麼都沒排進佇列**(呼叫端不要等它完成)。</returns>
    public static bool EnqueueToMapPoint(MapPointDestination dest, bool fly)
    {
        var routingPos = dest.RoutingPosition;
        var crossZone = P.Territory != dest.Territory;
        // 「這個區域能不能飛」是純資料(區域用途 + 該區風脈是否收集完),傳送前就能算,
        // 不需要真的去試起飛。算不出來或不可飛就直接用走的,一步都不浪費。
        var willFly = fly && IsFlyingUnlockedIn(dest.Territory);

        if(!EnqueueTravelToZone(dest.Territory, routingPos, dest.Name, out var zoned)) return false;

        PluginLog.Information($"[Goto] 地圖點 ⇒ {ExcelTerritoryHelper.GetName(dest.Territory)}({dest.Territory}) "
            + $"XZ({dest.WorldX:F1}, {dest.WorldZ:F1});跨區={crossZone};會經過讀取畫面={zoned};"
            + $"要求飛行={fly},區域可飛預判={willFly}(最後一段落點解析後再依距離門檻定案)。");

        var run = new MapPointRun() { Dest = dest, RequestedFly = fly, Zoned = zoned };
        // ⚠️ 逾時刻意不中止:逾時了也要走到下一步,由 ResolveFloorAndGo 判斷是「沒到對的區域」
        //    還是「網格沒建好」並印出對應的訊息 —— 靜默中止是最難回報的失敗形式。
        P.TaskManager.Enqueue(() => WaitNavmeshFresh(run), "GotoMapPointWaitNavFresh",
            new(timeLimitMS: 120000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() => ResolveFloorAndGo(run), "GotoMapPointResolveFloor");
        return true;
    }

    /// <summary>
    /// 把「抵達目的區域」那一段排進佇列:乙太網分店 / 玄關路線 / 傳送到最近主水晶,三選一。
    /// </summary>
    /// <param name="territory">目的區域的 TerritoryType row id。</param>
    /// <param name="routingPos">用來挑落點的座標。**只有 XZ 會被用到**,Y 是什麼都不影響結果。</param>
    /// <param name="name">聊天欄訊息與 log 用的顯示名稱。</param>
    /// <param name="zoned">這條路線是否會經過讀取畫面(＝之後必須做 navmesh 的 stale 閘門)。</param>
    /// <returns>false = 走不到,已在聊天欄講明原因,什麼都沒排。</returns>
    private static bool EnqueueTravelToZone(uint territory, Vector3 routingPos, string name, out bool zoned)
    {
        var hasAethernetRoute = TryFindAethernetRoute(territory, routingPos, name, out var root, out var shard);

        // 蒼天街、渴望灣這類區域沒有自己的乙太之光,是靠鄰近區域某座乙太之光的選單項進去的。
        // 對它們 FindClosestUnlockedAetheryte 永遠回 0,所以改走 TaskAetheryteAethernetTeleport 建模好的玄關路線。
        uint gatewayRoot = 0;
        uint gatewayAethernet = 0;
        var hasGatewayRoute = !hasAethernetRoute
            && P.Territory != territory
            && TaskAetheryteAethernetTeleport.TryGetGatewayRoute(territory, out gatewayRoot, out gatewayAethernet);

        // 先驗證目的地可達再排任務:走不到就在聊天欄講清楚,不丟例外。
        // (已在目的區域時不需要傳送點,TeleportToDestinationZone 會直接回 true)
        if(!hasAethernetRoute && !hasGatewayRoute && P.Territory != territory && FindClosestUnlockedAetheryte(territory, routingPos) == 0)
        {
            PluginLog.Information($"[Goto] {name}: 放棄——{ExcelTerritoryHelper.GetName(territory)}({territory}) 沒有已解鎖的乙太之光、都市傳送網路線或玄關路線可用，什麼都沒排。");
            ChatQueue.Red($"[Lifestream] {LocText.CannotReachDestinationNoAetheryte.Loc()} {name} ({ExcelTerritoryHelper.GetName(territory)})");
            zoned = false;
            return false;
        }

        // 三條路線都會過讀取畫面;同區而且不搭乙太網的「直接走過去」則不會。
        zoned = hasAethernetRoute || hasGatewayRoute || P.Territory != territory;

        if(hasAethernetRoute)
        {
            PluginLog.Information($"[Goto] {name}: using aethernet route {root.Name}({root.ID}) -> {shard.Name}({shard.ID})");
            // 需要的話先接近可用的乙太之光節點(優先用身邊摸得到的同網路節點),再互動並用乙太網跳到
            // 目標城內乙太之光。這一整套跟傳送面板/地圖點擊/「/li <地名>」共用同一份(見 TaskAethernetRoute)。
            TaskAethernetRoute.Enqueue(root, shard);
            // 乙太網移動也會過一次讀取畫面。等不到就繼續往下走(退回「從目前位置走過去」),
            // 不要讓整條佇列中斷。
            // ⚠️ 路線若決定「用走的」(RouteUsesAethernet=false)就根本不會有讀取畫面,直接放行,
            // 否則這裡會空轉滿 15 秒。
            P.TaskManager.Enqueue(
                () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                "GotoWaitAethernetTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else if(hasGatewayRoute)
        {
            PluginLog.Information($"[Goto] {name}: {ExcelTerritoryHelper.GetName(territory)} has no aetheryte of its own, entering through the menu of aetheryte {gatewayRoot}.");
            // 沿用既有的「傳送到玄關乙太之光 → 互動 → 選專用選單項」整套流程
            // (蒼天街走「傳送到蒼天街」;渴望灣／行星法恩娜走 /li cosmic／phaenna 的直達流程,沒到就中止佇列)。
            // 這裡用 Enqueue 排到佇列尾端是對的:此刻佇列裡還沒有這條路線的後續步驟,
            // 下面的等待與導航都是接在它之後才排進去的。
            TaskAetheryteAethernetTeleport.Enqueue(gatewayRoot, gatewayAethernet);
            // 進去是一整段區域轉場(可能還夾一個副本區選單),要等真的抵達目的區域再往下走。
            // ⚠️ 這一步刻意讓逾時中止整條佇列:沒到對的區域就開始導航,會在錯的地圖上亂走。
            P.TaskManager.Enqueue(() => P.Territory == territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
                "GotoWaitGatewayArrival", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else
        {
            if(P.Territory == territory)
            {
                PluginLog.Information($"[Goto] {name}: 已在目標區域，不需要傳送。");
            }
            else
            {
                var aetheryte = FindClosestUnlockedAetheryte(territory, routingPos);
                var distance = TryGetDistanceToDestination(aetheryte, routingPos, out var d) ? $"{d:F0}" : "?";
                PluginLog.Information($"[Goto] {name}: 路線＝傳送到乙太之光 {TravelDiag.AetheryteName(aetheryte)}({aetheryte})，離目標 {distance} 米（水平）。");
            }
            var wait = new ZoneWait();
            P.TaskManager.Enqueue(() => TeleportToDestinationZone(territory, routingPos, name, wait), "GotoTeleportToZone", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        return true;
    }

    /// <summary>一趟「等抵達目標區域」的診斷狀態，只在狀態轉換時寫 log。</summary>
    internal sealed class ZoneWait
    {
        public long Start;
        public string State = "";
        public int Sent;
    }

    internal static bool TeleportToDestinationZone(uint territory, Vector3 routingPos, string name, ZoneWait wait)
    {
        if(wait.Start == 0) wait.Start = Environment.TickCount64;
        if(P.Territory == territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas])
        {
            PluginLog.Information($"[Goto] {name}: 已在目標區域 {territory}，傳送這一步完成（等了 {(Environment.TickCount64 - wait.Start) / 1000.0:F1} 秒、送出傳送 {wait.Sent} 次）。");
            return true;
        }
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || Svc.Condition[ConditionFlag.Casting])
        {
            NoteZoneWait(wait, name, Svc.Condition[ConditionFlag.Casting] ? "詠唱中" : "讀取畫面中");
            return false;
        }
        if(!Player.Interactable)
        {
            NoteZoneWait(wait, name, "角色不可互動");
            return false;
        }
        var id = FindClosestUnlockedAetheryte(territory, routingPos);
        if(id == 0)
        {
            // Enqueue 已預先驗證過,理論上到不了這裡;真的發生(狀態在途中改變)也不丟例外,
            // 印聊天欄錯誤並整條中止,免得後續的導航步驟在錯的區域亂走。
            PluginLog.Information($"[Goto] {name}: 中止——目標區域 {territory} 已經找不到已解鎖的乙太之光。");
            ChatQueue.Red($"[Lifestream] {LocText.CannotReachDestinationNoAetheryte.Loc()} {name} ({ExcelTerritoryHelper.GetName(territory)})");
            P.TaskManager.Abort();
            return true;
        }
        if(EzThrottler.Throttle("GotoTeleport", 5000))
        {
            if(S.TeleportService.TeleportToAetheryte(id)) wait.Sent++;
            // 送出之後重新記一次「詠唱中」，被打斷又重送時才看得出來。
            wait.State = "";
        }
        return false;
    }

    private static void NoteZoneWait(ZoneWait wait, string name, string state)
    {
        if(wait.State == state) return;
        wait.State = state;
        PluginLog.Information($"[Goto] {name}: 等待抵達目標區域——{state}（已等 {(Environment.TickCount64 - wait.Start) / 1000.0:F1} 秒）。");
    }

    /// <summary>
    /// 找出目的區域裡離目標點最近的「城內以太之光」(aethernet shard)及其所屬主水晶。
    /// 只有在它確實比「直接傳送到該區主水晶」明顯更近時才回傳 true。
    /// 該區只有城內以太之光、沒有主水晶的區域(例如格里達尼亞舊街、烏爾達哈太陽神草原側)也由這裡處理。
    /// 📌 <paramref name="routingPos"/> 只有 XZ 會被用到(見 <see cref="DistanceXZ"/>),
    ///    所以地圖點那種「Y 恆為 0」的座標餵進來也是正確的。
    /// </summary>
    internal static bool TryFindAethernetRoute(uint territory, Vector3 routingPos, string name, out TinyAetheryte root, out TinyAetheryte shard)
    {
        root = default;
        shard = default;
        var bestDist = float.MaxValue;

        foreach(var (master, children) in S.Data.DataStore.Aetherytes)
        {
            // 要先能傳送到主水晶,才有辦法接以太之光網路
            if(!IsAetheryteUnlocked(master.ID)) continue;
            foreach(var child in children)
            {
                // 同一個以太之光網路可能橫跨多個區域,只比對跟目標點同區的
                if(child.TerritoryType != territory) continue;
                // 選單上不會出現的(飛空艇著陸場之類的隱藏節點)不能選
                if(child.Invisible) continue;
                if(!IsAetheryteUnlocked(child.ID)) continue;
                if(!TryGetDistanceToDestination(child.ID, routingPos, out var dist)) continue;
                if(dist < bestDist)
                {
                    bestDist = dist;
                    root = master;
                    shard = child;
                }
            }
        }
        if(bestDist == float.MaxValue) return false;

        // 跟「直接傳送到該區主水晶再走過去」比較。
        // 找不到主水晶(direct == 0)代表該區只有城內以太之光,那就一定走以太之光網路。
        var direct = FindClosestUnlockedAetheryte(territory, routingPos);
        if(direct != 0 && TryGetDistanceToDestination(direct, routingPos, out var directDist)
            && bestDist + AethernetGainThreshold >= directDist)
        {
            PluginLog.Information($"[Goto] {name}: aethernet {shard.Name} ({bestDist:F0}) not meaningfully closer than aetheryte ({directDist:F0}), teleporting directly");
            return false;
        }

        // 已經在目的區域,而且人本來就比那個以太之光更靠近目標點 —— 直接走過去就好
        if(P.Territory == territory && Player.Available && DistanceXZ(Player.Position, routingPos) <= bestDist)
        {
            PluginLog.Information($"[Goto] {name}: already closer than aethernet {shard.Name}, walking directly");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 是否已解鎖(含城內以太之光)。這只是讀 UIState 裡的解鎖點陣圖,
    /// 沒有特徵碼、沒有 hook,Questionable 也是用同一個方法判斷城內以太之光。
    /// 📌 <see cref="TaskTeleportPanelGo"/> 的落點中繼用的是同一份判斷,所以是 internal 不是 private ——
    /// 「解鎖了沒」各寫一份是最容易走散的那種重複。
    /// </summary>
    internal static bool IsAetheryteUnlocked(uint aetheryteId)
    {
        var uiState = UIState.Instance();
        return uiState != null && uiState->IsAetheryteUnlocked(aetheryteId);
    }

    /// <summary>
    /// ECommons 的 <see cref="ECommons.GameHelpers.Map.AetherytePosition(uint)"/> 對沒有 Level 資料的
    /// 乙太之光會全表掃描 MapMarker,而 TeleportToDestinationZone 是逐幀重試的任務,
    /// 每幀掃全表太浪費。座標是靜態遊戲資料,永久快取(null=解析不到,也快取避免重複丟例外)。
    /// </summary>
    private static readonly Dictionary<uint, Vector3?> AetherytePositionCache = [];

    private static bool TryGetDistanceToDestination(uint aetheryteId, Vector3 routingPos, out float distance)
    {
        distance = float.MaxValue;
        if(!AetherytePositionCache.TryGetValue(aetheryteId, out var pos))
        {
            try
            {
                pos = ECommons.GameHelpers.Map.AetherytePosition(aetheryteId);
            }
            catch(Exception e)
            {
                PluginLog.Debug($"[Goto] Could not resolve position of aetheryte {aetheryteId}: {e.Message}");
                pos = null;
            }
            AetherytePositionCache[aetheryteId] = pos;
        }
        if(pos == null) return false;
        distance = DistanceXZ(pos.Value, routingPos);
        return true;
    }

    /// <summary>
    /// 只比水平距離:以太之光座標多半是從地圖標記換算來的,Y 恆為 0,
    /// 把 Y 算進去會讓不同來源的座標無法公平比較。
    /// </summary>
    private static float DistanceXZ(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// 在目的區域的已解鎖主水晶中,挑距離目標點最近者。
    /// </summary>
    internal static uint FindClosestUnlockedAetheryte(uint territory, Vector3 routingPos)
    {
        uint best = 0;
        var bestDist = float.MaxValue;
        foreach(var x in Svc.AetheryteList)
        {
            var data = x.AetheryteData.ValueNullable;
            if(data == null || !data.Value.IsAetheryte) continue;
            if(data.Value.Territory.RowId != territory) continue;
            if(!TryGetDistanceToDestination(x.AetheryteId, routingPos, out var dist)) continue;
            if(dist < bestDist)
            {
                bestDist = dist;
                best = x.AetheryteId;
            }
        }
        return best;
    }

    internal static bool IsVnavmeshLoaded() => Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded);

    /// <summary>
    /// 這個區域現在能不能飛(純資料判斷,不需要人在場、也不用試起飛)。
    /// 先例:<c>AutoDuty/Helpers/MovementHelper.IsFlyingSupported</c>。
    /// ⚠️ 那邊接受 <c>TerritoryIntendedUse</c> 1/47/49;這裡刻意只認 **1**(一般野外)——
    ///    47/49 在台服到底准不准飛沒有離線證據,而**猜錯的代價不對稱**:
    ///    少飛只是走得慢;多飛則是在不能飛的地方按跳躍、vnavmesh 給出一條空中路徑,
    ///    而角色其實動不了。要放寬,先要有實機證據。
    /// </summary>
    internal static bool IsFlyingUnlockedIn(uint territory)
    {
        if(territory == 0) return false;
        if(!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)) return false;
        if(row.TerritoryIntendedUse.RowId != 1) return false;
        var flgSet = row.AetherCurrentCompFlgSet.RowId;
        if(flgSet == 0) return false;
        var playerState = PlayerState.Instance();
        // 取得器合法回 null(未登入/正在切角色);拿不到就當成不能飛(fail-closed)。
        return playerState != null && playerState->IsAetherCurrentZoneComplete(flgSet);
    }

    /// <summary>一趟 <see cref="EnqueueToMapPoint"/> 的狀態。任務之間用閉包共享,不跨趟共用。</summary>
    private sealed class MapPointRun
    {
        public MapPointDestination Dest = new();
        /// <summary>呼叫端要求的原值;實際飛不飛在落點解析後由 <see cref="FieldFlightDecision.ShouldFlyMapPoint"/> 定案。</summary>
        public bool RequestedFly;
        /// <summary>這一趟會不會經過讀取畫面(＝需不需要做 navmesh 的 stale 閘門)。</summary>
        public bool Zoned;
        public bool SawNavNotReady;
        public long FreshGateStart;
    }

    /// <summary>離目標還有這麼遠卻「已經沒在走」,才算飛行路線失敗(不然一兩點的短路徑會被誤判)。</summary>
    private const float FlyPathFailureDistance = FieldFlightDecision.DistanceFloor;
}
