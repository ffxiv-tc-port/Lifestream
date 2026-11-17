using System.Runtime.CompilerServices;

namespace Lifestream;

/// <summary>
/// hook detour 自訂邏輯擲出受管理例外時的回報：每份實例累計次數，30 秒最多寫一行 Information。
/// 只回報、不暫停：之後每次照樣執行自訂邏輯，Original 由呼叫端在 try 外照常呼叫。
/// </summary>
internal sealed class DetourErrorLog
{
    private long total;
    private DateTime lastLog = DateTime.MinValue;

    /// <param name="message">(site, 累計次數, 例外) → log 文字；傳 static lambda 免得每次配置。</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Report(string site, Exception ex, Func<string, long, Exception, string> message)
    {
        try
        {
            ++total;
            var now = DateTime.UtcNow;
            if(now - lastLog < TimeSpan.FromSeconds(30)) return;
            lastLog = now;
            PluginLog.Information(message(site, total, ex));
        }
        catch
        {
            // 回報本身擲例外也不可以穿回原生框架。
        }
    }
}
