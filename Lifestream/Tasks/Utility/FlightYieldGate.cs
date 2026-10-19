namespace Lifestream.Tasks.Utility;

internal enum FlightWatchEvent
{
    None,
    /// <summary>vnavmesh 停下時離目標在抵達距離內。</summary>
    Arrived,
    /// <summary>停在半路，但不是手動操作造成的（沒有偵測到按鍵）：不接續。</summary>
    StoppedShort,
    /// <summary>停在半路、使用者剛按過移動鍵：開始讓位。</summary>
    YieldStarted,
    /// <summary>讓位中使用者停手滿 <see cref="FlightYieldGate.ResumeQuietMs"/>：該從目前位置接續。</summary>
    Resume,
    /// <summary>不再接續（次數用完或持續手動太久），見 <see cref="FlightYieldGate.GiveUpReason"/>。</summary>
    GaveUp,
    /// <summary>讓位中 vnavmesh 又動了（別人送了路線）：這一趟交給它。</summary>
    Cancelled,
}

/// <summary>
/// 飛行最後一段的「被手動中斷就讓位、停手再接續」判斷。純邏輯、不讀遊戲，一趟一個實例；只在框架執行緒用。
/// 時間一律用呼叫端給的 now（毫秒）；「按過沒」另用旗標記，不拿 0 當「沒有」。
/// </summary>
internal sealed class FlightYieldGate
{
    /// <summary>放開按鍵後這麼久內仍算「剛按過」：vnavmesh 因輸入取消的那一幀之前按下的也算。</summary>
    internal const long HandsOnLatchMs = 1000;
    /// <summary>讓位中沒有按鍵、角色也沒在動，連續這麼久才接續（這裡看當下按鍵，不看 <see cref="HandsOnLatchMs"/>）。</summary>
    internal const long ResumeQuietMs = 1000;
    /// <summary>一次讓位最長這麼久；使用者一直在操作就當成他接手了。</summary>
    internal const long MaxYieldMs = 60_000;
    /// <summary>一趟最多接續幾次；用完後再被中斷就不再接續。</summary>
    internal const int MaxResumes = 3;
    /// <summary>三維速度（米／秒）不低於這個值就算「還在動」（騎乘減速、手動升降）。</summary>
    internal const float StillSpeedMax = 1f;
    internal const long SpeedWindowMs = 300;

    public int Resumes { get; private set; }
    public bool Yielding { get; private set; }
    public string GiveUpReason { get; private set; } = "";

    private bool pressSeen;
    private long lastPressAt;
    private long yieldStartedAt;
    private long lastActiveAt;
    private bool hasSample;
    private long sampleAt;
    private Vector3 samplePos;
    private float speed = -1f;

    /// <param name="now">呼叫端的單調時鐘（毫秒）。</param>
    /// <param name="navigating">vnavmesh 在走或還在算路。</param>
    /// <param name="mounted">還在坐騎上；讓位時下了坐騎＝使用者接手，不再接續。</param>
    /// <param name="pressed">這一幀有沒有按移動鍵／推搖桿。</param>
    /// <param name="player">角色目前位置（算速度用）。</param>
    /// <param name="remainingXZ">離目標的水平距離。</param>
    /// <param name="arriveDistance">在這個水平距離內算抵達。</param>
    public FlightWatchEvent Observe(long now, bool navigating, bool mounted, bool pressed, Vector3 player, float remainingXZ, float arriveDistance)
    {
        if(pressed)
        {
            pressSeen = true;
            lastPressAt = now;
        }
        Sample(now, player);
        var handsOn = pressSeen && now - lastPressAt <= HandsOnLatchMs;

        if(!Yielding)
        {
            if(navigating) return FlightWatchEvent.None;
            if(remainingXZ <= arriveDistance) return FlightWatchEvent.Arrived;
            if(!handsOn) return FlightWatchEvent.StoppedShort;
            if(!mounted)
            {
                GiveUpReason = "被手動操作中斷時已不在坐騎上";
                return FlightWatchEvent.GaveUp;
            }
            if(Resumes >= MaxResumes)
            {
                GiveUpReason = $"已經接續 {MaxResumes} 次，又被手動操作中斷";
                return FlightWatchEvent.GaveUp;
            }
            Yielding = true;
            yieldStartedAt = now;
            lastActiveAt = now;
            return FlightWatchEvent.YieldStarted;
        }

        if(navigating)
        {
            Yielding = false;
            return FlightWatchEvent.Cancelled;
        }
        if(!mounted)
        {
            Yielding = false;
            GiveUpReason = "讓位期間下了坐騎，當成使用者接手";
            return FlightWatchEvent.GaveUp;
        }
        if(now - yieldStartedAt >= MaxYieldMs)
        {
            Yielding = false;
            GiveUpReason = $"手動操作超過 {MaxYieldMs / 1000} 秒，當成使用者接手";
            return FlightWatchEvent.GaveUp;
        }
        if(pressed || speed < 0 || speed >= StillSpeedMax) lastActiveAt = now;
        if(now - lastActiveAt < ResumeQuietMs) return FlightWatchEvent.None;

        Yielding = false;
        if(remainingXZ <= arriveDistance) return FlightWatchEvent.Arrived;
        Resumes++;
        return FlightWatchEvent.Resume;
    }

    // 速度＝到上一次取樣的三維位移除以時間；取樣間隔至少 SpeedWindowMs，期間沿用上一個值。-1＝還不知道。
    private void Sample(long now, Vector3 player)
    {
        if(!hasSample)
        {
            hasSample = true;
            sampleAt = now;
            samplePos = player;
            return;
        }
        if(now - sampleAt < SpeedWindowMs) return;
        speed = Vector3.Distance(player, samplePos) / ((now - sampleAt) / 1000f);
        sampleAt = now;
        samplePos = player;
    }
}
