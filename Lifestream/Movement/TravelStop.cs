namespace Lifestream.Movement;

/// <summary>
/// 所有「整條中止」入口共用的收尾：先停掉自己交出去的移動（vnavmesh 路線、/automove），再清任務佇列與自己的跟隨路徑。
/// 只在框架執行緒呼叫。別人的移動不碰（兩個 Owner 只停確定是自己送的）。
/// </summary>
internal static class TravelStop
{
    /// <param name="source">寫進記錄檔的中止來源。</param>
    internal static void StopAll(string source)
    {
        // 要在 Abort 之前：佇列一空，兩個 Owner 的 Tick 就會把「是我們的」放掉。
        VnavMoveOwner.StopIfOurs(source);
        AutomoveOwner.StopIfOurs(source);
        P.TaskManager.Abort();
        P.followPath?.Stop();
    }
}
