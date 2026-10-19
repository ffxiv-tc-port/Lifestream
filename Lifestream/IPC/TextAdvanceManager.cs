using ECommons.EzSharedDataManager;

namespace Lifestream.IPC;

/// <summary>
/// 佇列忙碌期間把本插件放進 <c>TextAdvance.StopRequests</c>，讓忙碌期間的 Talk 只有 Lifestream 一個主人
/// （TextAdvance 的 Talk 點擊掛在 Talk 的 PostUpdate、只認自己的守衛，兩邊同幀各點一次＝攔不到的 AVE）。
/// 只在框架執行緒呼叫（<c>Framework_Update</c>、任務、<c>Dispose</c>）。
/// </summary>
internal static class TextAdvanceManager
{
    private const string StopRequestsName = "TextAdvance.StopRequests";
    private static bool Locked = false;
    private static int LockedFrames = 0;

    /// <summary>每幀由 <c>Framework_Update</c> 呼叫；不忙就解鎖，所以逾時、例外中止、停止都在下一幀解開。</summary>
    internal static void Tick()
    {
        if(P.TaskManager.IsBusy)
        {
            if(!EzSharedData.TryGet<HashSet<string>>(StopRequestsName, out var data)) return;
            // TextAdvance 重新載入時會清空名單 ⇒ 每幀補回；補回那一幀當成剛加鎖。
            if(data.Add(Svc.PluginInterface.InternalName))
            {
                if(!Locked) PluginLog.Debug("TextAdvance locked");
                LockedFrames = 0;
            }
            else if(LockedFrames < int.MaxValue)
            {
                LockedFrames++;
            }
            Locked = true;
        }
        else if(Locked)
        {
            Release();
        }
    }

    internal static void Release()
    {
        if(EzSharedData.TryGet<HashSet<string>>(StopRequestsName, out var data))
        {
            data.Remove(Svc.PluginInterface.InternalName);
        }
        if(Locked) PluginLog.Debug("TextAdvance unlocked");
        Locked = false;
        LockedFrames = 0;
    }

    /// <summary>
    /// Lifestream 這一幀可以點 Talk：TextAdvance 不在，或鎖已經掛滿一整幀。
    /// 加鎖那一幀不點：TextAdvance 的 Tick 可能已在同一幀稍早放行了它自己的 Talk 點擊。
    /// </summary>
    internal static bool MayClickTalk => !EzSharedData.TryGet<HashSet<string>>(StopRequestsName, out _) || (Locked && LockedFrames >= 1);
}
