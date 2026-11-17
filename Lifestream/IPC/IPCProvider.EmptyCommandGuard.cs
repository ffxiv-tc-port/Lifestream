using ECommons;
using ECommons.EzIpcManager;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using Lifestream.GUI.Windows;
using Lifestream.Tasks;
using Lifestream.Tasks.Login;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;

namespace Lifestream.IPC;

public partial class IPCProvider
{
    /// <summary>
    /// 與 <c>ProcessCommandInternal</c> 同一套切法：切完一段都不剩，就和裸 <c>/li</c> 走同一條路
    /// （而且連 <c>LiCommandBehavior</c> 都不看，因為原字串不是 ""）。
    /// </summary>
    internal static bool IsEffectivelyEmptyCommand(string arguments)
        => string.IsNullOrWhiteSpace(arguments)
        || arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0;

    /// <summary>
    /// 統一的拒絕點：寫一行 Information（要使用者回報的診斷），
    /// 並盡力指出是哪個外掛呼叫的。節流只擋 log，不擋拒絕本身。
    /// </summary>
    private static void RejectEmptyIpcCommand(string endpoint, string received)
    {
        var caller = TryIdentifyIpcCaller();
        // 🔴 這裡刻意**不用** EzThrottler：它是整個外掛共用的靜態 Dictionary 且零同步，
        //    而這支從 IPC 端點進來，跑在**呼叫端的執行緒**上。並行插入弄壞的不只是這一個 key，
        //    是整張表 —— 連帶弄壞外掛裡所有模組的節流。所以自帶字典＋自己的鎖。
        //    首次必放行、key 帶呼叫端，不同外掛各自看得到第一次。
        if(!ShouldLogReject($"{endpoint}.{caller}")) return;
        var shown = received == null ? "null" : $"「{received}」";
        PluginLog.Information($"[Lifestream IPC 守衛] 拒絕執行 {endpoint}({shown})：空參數（含只有逗號）等同裸 /li，預設設定下會把角色傳送回本世界，所以一律不執行。疑似呼叫端＝{caller}。請呼叫端改成傳明確的目的地，或在呼叫前自行判斷空值。");
    }

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="RejectLogIntervalMs"/> 毫秒放行一次。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不呼叫任何別的外掛、不做 I/O。
    /// </summary>
    private static bool ShouldLogReject(string key)
    {
        var now = Environment.TickCount64;
        lock(RejectLogTimes)
        {
            if(RejectLogTimes.TryGetValue(key, out var last) && now - last < RejectLogIntervalMs) return false;
            if(RejectLogTimes.Count >= MaxTrackedRejectKeys && !RejectLogTimes.ContainsKey(key)) RejectLogTimes.Clear();
            RejectLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>
    /// CallGate 不帶呼叫端身分，只能從受管堆疊回推第一個既不是 Lifestream
    /// 也不是框架的組件。這是盡力而為的診斷：回「不明」不代表沒事。
    /// </summary>
    private static string TryIdentifyIpcCaller()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(false);
            for(var i = 0; i < trace.FrameCount; i++)
            {
                var name = trace.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
                if(name == null) continue;
                if(IpcCallerSkippedAssemblies.Any(x => name == x || name.StartsWith($"{x}.", StringComparison.Ordinal))) continue;
                return name;
            }
        }
        catch(Exception e)
        {
            return $"辨識失敗({e.GetType().Name})";
        }
        return "不明";
    }
}
