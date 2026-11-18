using Lifestream.Systems;
using Lifestream.Tasks.Utility;

namespace Lifestream.Movement;

/// <summary>
/// 使用者主動下的傳送／導航鏈只剩最後一段自動步行時，使用者自己持續移動就當成他接手：
/// 走跟進度條右鍵相同的收尾（停移動、清佇列），不必再右鍵。只在框架執行緒呼叫。
/// </summary>
/// <remarks>
/// 「最後一段」＝FollowPath 還有航點，而佇列裡除了抵達提醒哨兵什麼都沒有（目前任務也只能是哨兵或空）；
/// 讀取畫面、傳送互動、後面還有步驟的步行都不符合，行為不變。
/// 別的插件經 IPC 驅動的鏈不套用：對方會把「Lifestream 不忙了」當成已經抵達。
/// </remarks>
internal static class UserTakeover
{
    /// <summary>短按微調方向不算接手。</summary>
    private const long HoldMs = 500;

    /// <summary>目前這條鏈是使用者主動下的；鏈跑完（佇列空、沒有航點）就清掉。</summary>
    private static bool UserChainActive;

    /// <summary>使用者主動下指令的入口在排完任務後呼叫；這次真的排出任務才算。</summary>
    internal static void NoteUserChain(int queuedBefore)
    {
        if(P.TaskManager.NumQueuedTasks > queuedBefore) UserChainActive = true;
    }

    internal static void Tick()
    {
        var path = P.followPath;
        if(path == null || path.Waypoints.Count == 0)
        {
            if(!P.TaskManager.IsBusy) UserChainActive = false;
            return;
        }
        if(!UserChainActive || !C.EndFinalWalkOnUserMovement || !OnlyArrivalSentinelLeft()) return;
        var held = path.UserInputHeldMs;
        if(held < HoldMs) return;
        UserChainActive = false;
        // 不解除「抵達世界後關閉遊戲」：接手走路不等於取消關機（倒數可能正在跑）；要取消照舊右鍵或 /li stop。
        TravelDiag.LogAbort($"使用者接手移動，結束最後一段步行（連續手動移動 {held} 毫秒）");
        TravelStop.StopAll("使用者接手移動", disarmShutdown: false);
    }

    private static bool OnlyArrivalSentinelLeft()
    {
        var taskManager = P.TaskManager;
        if(taskManager.CurrentTask is { } current && current.Name != TaskAnnounceArrival.TaskName) return false;
        var queued = taskManager.Tasks;
        for(var i = 0; i < queued.Count; i++)
        {
            if(queued[i].Name != TaskAnnounceArrival.TaskName) return false;
        }
        return true;
    }
}
