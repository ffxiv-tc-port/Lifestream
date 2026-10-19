namespace Lifestream.Data;

/// <summary>
/// 自訂座標地點(安全版自訂傳送,參考 DR BetterTeleport 的自訂落點概念):
/// 名稱 + 區域 + 世界座標;執行時傳送到該區最近的以太之光,再由 vnavmesh 走過去。
/// </summary>
public class CustomDestination
{
    public string Name = "";
    public uint Territory;
    public Vector3 Position;
}
