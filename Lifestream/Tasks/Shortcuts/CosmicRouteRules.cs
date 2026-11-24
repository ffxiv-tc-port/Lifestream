namespace Lifestream.Tasks.Shortcuts;

/// <summary>
/// <see cref="TaskCosmicShortcut"/> 的純判斷:不碰遊戲記憶體、不讀表,輸入由呼叫端在框架執行緒讀好再傳進來。
/// </summary>
internal static class CosmicRouteRules
{
    internal enum MissingEntry
    {
        /// <summary>照舊等到這一步逾時。零值。</summary>
        KeepWaiting = 0,
        /// <summary>要去法恩娜、選單只有渴望灣＝尚未解鎖:停下、不走退路。</summary>
        NotUnlocked = 1,
        /// <summary>要去渴望灣、選單只有法恩娜＝解鎖法恩娜後乙太之光只列最新星球:關選單、直接走入口物件路線。</summary>
        OtherPlanetOnly = 2,
    }

    /// <summary>乙太之光選單已開好、但沒有目標那一項時的處置。</summary>
    internal static MissingEntry ClassifyMissingEntry(bool targetIsSinusArdorum, bool menuHasSinusArdorum, bool menuHasPhaenna)
    {
        if(targetIsSinusArdorum) return menuHasPhaenna ? MissingEntry.OtherPlanetOnly : MissingEntry.KeepWaiting;
        return menuHasSinusArdorum ? MissingEntry.NotUnlocked : MissingEntry.KeepWaiting;
    }

    /// <summary>
    /// Lifestream 還沒替使用者選任何項目時,看到轉場、區域變了或目的地視窗開著＝使用者自己選了目的地。
    /// 呼叫端只在「還沒選」時呼叫;自己選過之後的轉場是預期中的,不能拿來判斷。
    /// </summary>
    /// <param name="betweenAreas">轉場旗標。</param>
    /// <param name="stageTerritory">這一段開始時的區域;0＝不比區域。</param>
    /// <param name="territory">目前區域。</param>
    /// <param name="planetWindowOpen">「選擇目的地」視窗(WKSPlanetSelect)開好了。</param>
    /// <returns>log 用的訊號說明;null＝沒有跡象。</returns>
    internal static string UserChoiceSignal(bool betweenAreas, uint stageTerritory, uint territory, bool planetWindowOpen)
    {
        if(betweenAreas) return "a zone change started";
        if(stageTerritory != 0 && territory != stageTerritory) return $"the zone changed from {stageTerritory} to {territory}";
        if(planetWindowOpen) return "the destination window opened";
        return null;
    }

    internal enum Arrival
    {
        /// <summary>還在轉場／讀取,再等一下。零值。</summary>
        Wait = 0,
        Arrived = 1,
        /// <summary>沒到:呼叫端中止後面的步驟,不在錯的區域繼續。</summary>
        NotArrived = 2,
    }

    /// <summary>
    /// 玄關路線跑完後「後面的步驟可不可以接著跑」。<paramref name="settled"/>＝不在轉場、畫面就緒、人能動;
    /// 轉場中先等,轉場結束或等滿 <paramref name="settleLimitMs"/> 就只看區域。
    /// </summary>
    internal static Arrival ClassifyArrival(uint targetTerritory, uint territory, bool settled, long waitedMs, long settleLimitMs)
    {
        if(!settled && waitedMs < settleLimitMs) return Arrival.Wait;
        return territory == targetTerritory ? Arrival.Arrived : Arrival.NotArrived;
    }
}
