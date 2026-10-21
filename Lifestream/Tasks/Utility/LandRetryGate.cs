namespace Lifestream.Tasks.Utility;

/// <summary>
/// 抵達後降落的「要不要再按一次跳下」判斷。純邏輯、不讀遊戲，一次降落一個實例；只在框架執行緒用。
/// 跳下（一般動作 23）在地面上騎乘時＝下坐騎，所以送過一次之後只在「還在空中、高度一段時間沒在降」時才重送，
/// 降落途中與剛落地的那幾幀絕不重送。是否還在空中由呼叫端判（在空中才呼叫）。
/// </summary>
internal sealed class LandRetryGate
{
    internal const int MaxSends = 5;
    /// <summary>送過之後每隔這麼久看一次高度；這段期間降不到 <see cref="MinDrop"/> 才算沒在降落。</summary>
    internal const long StallWindowMs = 1500;
    internal const float MinDrop = 0.5f;

    public int Sends { get; private set; }

    private bool started;
    private long checkAt;
    private float checkY;

    /// <param name="now">呼叫端的單調時鐘（毫秒）。</param>
    /// <param name="y">角色目前高度。</param>
    /// <returns>這一幀該不該送跳下（呼叫端還要自己確認動作可用）。</returns>
    public bool ShouldSend(long now, float y)
    {
        if(Sends >= MaxSends) return false;
        if(!started) return true;
        if(now < checkAt) return false;
        if(checkY - y >= MinDrop)
        {
            Arm(now, y);
            return false;
        }
        return true;
    }

    /// <summary>送出了（不論遊戲回傳什麼）。</summary>
    public void OnSent(long now, float y)
    {
        Sends++;
        Arm(now, y);
    }

    /// <summary>該送卻因為動作不可用沒送：下一個觀察窗再看。</summary>
    public void OnNotReady(long now, float y) => Arm(now, y);

    private void Arm(long now, float y)
    {
        started = true;
        checkAt = now + StallWindowMs;
        checkY = y;
    }
}
