using System;

namespace Lifestream.IPC;

/// <summary>
/// <c>Lifestream.*V2</c> 傳送端點的回傳值，以 <c>int</c> 傳出。
/// 🔴 數值是跨插件契約：只能新增、不能改號或改語意。
/// 除了 <see cref="Queued"/>，其餘都是「這次什麼都沒排」，呼叫端不要等 <c>IsBusy</c>。
/// </summary>
public enum IpcQueueResult
{
    /// <summary>已排入佇列；<c>Lifestream.IsBusy</c> 轉回 false＝這串跑完（成功或中途放棄都會停）。</summary>
    Queued = 0,
    /// <summary>已經在目的地，不需要做任何事。</summary>
    AlreadyThere = 1,
    /// <summary>Lifestream 正在忙（佇列或自動移動還在跑）。</summary>
    Busy = 2,
    /// <summary>角色目前不可用（未登入、讀取畫面、過場等）。</summary>
    NotReady = 3,
    /// <summary>目的地不存在或不可前往（名稱／id 查不到、不在我的最愛、沒有這間房、世界不可前往）。</summary>
    NotFound = 4,
    /// <summary>前置檢查都過了，但 Lifestream 最後沒排任何任務（原因寫在 log／聊天欄）。</summary>
    Refused = 5,
    /// <summary>等不到遊戲主執行緒（讀取畫面、嚴重掉幀）或插件卸載中；這次沒做任何事。</summary>
    Unavailable = 6,
    /// <summary>排程時發生例外；這次排進去的任務已撤掉。</summary>
    Error = 7,
}

/// <summary>「有沒有真的排進佇列」的判定。不碰任何 Dalamud 服務，離線探針可以直接編這個檔。</summary>
internal static class IpcQueueMeasure
{
    /// <param name="queuedCount">佇列目前的任務數（含執行中那一項）。</param>
    /// <param name="enqueue">實際排程；回 false＝排程端自己說沒排成。</param>
    /// <param name="minAdded">至少要多出幾項才算排入（有些排程端不論成敗都會先排一項「等畫面」）。</param>
    /// <param name="abortAll">清空佇列。只在呼叫前佇列是空的時候呼叫，所以撤掉的只會是這次排的。</param>
    /// <param name="onError">排程擲例外時呼叫（記錄用），之後回 <see cref="IpcQueueResult.Error"/>。</param>
    internal static IpcQueueResult Run(Func<int> queuedCount, Func<bool> enqueue, int minAdded, Action abortAll, Action<Exception> onError)
    {
        var before = queuedCount();
        try
        {
            var reported = enqueue();
            var added = queuedCount() - before;
            if(reported && added >= Math.Max(1, minAdded)) return IpcQueueResult.Queued;
            if(added > 0 && before == 0) abortAll();
            return IpcQueueResult.Refused;
        }
        catch(Exception e)
        {
            if(queuedCount() > before && before == 0) abortAll();
            onError(e);
            return IpcQueueResult.Error;
        }
    }
}
