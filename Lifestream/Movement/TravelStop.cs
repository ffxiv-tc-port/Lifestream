namespace Lifestream.Movement;

/// <summary>
/// 所有「整條中止」入口共用的收尾：先停掉自己交出去的移動（vnavmesh 路線、/automove），再清任務佇列與自己的跟隨路徑。
/// 只在框架執行緒呼叫。別人的移動不碰（兩個 Owner 只停確定是自己送的）。
/// </summary>
internal static class TravelStop
{
    /// <param name="source">寫進記錄檔的中止來源。</param>
    /// <param name="disarmShutdown">停止入口（使用者／艦隊急停）傳 true，一併解除「抵達就關遊戲」；
    /// 流程自己失敗的收尾傳 false：使用者重試同一趟時，他武裝的關機目標要還在。</param>
    internal static void StopAll(string source, bool disarmShutdown)
    {
        // 要在 Abort 之前：佇列一空，兩個 Owner 的 Tick 就會把「是我們的」放掉。
        VnavMoveOwner.StopIfOurs(source);
        AutomoveOwner.StopIfOurs(source);
        P.TaskManager.Abort();
        P.followPath?.Stop();
        if(disarmShutdown) GUI.TabUtility.DisarmShutdown(source);
    }

    /// <summary>
    /// 卸載時收回自己發起、還在跑的移動（vnavmesh 路線、/automove），別人的不碰。
    /// 只在框架執行緒、且不是 Dalamud 卸載期時動手：卸載期 Dispose 可能就地跑在別的執行緒，
    /// 那時送聊天指令／呼叫別的插件不安全，而且遊戲正要關、移動已無意義。
    /// </summary>
    internal static void StopOwnMovementOnUnload()
    {
        if(Svc.Framework.IsFrameworkUnloading || !Svc.Framework.IsInFrameworkUpdateThread) return;
        GenericHelpers.Safe(() => VnavMoveOwner.StopIfOurs("插件卸載"));
        GenericHelpers.Safe(() => AutomoveOwner.StopIfOurs("插件卸載"));
    }
}
