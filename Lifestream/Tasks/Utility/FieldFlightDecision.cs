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

    /// <param name="enabled">設定「可飛行時改用飛行」。</param>
    /// <param name="zoneFlyable">所在區域現在能飛。</param>
    /// <param name="diving">潛水中。</param>
    /// <param name="inCombat">戰鬥中。</param>
    /// <param name="distanceXZ">角色到目標的水平直線距離；量不到傳 NaN（＝走地面）。</param>
    /// <param name="minDistance">使用者設定的門檻；低於 <see cref="DistanceFloor"/> 時以下限為準。</param>
    /// <param name="reason">給 log 的一句理由。</param>
    internal static bool ShouldFly(bool enabled, bool zoneFlyable, bool diving, bool inCombat,
        float distanceXZ, float minDistance, out string reason)
    {
        var threshold = EffectiveThreshold(minDistance);
        if(!enabled) { reason = "「可飛行時改用飛行」關閉"; return false; }
        if(!zoneFlyable) { reason = "此區域不能飛（不是一般野外，或風脈還沒收集完）"; return false; }
        if(diving) { reason = "潛水中"; return false; }
        if(inCombat) { reason = "戰鬥中"; return false; }
        if(float.IsNaN(distanceXZ)) { reason = "量不到角色位置"; return false; }
        if(!(distanceXZ > threshold)) { reason = $"距離 {distanceXZ:F1} 米未超過門檻 {threshold:F0} 米"; return false; }
        reason = $"可飛行、距離 {distanceXZ:F1} 米超過門檻 {threshold:F0} 米";
        return true;
    }

    /// <summary>
    /// 地圖點導航（<c>GoToMapPoint</c> IPC）的最後一段：呼叫端要求飛行時才套用 <see cref="ShouldFly"/>；
    /// 只會把「飛」降成「不飛」，不看「可飛行時改用飛行」開關（那是使用者自己的目的地用的）。
    /// </summary>
    /// <param name="requested">呼叫端傳進來的 fly。</param>
    internal static bool ShouldFlyMapPoint(bool requested, bool zoneFlyable, bool diving, bool inCombat,
        float distanceXZ, float minDistance, out string reason)
    {
        if(!requested) { reason = "呼叫端沒有要求飛行"; return false; }
        return ShouldFly(true, zoneFlyable, diving, inCombat, distanceXZ, minDistance, out reason);
    }

    internal static float EffectiveThreshold(float minDistance)
        => float.IsNaN(minDistance) || minDistance < DistanceFloor ? DistanceFloor : minDistance;
}
