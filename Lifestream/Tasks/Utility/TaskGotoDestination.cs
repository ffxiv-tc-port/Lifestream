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
/// 紅線:不做任何記憶體改座標瞬移(DR 版的 TPSmart/TPPlayerAddress 不抄)。
/// </summary>
/// <remarks>📌 兩個進入點共用同一段「抵達目的區域」的編排(<see cref="EnqueueTravelToZone"/>):
///    <list type="bullet">
///    <item><see cref="Enqueue(CustomDestination)"/> —— 使用者存下來的自訂落點(有真實三維座標)。</item>
///    <item><see cref="EnqueueToMapPoint(MapPointDestination, bool)"/> —— 別的外掛透過
///          <c>Lifestream.GoToMapPoint</c> IPC 丟進來的地圖上一點(只有 XZ,高度抵達後才問 vnavmesh)。</item>
///    </list>
/// </remarks>
public static unsafe class TaskGotoDestination
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
            // (蒼天街走「傳送到蒼天街」,渴望灣走「前往渴望灣」+ 需要時再選副本區)。
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

    /// <summary>
    /// 轉場後等 navmesh 真的換成**這個區域**的網格。詳見 <see cref="NavStaleObserveMs"/> 的說明。
    /// </summary>
    /// <returns>null = 中止整條佇列(vnavmesh 整個叫不動,它自己已經印過一次聊天欄錯誤)。</returns>
    private static bool? WaitNavmeshFresh(MapPointRun run)
    {
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || !Player.Interactable)
        {
            // 又進了一次轉場(也可能是使用者自己動的),前面的觀察全部作廢重來。
            run.Zoned = true;
            run.SawNavNotReady = false;
            run.FreshGateStart = 0;
            return false;
        }
        if(P.Territory != run.Dest.Territory) return false;

        // ⚠️ 刻意不寫成 `IsReady() == true`:它在 IPC 整個叫不動時回 null(同時自己印一次錯誤),
        //    而 null 在 NeoTaskManager 的語意是「中止」。包成 == true 會讓這一步空轉到逾時,
        //    而 IsReady 每一幀都再印一次聊天欄錯誤 —— 一次故障洗出上千行。
        var ready = S.Ipc.VnavmeshIPC.IsReady();
        if(ready == null) return null;
        if(ready == false)
        {
            run.SawNavNotReady = true;
            return false;
        }
        if(!run.Zoned || run.SawNavNotReady)
        {
            PluginLog.Information($"[Goto] {run.Dest.Name}: 目標區域的 vnavmesh 網格已就緒（{(run.Zoned ? "轉場後有觀察到重建" : "沒有經過讀取畫面")}）。");
            return true;
        }

        if(run.FreshGateStart == 0) run.FreshGateStart = Environment.TickCount64;
        var waited = Environment.TickCount64 - run.FreshGateStart;
        if(waited < NavStaleObserveMs + NavStaleFallbackMs) return false;
        PluginLog.Information($"[Goto] 轉場後 {waited}ms 內沒觀察到 vnavmesh 重建網格(可能是同一張網格可重用),改用固定等待後繼續。");
        return true;
    }

    /// <summary>
    /// 抵達目的區域後,把「地圖上的 XZ」解成一個真的站得住的三維落點,然後排最後一段移動。
    /// </summary>
    /// <remarks>
    /// 🔴 一定要解 Y。vnavmesh 的 <c>FindNearestMeshPoly</c> 預設 halfExtentY=5,
    ///    直接拿 Y=0 的地圖座標去尋路,在多數野外地圖會得到**空路徑而且零訊息**。
    /// ⚠️ <c>Query.Mesh.PointOnFloor</c> 的第 2 個參數(allowUnlandable)這裡傳 false。我方 vnavmesh fork 的 <c>IPCProvider.cs</c> 目前刻意**忽略**
    ///    這個參數,並在該處列了「傳 false 的消費端」清單;那份清單還沒有 Lifestream ——
    ///    若日後 vnavmesh 真的把這個參數接上去,這裡會開始拿到 null 並靜默拒絕出發,
    ///    屆時要連同那份清單一起處理。
    /// </remarks>
    private static bool ResolveFloorAndGo(MapPointRun run)
    {
        var dest = run.Dest;
        if(P.Territory != dest.Territory)
        {
            PluginLog.Information($"[Goto] 地圖點中止：人在區域 {P.Territory}，不是目標區域 {dest.Territory}。");
            TravelDiag.Record($"地圖點中止：沒到目標區域 {dest.Territory}");
            ChatQueue.Red($"[Lifestream] {"Could not reach the target zone.".Loc()} ({ExcelTerritoryHelper.GetName(dest.Territory)})");
            P.TaskManager.Abort();
            return true;
        }
        var ready = S.Ipc.VnavmeshIPC.IsReady();
        if(ready != true)
        {
            PluginLog.Information($"[Goto] 地圖點中止：vnavmesh 網格還沒就緒（IsReady={ready?.ToString() ?? "null，IPC 叫不動"}、建置進度 {S.Ipc.VnavmeshIPC.BuildProgress():F2}）。");
            TravelDiag.Record("地圖點中止：vnavmesh 網格沒就緒");
            ChatQueue.Red($"[Lifestream] {"vnavmesh has not finished building the navigation mesh of this zone yet.".Loc()}");
            P.TaskManager.Abort();
            return true;
        }

        var probe = new Vector3(dest.WorldX, FloorProbeAltitude, dest.WorldZ);
        // PointOnFloor 是 SafeWrapper.AnyException 包過的:vnavmesh 中途消失也只是回 null,不會擲例外。
        var floor = S.Ipc.VnavmeshIPC.PointOnFloor(probe, false, FloorProbeHalfExtentXZ);
        if(floor == null)
        {
            // 地圖點擊本來就有幾碼誤差,放寬一次再試 —— 這比直接放棄有用得多。
            floor = S.Ipc.VnavmeshIPC.PointOnFloor(probe, false, FloorProbeHalfExtentXZWide);
            if(floor != null) PluginLog.Information($"[Goto] 地圖點 XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) 在 {FloorProbeHalfExtentXZ} 米內沒有地板,放寬到 {FloorProbeHalfExtentXZWide} 米後找到。");
        }
        if(floor == null)
        {
            PluginLog.Information($"[Goto] 地圖點中止：XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) 在 {FloorProbeHalfExtentXZWide} 米內找不到可站的地板（PointOnFloor 兩次都回 null）。");
            TravelDiag.Record("地圖點中止：找不到地板");
            ChatQueue.Red($"[Lifestream] {"There is no ground to stand on below that spot.".Loc()}");
            P.TaskManager.Abort();
            return true;
        }

        PluginLog.Information($"[Goto] 地圖點落點解析完成:XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) ⇒ {floor.Value:F1}。");
        var territory = P.Territory;
        var flyable = IsFlyingUnlockedIn(territory);
        var distance = Player.Available ? DistanceXZ(Player.Position, floor.Value) : float.NaN;
        var heightDiff = Player.Available ? floor.Value.Y - Player.Position.Y : float.NaN;
        var fly = FieldFlightDecision.ShouldFlyMapPoint(run.RequestedFly, flyable, Svc.Condition[ConditionFlag.Diving],
            Svc.Condition[ConditionFlag.InCombat], Svc.Condition[ConditionFlag.InFlight], distance, heightDiff, C.GotoAutoFlyMinDistance, out var reason);
        PluginLog.Information($"[Goto] 地圖點: 最後一段用{(fly ? "飛行" : "地面")}——{reason}"
            + $"（區域 {territory} 可飛={flyable}、水平距離 {distance:F1} 米、高低差 {heightDiff:F1} 米、門檻 {FieldFlightDecision.EffectiveThreshold(C.GotoAutoFlyMinDistance):F0} 米、要求飛行={run.RequestedFly}）。");
        // 📌 此刻佇列是空的(本任務是 EnqueueToMapPoint 排的最後一個),所以 Enqueue 等同 Insert。
        EnqueueNavTo(floor.Value, fly);
        return true;
    }

    /// <summary>
    /// 使用者自己的野外目的地(自訂落點、<c>/li goto</c>)的最後一段:依 <see cref="FieldFlightDecision"/> 選飛或走。
    /// 必須在角色已抵達目標區域的那一刻呼叫(區域與距離都取當下值)。
    /// </summary>
    internal static void EnqueueFieldNavTo(Vector3 point, string source)
    {
        var territory = P.Territory;
        var flyable = IsFlyingUnlockedIn(territory);
        var distance = Player.Available ? DistanceXZ(Player.Position, point) : float.NaN;
        var fly = FieldFlightDecision.ShouldFly(C.GotoAutoFly, flyable, Svc.Condition[ConditionFlag.Diving],
            Svc.Condition[ConditionFlag.InCombat], Svc.Condition[ConditionFlag.InFlight], distance, C.GotoAutoFlyMinDistance, out var reason);
        PluginLog.Information($"[Goto] {source}: 最後一段用{(fly ? "飛行" : "地面")}——{reason}"
            + $"（區域 {territory} 可飛={flyable}、水平距離 {distance:F1} 米、門檻 {FieldFlightDecision.EffectiveThreshold(C.GotoAutoFlyMinDistance):F0} 米、開關={C.GotoAutoFly}）。");
        EnqueueNavTo(point, fly);
    }

    /// <summary>最後一段移動。<paramref name="fly"/>=true 時走飛行編排,起飛失敗會自動退回地面。</summary>
    internal static void EnqueueNavTo(Vector3 point, bool fly = false)
    {
        TravelDiag.Record($"排入最後一段移動 ⇒ {point:F1}（{(fly ? "飛行" : "地面")}）");
        P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady, "GotoWaitNavReady", new(timeLimitMS: 120000));
        if(fly)
        {
            EnqueueFlyNavTo(point);
        }
        else
        {
            EnqueueGroundNavTo(point);
        }
    }

    /// <summary>地面路線:自己算路徑、自己跟(Lifestream 的 FollowPath)。</summary>
    private static void EnqueueGroundNavTo(Vector3 point)
    {
        P.TaskManager.Enqueue(() =>
        {
            var from = Player.Position;
            var task = S.Ipc.VnavmeshIPC.Pathfind(from, point, false);
            PluginLog.Information(task == null
                ? "[Goto] 地面導航：vnavmesh Nav.Pathfind 沒有回傳工作（IPC 呼叫失敗），下一步會以例外中止。"
                : $"[Goto] 地面導航：向 vnavmesh 要路徑 {from:F1} ⇒ {point:F1}（直線 {Vector3.Distance(from, point):F1} 米）。");
            P.TaskManager.Enqueue(() =>
            {
                if(!task.IsCompleted) return false;
                LogGroundPath(task, point);
                var path = task.Result;
                var mount = ShouldMountForPath(path);
                // ⚠️ 上坐騎這一步刻意 abortOnTimeout:false —— 召喚不出來(卡動畫、區域其實不准騎)
                // 時只放棄坐騎、照樣用走的，不能因此把整條落點/導航佇列中止掉。
                P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(mount), "GotoUseSprint",
                    new(timeLimitMS: 20000, abortOnTimeout: false));
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. path], true));
                return true;
            }, "GotoBuildPath");
            return true;
        }, "GotoNavmeshMaster");
    }

    /// <summary>只寫 log，不改變流程：尋路失敗時後面的 task.Result 照樣擲例外中止。</summary>
    private static void LogGroundPath(Task<List<Vector3>> task, Vector3 target)
    {
        if(task.IsFaulted || task.IsCanceled)
        {
            PluginLog.Information($"[Goto] 地面導航：vnavmesh 尋路失敗（faulted={task.IsFaulted}、canceled={task.IsCanceled}：{task.Exception?.InnerException?.Message ?? task.Exception?.Message}），這一步會以例外中止整條佇列。");
            return;
        }
        var path = task.Result;
        if(path == null)
        {
            PluginLog.Information("[Goto] 地面導航：vnavmesh 回傳 null 路徑，下一步會以例外中止。");
            return;
        }
        if(path.Count == 0)
        {
            PluginLog.Information($"[Goto] 地面導航：vnavmesh 回傳空路徑（目標 {target:F1} 可能不在網格上），角色不會移動。");
            return;
        }
        var length = 0f;
        var prev = Player.Position;
        foreach(var p in path)
        {
            length += Vector3.Distance(prev, p);
            prev = p;
        }
        PluginLog.Information($"[Goto] 地面導航：路徑 {path.Count} 個航點、全長 {length:F0} 米，終點 {path[^1]:F1}（離目標 {Vector3.Distance(path[^1], target):F1} 米）。");
    }

    /// <summary>
    /// 飛行路線:上坐騎 → 起飛 → 交給 vnavmesh 的 <c>SimpleMove.PathfindAndMoveTo(point, true)</c>。
    /// 🔴 起飛失敗一定要退回地面重算,這不是保險而是必要:vnavmesh 的 FollowPath 在
    ///    「路徑要求飛行、角色卻沒上坐騎」時是 <c>_movement.Enabled = false; return;</c> ——
    ///    角色會**站在原地不動、沒有任何訊息**,使用者只看得到「按了沒反應」。
    /// 📌 這條路用的是 vnavmesh 自己的 FollowPath(不是 Lifestream 的),所以要排一個
    ///    「等 Path.IsRunning 變 false」的任務,<c>Lifestream.IsBusy()</c> 才會誠實。
    /// </summary>
    private static void EnqueueFlyNavTo(Vector3 point, FlightYieldGate gate = null)
    {
        gate ??= new();
        // ⚠️ abortOnTimeout:false —— 召喚不出坐騎不代表要中止整趟,下面的分流會改用地面路線。
        P.TaskManager.Enqueue(TaskMount.MountIfCan, "GotoFlyMount", new(timeLimitMS: 20000, abortOnTimeout: false));
        // 🔴 FlyIfCan 在不可飛區域回 null,而 NeoTaskManager 的 null = 中止整條佇列。
        //    一定要用包過的版本,否則「勾了飛行卻在不能飛的地方」會整條靜默斷掉。
        P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp, "GotoFlyTakeoff", new(timeLimitMS: 15000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            if(!Svc.Condition[ConditionFlag.InFlight])
            {
                PluginLog.Information("[Goto] 起飛沒有成功(不可飛/沒上到坐騎/被打斷),改用地面路線。");
                ChatQueue.Green($"[Lifestream] {"Could not take off - walking to the destination instead.".Loc()}");
                EnqueueGroundNavTo(point);
                return true;
            }
            PluginLog.Information($"[Goto] 已在空中,交給 vnavmesh 飛往 {point:F1}。");
            P.TaskManager.Enqueue(() => Movement.VnavMoveOwner.PathfindAndMoveTo(point, true), "GotoFlyStartMove",
                new(timeLimitMS: 30000, abortOnTimeout: false));
            P.TaskManager.Enqueue(() => WaitFlyPathfindThenFollow(point, gate), "GotoFlyWaitPathfind",
                new(timeLimitMS: 120000, abortOnTimeout: false));
            return true;
        }, "GotoFlyDecide");
    }

    /// <summary>
    /// 等 vnavmesh 把飛行路線算完,再決定是「跟著它走完」還是「退回地面重算」。
    /// </summary>
    /// <remarks>
    /// 📌 <c>AsyncMoveRequest.Update</c> 是在同一次呼叫裡把 TaskInProgress 清掉並呼叫 FollowPath.Move 的,
    ///    所以「TaskInProgress 剛變 false」的那一幀,IsRunning 已經反映了結果,兩者之間沒有空窗。
    /// ⚠️ 目標很近時路徑可能只有一兩個點、一瞬間就走完,那也會看到 IsRunning=false ——
    ///    所以只有在**人還離目標很遠**時才判定成失敗,否則會白跑一次地面路線。
    /// </remarks>
    private static bool WaitFlyPathfindThenFollow(Vector3 point, FlightYieldGate gate)
    {
        if(S.Ipc.VnavmeshIPC.PathfindInProgress()) return false;
        if(S.Ipc.VnavmeshIPC.IsRunning())
        {
            P.TaskManager.Enqueue(() => WaitFlyArrival(point, gate), "GotoFlyWaitArrival",
                new(timeLimitMS: 600000, abortOnTimeout: false));
            return true;
        }
        Movement.VnavMoveOwner.Release();
        if(Player.Available)
        {
            var remaining = DistanceXZ(Player.Position, point);
            if(remaining > FlyPathFailureDistance)
            {
                PluginLog.Information($"[Goto] vnavmesh 沒有給出飛行路線(離目標還有 {remaining:F0} 米),改用地面路線。");
                ChatQueue.Green($"[Lifestream] {"No flight path was found - walking to the destination instead.".Loc()}");
                EnqueueGroundNavTo(point);
            }
            else
            {
                PluginLog.Information($"[Goto] vnavmesh 已不在移動，離目標 {remaining:F1} 米（{FlyPathFailureDistance} 米內，視為已抵達）。");
                InsertLand();
            }
        }
        else
        {
            MarkNotArrived();
        }
        return true;
    }

    /// <summary>
    /// 跟著 vnavmesh 飛到停下為止。停在半路而且使用者剛按過移動鍵＝手動中斷：讓位，停手後從目前位置接續
    /// （上限見 <see cref="FlightYieldGate"/>）；抵達就降落。沒抵達的那一趟不念抵達播報。
    /// </summary>
    private static bool WaitFlyArrival(Vector3 point, FlightYieldGate gate)
    {
        var navigating = S.Ipc.VnavmeshIPC.IsRunning() || S.Ipc.VnavmeshIPC.PathfindInProgress();
        if(!Player.Available)
        {
            if(navigating) return false;
            Movement.VnavMoveOwner.Release();
            PluginLog.Information("[Goto] vnavmesh 停止飛行移動（角色目前不可用，量不到距離）。");
            MarkNotArrived();
            return true;
        }
        var remaining = DistanceXZ(Player.Position, point);
        var ev = gate.Observe(Environment.TickCount64, navigating, Svc.Condition[ConditionFlag.Mounted],
            MoveInputProbe.Pressed(), Player.Position, remaining, FlyPathFailureDistance);
        // 讓位中 vnavmesh 可能還在跑我們的路線，旗標留著；其餘結果＝這一趟已結束或交給別人（接續會重新掛上）。
        if(ev is not (FlightWatchEvent.None or FlightWatchEvent.YieldStarted)) Movement.VnavMoveOwner.Release();
        switch(ev)
        {
            case FlightWatchEvent.None:
                return false;
            case FlightWatchEvent.Arrived:
                PluginLog.Information($"[Goto] vnavmesh 停止飛行移動：離目標 {remaining:F1} 米（{FlyPathFailureDistance} 米內，視為已抵達）。");
                InsertLand();
                return true;
            case FlightWatchEvent.StoppedShort:
                PluginLog.Information($"[Goto] vnavmesh 停止飛行移動：離目標還有 {remaining:F1} 米（水平），沒有偵測到手動操作，不接續。");
                MarkNotArrived();
                return true;
            case FlightWatchEvent.YieldStarted:
                PluginLog.Information($"[Goto] 飛行被手動操作中斷（離目標 {remaining:F1} 米）：讓位，停手 {FlightYieldGate.ResumeQuietMs / 1000.0:F0} 秒後從目前位置接續（已接續 {gate.Resumes}/{FlightYieldGate.MaxResumes} 次）。");
                return false;
            case FlightWatchEvent.Resume:
                PluginLog.Information($"[Goto] 使用者已停手：從目前位置接續飛往 {point:F1}（離目標 {remaining:F1} 米，第 {gate.Resumes}/{FlightYieldGate.MaxResumes} 次接續）。");
                P.TaskManager.InsertStack(() => EnqueueFlyNavTo(point, gate));
                return true;
            case FlightWatchEvent.GaveUp:
                PluginLog.Information($"[Goto] 放棄接續飛行：{gate.GiveUpReason}（離目標 {remaining:F1} 米），交給使用者。");
                MarkNotArrived();
                return true;
            default:
                PluginLog.Information($"[Goto] 讓位期間 vnavmesh 又開始移動（別的插件或使用者送了新路線）：這一趟交給它、不再接續（離目標 {remaining:F1} 米）。");
                MarkNotArrived();
                return true;
        }
    }

    /// <summary>這一趟沒走到：讓收尾的抵達播報閉嘴（它看的就是這個旗標）。</summary>
    private static void MarkNotArrived()
    {
        if(P.followPath != null) P.followPath.LastMovementFailed = true;
    }

    /// <summary>
    /// 抵達後降落，不下坐騎（野外導航沒有「抵達後下坐騎」的設定；房區那個只管房區）。
    /// 在空中且動作可用時送一般動作 23（跳下），落地（InFlight 消失）即完成；重送與否見 <see cref="LandRetryGate"/>
    /// （跳下在地面騎乘時＝下坐騎，所以降落途中不重送）。
    /// </summary>
    private static void InsertLand()
    {
        if(!Svc.Condition[ConditionFlag.InFlight]) return;
        var gate = new LandRetryGate();
        P.TaskManager.Insert(() =>
        {
            if(!Svc.Condition[ConditionFlag.InFlight])
            {
                PluginLog.Information($"[Goto] 已降落（送出降落 {gate.Sends} 次）。");
                return true;
            }
            if(!Player.Available) return false;
            var now = Environment.TickCount64;
            var y = Player.Position.Y;
            if(!gate.ShouldSend(now, y)) return false;
            var am = ActionManager.Instance();
            if(am == null || am->GetActionStatus(ActionType.GeneralAction, 23) != 0)
            {
                gate.OnNotReady(now, y);
                return false;
            }
            var accepted = am->UseAction(ActionType.GeneralAction, 23);
            gate.OnSent(now, y);
            if(gate.Sends == 1) PluginLog.Information($"[Goto] 抵達目標上空：送出降落（遊戲回傳 {accepted}）。");
            else PluginLog.Information($"[Goto] 仍在空中、高度 {LandRetryGate.StallWindowMs / 1000.0:F1} 秒內沒在下降：再送一次降落（第 {gate.Sends}/{LandRetryGate.MaxSends} 次，遊戲回傳 {accepted}）。");
            return false;
        }, "GotoFlyLand", new(timeLimitMS: 10000, abortOnTimeout: false));
    }

    /// <summary>離目標還有這麼遠卻「已經沒在走」,才算飛行路線失敗(不然一兩點的短路徑會被誤判)。</summary>
    private const float FlyPathFailureDistance = FieldFlightDecision.DistanceFloor;

    /// <summary>
    /// 這一段路值不值得上坐騎。
    /// 📌 「能不能騎」不自己判斷 —— <see cref="TaskMount.MountIfCan"/> 問的是
    /// <c>ActionManager.GetActionStatus(GeneralAction, 9)</c>，那就是遊戲自己對「現在能不能召喚
    /// 坐騎」的答案(區域不准、戰鬥中、室內、沒解鎖…全都涵蓋)，回非 0 時它會直接回 true 放行、不會卡住佇列。
    /// 這裡只補遊戲答不出來的那一半：**值不值得**。
    /// ⚠️ 距離用的是**路徑總長**不是直線距離：要繞路時直線距離會嚴重低估，而那正是最該上坐騎的情況。
    /// </summary>
    private static bool ShouldMountForPath(List<Vector3> path)
    {
        if(!C.GotoUseMount) return false;
        if(path == null || path.Count == 0) return false;
        // 已經在坐騎上就不必再判斷距離：MountIfCan 會立刻回 true，不會有任何額外動作。
        if(Svc.Condition[ConditionFlag.Mounted]) return true;
        // 潛水中上坐騎會先浮上來，等於把使用者拉離原本的路徑
        if(Svc.Condition[ConditionFlag.Diving]) return false;
        if(Svc.Condition[ConditionFlag.InCombat]) return false;

        var length = 0f;
        var prev = Player.Position;
        foreach(var p in path)
        {
            length += DistanceXZ(prev, p);
            prev = p;
        }
        if(length < C.GotoMountMinDistance)
        {
            // 要使用者回報的診斷:「為什麼沒上坐騎」正是他會來問的事。
            PluginLog.Information($"[Goto] Path is {length:F0}y, shorter than the {C.GotoMountMinDistance:F0}y mount threshold - walking.");
            return false;
        }
        PluginLog.Information($"[Goto] Path is {length:F0}y, mounting up before moving.");
        return true;
    }

    internal static void SetFlag(uint territory, Vector3 position, string name)
    {
        // AgentMap 取得器合法回 null;拿不到就不插旗也不開地圖(fail-closed)。
        var agent = AgentMap.Instance();
        if(agent == null) return;
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.RowId ?? 0;
        if(mapId == 0) return;
        agent->SetFlagMapMarker(territory, mapId, position);
        agent->OpenMap(mapId, territory);
        ChatQueue.Green($"[Lifestream] {LocText.VnavmeshNotInstalledFlagged.Loc()} {name}");
    }
}
