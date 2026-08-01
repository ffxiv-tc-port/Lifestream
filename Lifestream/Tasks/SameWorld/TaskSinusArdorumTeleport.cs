using ECommons.GameHelpers;
using ECommons.Throttlers;
using Lifestream.Schedulers;

namespace Lifestream.Tasks.SameWorld;

/// <summary>
/// 比照 <see cref="TaskFirmanentTeleport"/>:站在嘆息海「最佳威兔洞」乙太之光(175)旁時,
/// 互動並選「前往渴望灣」選單項直達宇宙探索區域。
/// </summary>
internal static class TaskSinusArdorumTeleport
{
    internal static void Enqueue()
    {
        if(C.WaitForScreenReady) P.TaskManager.Enqueue(Utils.WaitForScreen);
        P.TaskManager.Enqueue(WorldChange.TargetValidAetheryte);
        P.TaskManager.Enqueue(WorldChange.InteractWithTargetedAetheryte);
        P.TaskManager.Enqueue(() =>
        {
            if(!Player.Available) return false;
            return Utils.TrySelectSpecificEntry(Lang.TravelToSinusArdorum, () => EzThrottler.Throttle("SelectString"));
        }, $"TeleportToSinusArdorumSelect {Lang.TravelToSinusArdorum.Print()}");
        EnqueueSelectAnyInstance();
    }

    /// <summary>
    /// 選完「前往渴望灣」後,多副本區的情況會再跳一個「切換副本區」選單,單副本區則直接轉場。
    /// 比照 StaticAlias.CosmicExploration 的處理:畫面開始轉場就視為完成,
    /// 選單有出現就選「隨意(自動選擇)」(Addon#2091)。逾時不中止,最差情況等同玩家手動選。
    /// </summary>
    internal static void EnqueueSelectAnyInstance()
    {
        P.TaskManager.Enqueue(() =>
        {
            if(!Player.Available) return false;
            if(!IsScreenReady()) return true;
            return Utils.TrySelectSpecificEntry(Lang.AnyInstance, () => EzThrottler.Throttle("SelectString"));
        }, "SelectAnyWksInstance", new(timeLimitMS: 10000, abortOnTimeout: false));
    }
}
