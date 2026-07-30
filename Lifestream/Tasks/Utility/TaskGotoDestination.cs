using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Data;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 自訂座標傳送(安全版,參考 DR BetterTeleport 的自訂落點功能):
/// 1) 傳送到目的區域「距離目標點最近」的已解鎖以太之光;
/// 2) vnavmesh IPC 尋路走過去(軟依賴);
/// 3) 未安裝 vnavmesh 時改為標記地圖旗點並開地圖,提示手動前往。
/// 紅線:不做任何記憶體改座標瞬移(DR 版的 TPSmart/TPPlayerAddress 不抄)。
/// </summary>
public static unsafe class TaskGotoDestination
{
    public static void Enqueue(CustomDestination dest)
    {
        P.TaskManager.Enqueue(() => TeleportToDestinationZone(dest), "GotoTeleportToZone", new(timeLimitMS: 120000));
        P.TaskManager.Enqueue(Utils.WaitForScreen);
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
        if(id == 0) throw new InvalidOperationException($"No unlocked aetheryte in destination zone {ExcelTerritoryHelper.GetName(dest.Territory)}");
        if(EzThrottler.Throttle("GotoTeleport", 5000))
        {
            S.TeleportService.TeleportToAetheryte(id);
        }
        return false;
    }

    /// <summary>
    /// 在目的區域的已解鎖以太之光中,挑距離目標點最近者
    /// (座標來自 ECommons Map.AetherytePosition:Level 資料優先,退回 MapMarker 換算)。
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
            float dist;
            try
            {
                dist = (ECommons.GameHelpers.Map.AetherytePosition(data.Value) - dest.Position).LengthSquared();
            }
            catch
            {
                dist = float.MaxValue - 1;
            }
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
