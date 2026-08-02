using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Data;
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

        // 先驗證目的地可達再排任務:走不到就在聊天欄講清楚,不丟例外。
        // (已在目的區域時不需要傳送點,TeleportToDestinationZone 會直接回 true)
        if(!hasAethernetRoute && P.Territory != dest.Territory && FindClosestUnlockedAetheryte(dest) == 0)
        {
            ChatPrinter.Red($"[Lifestream] {"Cannot reach destination - no unlocked aetheryte in target zone:".Loc()} {dest.Name} ({ExcelTerritoryHelper.GetName(dest.Territory)})");
            return;
        }

        if(hasAethernetRoute)
        {
            PluginLog.Information($"[Goto] {dest.Name}: using aethernet route {root.Name}({root.ID}) -> {shard.Name}({shard.ID})");
            // 既有且已驗證的流程:需要的話先傳送到主水晶,再互動並用以太之光網路跳到目標小以太之光
            TaskAetheryteAethernetTeleport.Enqueue(root.ID, shard.ID);
            // 以太之光網路移動也會過一次讀取畫面。等不到就繼續往下走(退回「從目前位置走過去」),
            // 不要讓整條佇列中斷 —— 最差情況等同修正前的行為,不會比原本更糟。
            P.TaskManager.Enqueue(
                () => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                "GotoWaitAethernetTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
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
