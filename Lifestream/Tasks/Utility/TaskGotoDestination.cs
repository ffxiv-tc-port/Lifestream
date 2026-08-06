using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Data;
using Lifestream.Schedulers;
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
public static unsafe class TaskGotoDestination
{
    /// <summary>
    /// 多繞一次城內以太之光要多花十幾秒(互動→開選單→讀取畫面),
    /// 所以要比「直接傳送到主水晶再走」近上這個距離才值得。
    /// 這是取捨用的經驗值,不是實測出來的數字;同時也吸收地圖標記座標本身的誤差。
    /// </summary>
    private const float AethernetGainThreshold = 30f;

    public static void Enqueue(CustomDestination dest)
    {
        var hasAethernetRoute = TryFindAethernetRoute(dest, out var root, out var shard);

        // 蒼天街、渴望灣這類區域沒有自己的乙太之光,是靠鄰近區域某座乙太之光的選單項進去的。
        // 對它們 FindClosestUnlockedAetheryte 永遠回 0,修正前會直接回報「目標區域沒有已解鎖的
        // 乙太之光」就放棄——但 Lifestream 其實早就把這兩條路建模好了(只是只有別名/浮動視窗在用)。
        uint gatewayRoot = 0;
        uint gatewayAethernet = 0;
        var hasGatewayRoute = !hasAethernetRoute
            && P.Territory != dest.Territory
            && TaskAetheryteAethernetTeleport.TryGetGatewayRoute(dest.Territory, out gatewayRoot, out gatewayAethernet);

        // 先驗證目的地可達再排任務:走不到就在聊天欄講清楚,不丟例外。
        // (已在目的區域時不需要傳送點,TeleportToDestinationZone 會直接回 true)
        if(!hasAethernetRoute && !hasGatewayRoute && P.Territory != dest.Territory && FindClosestUnlockedAetheryte(dest) == 0)
        {
            ChatPrinter.Red($"[Lifestream] {"Cannot reach destination - no unlocked aetheryte in target zone:".Loc()} {dest.Name} ({ExcelTerritoryHelper.GetName(dest.Territory)})");
            return;
        }

        if(hasAethernetRoute)
        {
            PluginLog.Information($"[Goto] {dest.Name}: using aethernet route {root.Name}({root.ID}) -> {shard.Name}({shard.ID})");
            // 需要的話先接近可用的乙太之光節點(優先用身邊摸得到的同網路節點),再互動並用乙太網跳到
            // 目標城內乙太之光。這一整套現在跟傳送面板/地圖點擊/「/li <地名>」共用同一份
            // (見 <see cref="TaskAethernetRoute"/>),不再是兩份各自演化的複製品。
            TaskAethernetRoute.Enqueue(root, shard);
            // 乙太網移動也會過一次讀取畫面。等不到就繼續往下走(退回「從目前位置走過去」),
            // 不要讓整條佇列中斷 —— 最差情況等同修正前的行為,不會比原本更糟。
            // ⚠️ 路線若決定「用走的」(RouteUsesAethernet=false)就根本不會有讀取畫面,直接放行,
            // 否則這裡會空轉滿 15 秒。
            P.TaskManager.Enqueue(
                () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                "GotoWaitAethernetTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else if(hasGatewayRoute)
        {
            PluginLog.Information($"[Goto] {dest.Name}: {ExcelTerritoryHelper.GetName(dest.Territory)} has no aetheryte of its own, entering through the menu of aetheryte {gatewayRoot}.");
            // 沿用既有的「傳送到玄關乙太之光 → 互動 → 選專用選單項」整套流程
            // (蒼天街走「傳送到蒼天街」,渴望灣走「前往渴望灣」+ 需要時再選副本區)。
            // 這裡用 Enqueue 排到佇列尾端是對的:此刻佇列裡還沒有這條路線的後續步驟,
            // 下面的等待與導航都是接在它之後才排進去的。
            TaskAetheryteAethernetTeleport.Enqueue(gatewayRoot, gatewayAethernet);
            // 進去是一整段區域轉場(可能還夾一個副本區選單),要等真的抵達目的區域再往下走。
            // ⚠️ 這一步刻意讓逾時中止整條佇列:沒到對的區域就開始導航,會在錯的地圖上亂走。
            P.TaskManager.Enqueue(() => P.Territory == dest.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
                "GotoWaitGatewayArrival", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else
        {
            P.TaskManager.Enqueue(() => TeleportToDestinationZone(dest), "GotoTeleportToZone", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        P.TaskManager.Enqueue(() =>
        {
            if(Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded))
            {
                EnqueueNavTo(dest.Position);
            }
            else
            {
                SetFlag(dest);
            }
            return true;
        }, "GotoNavOrFlag");
    }

    internal static bool TeleportToDestinationZone(CustomDestination dest)
    {
        if(P.Territory == dest.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas]) return true;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || Svc.Condition[ConditionFlag.Casting]) return false;
        if(!Player.Interactable) return false;
        var id = FindClosestUnlockedAetheryte(dest);
        if(id == 0)
        {
            // Enqueue 已預先驗證過,理論上到不了這裡;真的發生(狀態在途中改變)也不丟例外,
            // 印聊天欄錯誤並整條中止,免得後續的導航步驟在錯的區域亂走。
            ChatPrinter.Red($"[Lifestream] {"Cannot reach destination - no unlocked aetheryte in target zone:".Loc()} {dest.Name} ({ExcelTerritoryHelper.GetName(dest.Territory)})");
            P.TaskManager.Abort();
            return true;
        }
        if(EzThrottler.Throttle("GotoTeleport", 5000))
        {
            S.TeleportService.TeleportToAetheryte(id);
        }
        return false;
    }

    /// <summary>
    /// 找出目的區域裡離目標點最近的「城內以太之光」(aethernet shard)及其所屬主水晶。
    /// 只有在它確實比「直接傳送到該區主水晶」明顯更近時才回傳 true。
    /// 這一段就是修正的重點:原本只看 <see cref="Svc.AetheryteList"/> 裡的主水晶,
    /// 城內以太之光完全沒被納入考慮,所以在黃金港之類的城市會從主水晶一路走過去。
    /// 另外也順帶修好「該區只有城內以太之光、沒有主水晶」的區域(例如格里達尼亞舊街、
    /// 烏爾達哈太陽神草原側),那些區域原本會直接丟出 InvalidOperationException。
    /// </summary>
    internal static bool TryFindAethernetRoute(CustomDestination dest, out TinyAetheryte root, out TinyAetheryte shard)
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
                if(child.TerritoryType != dest.Territory) continue;
                // 選單上不會出現的(飛空艇著陸場之類的隱藏節點)不能選
                if(child.Invisible) continue;
                if(!IsAetheryteUnlocked(child.ID)) continue;
                if(!TryGetDistanceToDestination(child.ID, dest, out var dist)) continue;
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
        var direct = FindClosestUnlockedAetheryte(dest);
        if(direct != 0 && TryGetDistanceToDestination(direct, dest, out var directDist)
            && bestDist + AethernetGainThreshold >= directDist)
        {
            PluginLog.Information($"[Goto] {dest.Name}: aethernet {shard.Name} ({bestDist:F0}) not meaningfully closer than aetheryte ({directDist:F0}), teleporting directly");
            return false;
        }

        // 已經在目的區域,而且人本來就比那個以太之光更靠近目標點 —— 直接走過去就好
        if(P.Territory == dest.Territory && Player.Available && DistanceXZ(Player.Position, dest.Position) <= bestDist)
        {
            PluginLog.Information($"[Goto] {dest.Name}: already closer than aethernet {shard.Name}, walking directly");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 是否已解鎖(含城內以太之光)。這只是讀 UIState 裡的解鎖點陣圖,
    /// 沒有特徵碼、沒有 hook,Questionable 也是用同一個方法判斷城內以太之光。
    /// </summary>
    private static bool IsAetheryteUnlocked(uint aetheryteId)
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

    private static bool TryGetDistanceToDestination(uint aetheryteId, CustomDestination dest, out float distance)
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
        distance = DistanceXZ(pos.Value, dest.Position);
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
    internal static uint FindClosestUnlockedAetheryte(CustomDestination dest)
    {
        uint best = 0;
        var bestDist = float.MaxValue;
        foreach(var x in Svc.AetheryteList)
        {
            var data = x.AetheryteData.ValueNullable;
            if(data == null || !data.Value.IsAetheryte) continue;
            if(data.Value.Territory.RowId != dest.Territory) continue;
            if(!TryGetDistanceToDestination(x.AetheryteId, dest, out var dist)) continue;
            if(dist < bestDist)
            {
                bestDist = dist;
                best = x.AetheryteId;
            }
        }
        return best;
    }

    internal static void EnqueueNavTo(Vector3 point)
    {
        P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady, "GotoWaitNavReady", new(timeLimitMS: 120000));
        P.TaskManager.Enqueue(() =>
        {
            var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, point, false);
            P.TaskManager.Enqueue(() =>
            {
                if(!task.IsCompleted) return false;
                var path = task.Result;
                var mount = ShouldMountForPath(path);
                // ⚠️ 上坐騎這一步刻意 abortOnTimeout:false —— 召喚不出來(卡動畫、區域其實不准騎)
                // 時只放棄坐騎、照樣用走的，不能因此把整條落點/導航佇列中止掉。
                P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(mount), "GotoUseSprint",
                    new(timeLimitMS: 20000, abortOnTimeout: false));
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. path], true));
                return true;
            }, "GotoBuildPath");
        }, "GotoNavmeshMaster");
    }

    /// <summary>
    /// 這一段路值不值得上坐騎。
    ///
    /// 📌 「能不能騎」不自己判斷 —— <see cref="TaskMount.MountIfCan"/> 問的是
    /// <c>ActionManager.GetActionStatus(GeneralAction, 9)</c>，那就是遊戲自己對「現在能不能召喚
    /// 坐騎」的答案(區域不准、戰鬥中、室內、沒解鎖…全都涵蓋)，而且回非 0 時它會直接回 true 放行，
    /// 不會卡住佇列。自己再寫一份區域白名單只會多一份會過期的資料。
    /// 這裡只補遊戲答不出來的那一半：**值不值得**。
    ///
    /// ⚠️ 距離用的是**路徑總長**不是直線距離：要繞路的時候直線距離會嚴重低估，
    /// 而那正是最該上坐騎的情況。
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
            // 使用者跑 LogLevel 2 —— 「為什麼沒上坐騎」正是他會來問的事，寫 Debug 他收不到。
            PluginLog.Information($"[Goto] Path is {length:F0}y, shorter than the {C.GotoMountMinDistance:F0}y mount threshold - walking.");
            return false;
        }
        PluginLog.Information($"[Goto] Path is {length:F0}y, mounting up before moving.");
        return true;
    }

    internal static void SetFlag(CustomDestination dest)
    {
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(dest.Territory)?.Map.RowId ?? 0;
        if(mapId == 0) return;
        AgentMap.Instance()->SetFlagMapMarker(dest.Territory, mapId, dest.Position);
        AgentMap.Instance()->OpenMap(mapId, dest.Territory);
        ChatPrinter.Green($"[Lifestream] {"vnavmesh is not installed - destination flagged on map, please walk there manually:".Loc()} {dest.Name}");
    }
}
