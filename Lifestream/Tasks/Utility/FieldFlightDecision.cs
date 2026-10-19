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

    internal static float EffectiveThreshold(float minDistance)
        => float.IsNaN(minDistance) || minDistance < DistanceFloor ? DistanceFloor : minDistance;
}
