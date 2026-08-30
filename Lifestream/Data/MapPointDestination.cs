namespace Lifestream.Data;

/// <summary>
/// 「地圖上的一個點」——<c>Lifestream.GoToMapPoint</c> IPC 的目標。
///
/// 🔴 刻意**不重用** <see cref="CustomDestination"/>:那是使用者自己站在某處存下來的落點,
///    它的 <c>Position</c> 是真實的三維座標;這裡的來源是地圖上的一次點擊,
///    只算得出世界 X/Z,**高度必須等抵達目標區域、navmesh 就緒之後再向 vnavmesh 問地板**。
///    兩者混成同一個型別,早晚會有人把這個「假的 Y」當成真高度直接餵進尋路 ——
///    而那個失敗是靜默的(vnavmesh 的 FindNearestMeshPoly 預設 halfExtentY=5,
///    拿 Y=0 去查在多數野外地圖只會回空路徑,不會報錯)。
/// </summary>
public class MapPointDestination
{
    /// <summary>只用在聊天欄訊息與 log,不參與路由。</summary>
    public string Name = "";
    public uint Territory;
    public float WorldX;
    public float WorldZ;

    /// <summary>
    /// 只給「挑哪座乙太之光」的距離比較用。
    /// ⚠️ <b>Y 恆為 0,不是真實高度</b> —— 路由端全部只取 XZ(TaskGotoDestination.DistanceXZ),
    /// 所以這樣是正確的;但**絕對不可以**把它直接交給 vnavmesh 尋路。
    /// </summary>
    public Vector3 RoutingPosition => new(WorldX, 0f, WorldZ);
}
