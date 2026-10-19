namespace Lifestream.Movement;

/// <summary>
/// 記住「vnavmesh 現在的移動是 Lifestream 送的」，讓自己的停止路徑只停自己發的導航、不碰別的插件的。
/// 只在框架執行緒讀寫（任務、<c>Framework_Update</c>、停止點）。
/// </summary>
internal static class VnavMoveOwner
{
    private static bool Issued = false;

    internal static bool PathfindAndMoveTo(Vector3 point, bool fly)
    {
        Issued = true;
        return S.Ipc.VnavmeshIPC.PathfindAndMoveTo(point, fly);
    }

    /// <summary>等待那一步看到移動結束、或移動已交給別人時呼叫。</summary>
    internal static void Release() => Issued = false;

    /// <summary>佇列空了就不再算是我們的（逾時放行、中止之後別人送的路線不能被我們停掉）。</summary>
    internal static void Tick()
    {
        if(Issued && !P.TaskManager.IsBusy) Issued = false;
    }

    /// <summary>停止點呼叫：在佇列 Abort 之前呼叫，否則 <see cref="Tick"/> 可能已經放掉旗標。</summary>
    internal static void StopIfOurs(string source)
    {
        if(!Issued) return;
        Issued = false;
        // 只送 Path.Stop 停不住還在算的尋路（算完照走），兩個都要。
        S.Ipc.VnavmeshIPC.PathfindCancelAll();
        S.Ipc.VnavmeshIPC.Stop();
        PluginLog.Information($"[Goto] {source}：已停止 Lifestream 交給 vnavmesh 的移動（取消尋路＋停止路徑）。");
    }
}
