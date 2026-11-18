using Lumina.Excel.Sheets;
using System.Threading;

namespace Lifestream.Movement;

/// <summary>
/// 「抵達就關遊戲」的狀態。只有使用者在 Utility 分頁能武裝，停止入口只會解除。
/// 🔴 本型別不得有介面元件的靜態初始式：IPC 停止端點在呼叫端執行緒上碰它，第一次碰會在那條執行緒上跑型別初始化。
/// </summary>
internal static class ShutdownOnArrival
{
    /// <summary>目標世界；0＝未武裝。跨執行緒讀寫一律整數原子操作。</summary>
    internal static int TargetWorldID = 0;

    /// <summary>
    /// 停止入口共用：解除「抵達就關遊戲」（倒數中也一併取消，倒數只在目標非 0 時跑）。
    /// 可在任何執行緒呼叫：IPC <c>Abort</c> 在呼叫端執行緒先解除，閘門逾時或卸載期不進框架執行緒也照樣生效。
    /// </summary>
    internal static void Disarm(string source)
    {
        var old = Interlocked.Exchange(ref TargetWorldID, 0);
        if(old == 0) return;
        // 解除已經生效；提示失敗不能擲回呼叫端（IPC Abort 接著還要中止任務鏈）。
        try
        {
            var name = Svc.Data.GetExcelSheet<World>().GetRowOrDefault((uint)old)?.Name.ToString();
            if(string.IsNullOrEmpty(name)) name = $"#{old}";
            PluginLog.Information($"[Lifestream] {source}：已解除「抵達世界後關閉遊戲」（目標＝{name}）。");
            ChatQueue.Yellow($"[Lifestream] {"Stopped: cancelled \"shutdown game upon arriving to ??\".".Loc(name)}");
        }
        catch(Exception e)
        {
            PluginLog.Warning($"[Lifestream] {source}：已解除「抵達世界後關閉遊戲」（目標 #{old}），但提示失敗：{e.Message}");
        }
    }
}
