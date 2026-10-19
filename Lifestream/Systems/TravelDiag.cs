using ECommons.Automation.NeoTaskManager;
using System.Threading;
using AetheryteSheet = Lumina.Excel.Sheets.Aetheryte;

namespace Lifestream.Systems;

/// <summary>
/// 座標傳送／導航鏈的 Information 級診斷共用件：節流、最後動作、任務鏈被誰中止、登出當下的現場。
/// </summary>
/// <remarks>
/// 契約：<see cref="ShouldLog"/> 與 <see cref="Record"/> 可從任何執行緒呼叫；其餘成員只在遊戲主執行緒用。
/// 節流刻意不用 EzThrottler：它是整個外掛共用的零同步靜態表，而 IPC 端點跑在呼叫端的執行緒上。
/// </remarks>
internal static class TravelDiag
{
    private const int MaxThrottleKeys = 256;
    private static readonly Dictionary<string, long> LogTimes = [];

    /// <summary>首次必放行，之後同一個 key 每 <paramref name="intervalMs"/> 毫秒放行一次。鎖內只碰字典。</summary>
    internal static bool ShouldLog(string key, long intervalMs = 10000)
    {
        var now = Environment.TickCount64;
        lock(LogTimes)
        {
            if(LogTimes.TryGetValue(key, out var last) && now - last < intervalMs) return false;
            if(LogTimes.Count >= MaxThrottleKeys && !LogTimes.ContainsKey(key)) LogTimes.Clear();
            LogTimes[key] = now;
            return true;
        }
    }

    private sealed record Mark(string What, long At);
    private static Mark lastAction;
    private static Mark lastDirectWrite;

    /// <summary>記下 Lifestream 剛做的一件事，登出／斷線時印出來。</summary>
    internal static void Record(string what) => Volatile.Write(ref lastAction, new Mark(what, Environment.TickCount64));

    internal static void RecordDirectWrite(string what)
    {
        var mark = new Mark(what, Environment.TickCount64);
        Volatile.Write(ref lastDirectWrite, mark);
        Volatile.Write(ref lastAction, mark);
    }

    private static string Describe(Mark mark)
        => mark == null ? "（這次載入後沒有）" : $"{mark.What}（{(Environment.TickCount64 - mark.At) / 1000.0:F1} 秒前）";

    internal static string AetheryteName(uint id)
    {
        var row = Svc.Data.GetExcelSheet<AetheryteSheet>().GetRowOrDefault(id);
        return row?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } place ? place
            : row?.AethernetName.ValueNullable?.Name.ToString() is { Length: > 0 } shard ? shard
            : $"#{id}";
    }

    internal static void Enable(TaskManager taskManager)
    {
        taskManager.DefaultConfiguration.OnTaskCompletion += OnTaskCompletion;
        Svc.ClientState.Logout += OnLogout;
    }

    internal static void Disable() => Svc.ClientState.Logout -= OnLogout;

    /// <summary>有東西可中止時記一行「誰中止的」。要在真正中止之前呼叫；什麼都沒在跑時不寫。</summary>
    internal static void LogAbort(string source)
    {
        var taskManager = P.TaskManager;
        var waypoints = P.followPath?.Waypoints.Count ?? 0;
        if(!taskManager.IsBusy && waypoints == 0) return;
        var what = $"{source}：捨棄 {taskManager.NumQueuedTasks} 項任務（目前任務 {DescribeTask(taskManager.CurrentTask)}），停止自動移動（剩 {waypoints} 個航點）";
        Record(what);
        PluginLog.Information($"[Lifestream] 中止任務鏈 ← {what}。");
    }

    private static string DescribeTask(TaskManagerTask task) => task == null ? "無" : TaskTimeoutLog.Describe(task);

    /// <summary>任務回傳 null＝要求中止整條佇列。ECommons 自己那一行是 Debug 級，Information 等級收不到。</summary>
    private static void OnTaskCompletion(TaskManagerTask task, ref bool? isCompleted)
    {
        if(isCompleted != null) return;
        var what = $"任務 {TaskTimeoutLog.Describe(task)} 回傳 null，整條任務佇列中止（捨棄後續 {P.TaskManager.Tasks.Count} 項）";
        Record(what);
        PluginLog.Information($"[Lifestream] {what}。");
    }

    private static void OnLogout(int type, int code)
    {
        if(P == null) return;
        var taskManager = P.TaskManager;
        var navRunning = S.Ipc.VnavmeshIPC?.IsRunning?.Invoke();
        var message = $"[診斷] 登出／斷線事件 type={type} code={code}。Lifestream 最後動作＝{Describe(Volatile.Read(ref lastAction))}；"
            + $"任務佇列忙碌={taskManager?.IsBusy}（目前任務 {DescribeTask(taskManager?.CurrentTask)}、排隊 {taskManager?.Tasks.Count}）；"
            + $"自動移動剩 {P.followPath?.Waypoints.Count ?? 0} 個航點；vnavmesh 移動中={navRunning?.ToString() ?? "?"}；"
            + $"直接寫入座標開關={C?.AetheryteLandingDirectWrite}，上次直接寫入＝{Describe(Volatile.Read(ref lastDirectWrite))}。";
        // code 10000 是一般登出，只記 Verbose；其他代碼才寫 Information，使用者回報時看得到。
        if(code == NormalLogoutCode) PluginLog.Verbose(message);
        else PluginLog.Information(message);
    }

    private const int NormalLogoutCode = 10000;
}
