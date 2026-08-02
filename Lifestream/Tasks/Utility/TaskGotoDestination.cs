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
            // 需要的話先接近可用的以太之光節點(優先用身邊摸得到的同網路節點,見 EnqueueAethernetRoute),
            // 再互動並用以太之光網路跳到目標小以太之光。
            EnqueueAethernetRoute(root, shard);
            // 以太之光網路移動也會過一次讀取畫面。等不到就繼續往下走(退回「從目前位置走過去」),
            // 不要讓整條佇列中斷 —— 最差情況等同修正前的行為,不會比原本更糟。
            P.TaskManager.Enqueue(
                () => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
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
    /// 走以太之光網路前往 <paramref name="shard"/>。2.0 的城市各有兩張地圖(利姆薩上/下層甲板、
    /// 烏爾達哈娜爾神殿/太陽神草原、格里達尼亞新/舊街),兩張圖共用同一個以太之光網路,但只有
    /// 其中一張有主水晶本體。人站在「沒有主水晶」的那張圖時,身邊還是摸得到城內以太之光,直接
    /// 走過去用它開選單就好,不必先傳送到另一張圖的主水晶——省掉一次讀取畫面。
    ///
    /// 執行時(不是排入佇列的當下,因為人可能還沒真的走到)先用
    /// <see cref="Utils.GetReachableAethernetNetworkNode"/> 找身邊摸得到(50y 內——刻意比主水晶版
    /// 走位用的 30y 寬,因為這裡多了實際走過去的步驟,見該方法的註解)的同網路節點:
    /// - 摸不到:照舊傳送到主水晶(<see cref="InsertMasterAetheryteFallback"/>)。
    /// - 摸得到:鎖定→開自動移動→走到後互動,沿用
    ///   <see cref="TaskAetheryteAethernetTeleport"/> 走位到主水晶的同一套結構,只是目標放寬成
    ///   「同網路的任一節點」。20 秒內走不到(卡地形、或其實距離超出自動移動可靠範圍)就放棄這條
    ///   捷徑,一樣退回 <see cref="InsertMasterAetheryteFallback"/>——不讓佇列卡住,最差情況等同
    ///   完全不做這個最佳化時的行為。
    ///
    /// ⚠️ 走到的節點可能是主水晶,也可能是城內以太之光(子節點),兩者互動後開的視窗不同:主水晶會先跳
    /// 一層 SelectString(要選「以太之光網路」),子節點則直接開 TelepotTown 目的地清單。所以互動之後
    /// 排的是 <see cref="WorldChange.SelectAethernetIfNeeded"/> 而不是 <see cref="WorldChange.SelectAethernet"/>
    /// ——後者在子節點上永遠找不到那一項,這正是 v7.20.0.19「走到了、互動了、卻不選目標」的原因。
    /// </summary>
    private static void EnqueueAethernetRoute(TinyAetheryte root, TinyAetheryte shard)
    {
        TaskRemoveAfkStatus.Enqueue();
        P.TaskManager.Enqueue(() =>
        {
            if(Utils.GetReachableAethernetNetworkNode(root) == null)
            {
                // 身邊摸不到這個網路的任何節點(不同城市,或距離超出可靠走位範圍)——照舊傳送到主水晶。
                InsertMasterAetheryteFallback(root, shard);
                return;
            }

            P.TaskManager.InsertMulti(
                new(() => WorldChange.TargetReachableAethernetNetworkNode(root), "ApproachTargetNetworkNode"),
                new(() =>
                {
                    if(!Utils.IsActiveAetheryteInNetwork(root))
                    {
                        P.TaskManager.InsertMulti(
                            new(WorldChange.LockOn),
                            new(WorldChange.EnableAutomove),
                            // 50y 以內正常步行時間遠低於 20 秒,逾時多半是卡地形。abortOnTimeout:false
                            // 讓它只放棄這一步(接著仍會關自動移動),不牽連後面的收尾或整條佇列。
                            new(() => Utils.IsActiveAetheryteInNetwork(root), "WaitArriveAtNetworkNode",
                                new(timeLimitMS: 20000, abortOnTimeout: false)),
                            new(WorldChange.DisableAutomove),
                            new FrameDelayTask(10)
                            );
                    }
                }, "ApproachConditionalLockon"),
                new(() =>
                {
                    if(Utils.IsActiveAetheryteInNetwork(root))
                    {
                        P.TaskManager.InsertMulti(
                            new(WorldChange.InteractWithTargetedAetheryte),
                            // 走到的可能是主水晶,也可能是城內以太之光(子節點)——兩者互動後開的視窗不一樣,
                            // 子節點沒有「以太之光網路」這一層選單。所以這裡不能直接排 SelectAethernet
                            // (那正是 .19 卡住的原因),要用會看實際視窗決定的版本。
                            new(WorldChange.SelectAethernetIfNeeded),
                            new DelayTask(C.SlowTeleport ? C.SlowTeleportThrottle : 0),
                            new(() => WorldChange.TeleportToAethernetDestination(shard.Name), nameof(WorldChange.TeleportToAethernetDestination))
                            );
                    }
                    else
                    {
                        // 沒能在時限內走到——放棄這條捷徑,退回傳送到主水晶的完整流程,不讓佇列卡住。
                        PluginLog.Information($"[Goto] Could not reach nearby aethernet network node for {root.Name}, falling back to teleporting to root aetheryte.");
                        InsertMasterAetheryteFallback(root, shard);
                    }
                }, "ApproachCheckArrival")
                );
        }, "ApproachAethernetNetworkNode");
    }

    /// <summary>
    /// 退路:傳送到主水晶再走以太之光網路,跟 <see cref="TaskAetheryteAethernetTeleport"/> 一般情形
    /// (非天穹街/渴望灣特例)的步驟完全同一套,只是照抄成本地方法而不是直接呼叫它。
    /// 不能直接呼叫 <see cref="TaskAetheryteAethernetTeleport.Enqueue(uint, uint)"/>,因為它用
    /// Enqueue 把步驟排到佇列尾端——而這個退路是在佇列「執行途中」才觸發
    /// (<see cref="EnqueueAethernetRoute"/> 呼叫它的當下,呼叫端已經先排了
    /// GotoWaitAethernetTransition/WaitForScreen 在後面),用 Enqueue 會讓退路的步驟排到那些之後,
    /// 變成還沒傳送到目的地就先跑完「等讀取畫面」跟後面的導航/標旗。改用 InsertMulti 插到佇列
    /// 最前面即可保留正確順序——這正是上一版被卡住的根因沒有的東西(接近步驟本身),這裡確保
    /// 就算接近失敗,也不多花一秒地退回原本可靠的行為。
    /// </summary>
    private static void InsertMasterAetheryteFallback(TinyAetheryte root, TinyAetheryte shard)
    {
        P.TaskManager.InsertMulti(
            new(() =>
            {
                if(Svc.ClientState.TerritoryType != root.TerritoryType
                    || Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.Value.ID == root.ID) == null)
                {
                    P.TaskManager.InsertMulti(
                        new(() => S.TeleportService.TeleportToAetheryte(root.ID), "TeleportToRootAetheryte"),
                        new(Utils.WaitForScreenFalse),
                        new(Utils.WaitForScreen)
                        );
                }
            }, "FallbackConditionalTeleportToRootAetheryte"),
            new FrameDelayTask(10),
            new(WorldChange.TargetReachableMasterAetheryte),
            new(() =>
            {
                if(P.ActiveAetheryte == null)
                {
                    P.TaskManager.InsertMulti(
                        new(WorldChange.LockOn),
                        new(WorldChange.EnableAutomove),
                        new(WorldChange.WaitUntilMasterAetheryteExists),
                        new(WorldChange.DisableAutomove),
                        new FrameDelayTask(10)
                        );
                }
            }, "FallbackConditionalLockonTask"),
            new(WorldChange.InteractWithTargetedAetheryte),
            // 這條退路鎖定的是主水晶(TargetReachableMasterAetheryte 只認 IsAetheryte=true),照理一定有
            // 「以太之光網路」選單;仍然用會看實際視窗的版本,是因為它在主水晶情境下行為完全相同,
            // 而萬一鎖到的不是主水晶就不會靜默卡死,還會把當下的選單內容寫進 log。
            new(WorldChange.SelectAethernetIfNeeded),
            new DelayTask(C.SlowTeleport ? C.SlowTeleportThrottle : 0),
            new(() => WorldChange.TeleportToAethernetDestination(shard.Name), nameof(WorldChange.TeleportToAethernetDestination))
            );
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
                P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false));
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. path], true));
                return true;
            }, "GotoBuildPath");
        }, "GotoNavmeshMaster");
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
