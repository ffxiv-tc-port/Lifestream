namespace Lifestream.Tasks.Utility;

/// <summary>
/// 野外導航「飛或走」的純判斷：不讀遊戲狀態，呼叫端把當下的值量好傳進來（離線量測台直接呼叫它）。
/// </summary>
internal static class FieldFlightDecision
{
    /// <summary>
    /// 門檻下限＝飛行分支判「vnavmesh 沒給路線」的距離：比它近時，起飛後路線失敗會被當成已抵達、不會退回地面。
    /// </summary>
    internal const float DistanceFloor = 20f;

    /// <summary>
    /// 地圖點的「陡坡」下限：高低差超過這個值、而且比水平距離還大（平均坡度超過 45 度）時，水平距離在門檻內也照飛。
    /// </summary>
    internal const float SteepClimbMinHeight = 10f;

    /// <param name="enabled">設定「可飛行時改用飛行」。</param>
    /// <param name="zoneFlyable">所在區域現在能飛。</param>
    /// <param name="diving">潛水中。</param>
    /// <param name="inCombat">戰鬥中。</param>
    /// <param name="inFlight">已在空中：不看開關、區域、距離與戰鬥照飛，否則從空中要地面路線可能回空路徑、停在半空。</param>
    /// <param name="distanceXZ">角色到目標的水平直線距離；量不到傳 NaN（＝走地面）。</param>
    /// <param name="minDistance">使用者設定的門檻；低於 <see cref="DistanceFloor"/> 時以下限為準。</param>
    /// <param name="reason">給 log 的一句理由。</param>
    internal static bool ShouldFly(bool enabled, bool zoneFlyable, bool diving, bool inCombat, bool inFlight,
        float distanceXZ, float minDistance, out string reason)
        => Decide(enabled, zoneFlyable, diving, inCombat, inFlight, distanceXZ, float.NaN, minDistance, out reason);

    /// <summary>
    /// 地圖點導航（<c>GoToMapPoint</c> IPC）的最後一段：呼叫端要求飛行時才套用 <see cref="ShouldFly"/> 的條件；
    /// 不看「可飛行時改用飛行」開關（那是使用者自己的目的地用的）。已在空中時一律照飛（見 inFlight）。
    /// 水平距離在門檻內、但落點比角色高或低得多（<see cref="IsSteepClimb"/>）時也飛：地面路線可能要繞很遠或根本沒有。
    /// </summary>
    /// <param name="requested">呼叫端傳進來的 fly。</param>
    /// <param name="heightDiff">落點與角色的高度差（正負皆可）；量不到傳 NaN（＝不看高度）。</param>
    internal static bool ShouldFlyMapPoint(bool requested, bool zoneFlyable, bool diving, bool inCombat, bool inFlight,
        float distanceXZ, float heightDiff, float minDistance, out string reason)
    {
        if(!requested && !(inFlight && !float.IsNaN(distanceXZ))) { reason = "呼叫端沒有要求飛行"; return false; }
        return Decide(true, zoneFlyable, diving, inCombat, inFlight, distanceXZ, heightDiff, minDistance, out reason);
    }

    internal static float EffectiveThreshold(float minDistance)
        => float.IsNaN(minDistance) || minDistance < DistanceFloor ? DistanceFloor : minDistance;

    /// <summary>高低差超過 <see cref="SteepClimbMinHeight"/> 且大於水平距離；任一值是 NaN 時為 false。</summary>
    internal static bool IsSteepClimb(float distanceXZ, float heightDiff)
    {
        var dy = MathF.Abs(heightDiff);
        return dy > SteepClimbMinHeight && dy > distanceXZ;
    }

    private static bool Decide(bool enabled, bool zoneFlyable, bool diving, bool inCombat, bool inFlight,
        float distanceXZ, float heightDiff, float minDistance, out string reason)
    {
        var threshold = EffectiveThreshold(minDistance);
        if(inFlight && !float.IsNaN(distanceXZ)) { reason = $"已在飛行中（距離 {distanceXZ:F1} 米），照舊飛過去"; return true; }
        if(!enabled) { reason = "「可飛行時改用飛行」關閉"; return false; }
        if(!zoneFlyable) { reason = "此區域不能飛（不是一般野外，或風脈還沒收集完）"; return false; }
        if(diving) { reason = "潛水中"; return false; }
        if(inCombat) { reason = "戰鬥中"; return false; }
        if(float.IsNaN(distanceXZ)) { reason = "量不到角色位置"; return false; }
        if(distanceXZ > threshold) { reason = $"可飛行、距離 {distanceXZ:F1} 米超過門檻 {threshold:F0} 米"; return true; }
        if(IsSteepClimb(distanceXZ, heightDiff))
        {
            reason = $"水平距離 {distanceXZ:F1} 米未超過門檻 {threshold:F0} 米，但高低差 {MathF.Abs(heightDiff):F1} 米比水平距離還大，地面可能要繞很遠";
            return true;
        }
        reason = $"距離 {distanceXZ:F1} 米未超過門檻 {threshold:F0} 米";
        return false;
    }
}
