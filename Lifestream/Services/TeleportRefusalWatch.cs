namespace Lifestream.Services;

/// <summary>
/// 「傳送被遊戲拒絕多久了、該不該中止任務鏈」的純判斷，不碰遊戲記憶體。
/// 狀態碼＝<c>ActionManager.GetActionStatus</c> 的回傳值＝LogMessage 的列號；0＝現在可以傳送（或無法判斷）。
/// 契約：只在框架執行緒用；時間一律由呼叫端給（毫秒，單調遞增）。
/// </summary>
internal sealed class TeleportRefusalWatch
{
    internal const long GraceMs = 3000;

    private long since = -1;

    internal bool Armed => since >= 0;
    internal uint Status { get; private set; }

    /// <summary>
    /// 等一下就會自己解除的拒絕：576 移動中、580 詠唱中、582 暫時無法發動、1665 其他傳送進行中。
    /// 這幾種照舊等，不中止也不重設計時；其他狀態碼（含沒見過的）都算「這次傳送走不了」。
    /// </summary>
    internal static bool IsTransient(uint status) => status is 576 or 580 or 582 or 1665;

    /// <summary>任務鏈裡的傳送被拒時呼叫。暫時性狀態不開始計時；已在計時就沿用最早那一刻。</summary>
    internal void Arm(uint status, long now)
    {
        if(status == 0 || IsTransient(status)) return;
        if(since < 0) since = now;
        Status = status;
    }

    internal void Clear()
    {
        since = -1;
        Status = 0;
    }

    /// <summary>
    /// 每幀呼叫。回傳 true＝非暫時性的拒絕已持續 <see cref="GraceMs"/> 以上、該中止了
    /// （<see cref="Status"/> 是最後看到的狀態碼，呼叫端讀完再 <see cref="Clear"/>）。
    /// 任務鏈結束或遊戲說可以傳送（status 0）就歸零。
    /// </summary>
    internal bool Observe(uint status, bool chainBusy, long now, out long elapsedMs)
    {
        elapsedMs = 0;
        if(since < 0) return false;
        if(!chainBusy || status == 0)
        {
            Clear();
            return false;
        }
        elapsedMs = now - since;
        if(IsTransient(status)) return false;
        Status = status;
        return elapsedMs >= GraceMs;
    }
}
