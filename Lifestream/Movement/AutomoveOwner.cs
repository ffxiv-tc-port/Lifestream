using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace Lifestream.Movement;

/// <summary>
/// 本插件所有 <c>/automove</c> 的唯一出口，並每幀對帳遊戲真值（照 AutoRetainer AutomoveManager）：
/// 佇列中止會連「關閉」那一步一起丟掉，所以「我們開的、佇列已空、遊戲還在自動前進」由 <see cref="Tick"/> 補送關閉。
/// 只在框架執行緒呼叫。
/// </summary>
internal static class AutomoveOwner
{
    /// <summary>我們送過 on 之後設；看到遊戲回報已停才清（送 off 不清，off 沒生效時 Tick 會再補）。</summary>
    private static bool EngagedByUs = false;
    private static long StaleSince = long.MaxValue;
    /// <summary>兩段 Enqueue 之間、或鏈正要排自己的 off 時的正常空檔，不當成中止。</summary>
    private const int StaleGraceMs = 1000;
    /// <summary>補送上限：讀不到真值（恆當成前進中）時不會每秒送一次、搶掉使用者自己開的自動前進。</summary>
    private const int MaxRescues = 5;
    private static int Rescues = 0;

    internal static void On()
    {
        EngagedByUs = true;
        StaleSince = long.MaxValue;
        Rescues = 0;
        Chat.ExecuteCommand("/automove on");
    }

    internal static void Off()
    {
        StaleSince = long.MaxValue;
        Chat.ExecuteCommand("/automove off");
    }

    /// <summary>停止點呼叫：是我們開的而且還在前進就立刻關，不等 <see cref="Tick"/> 的寬限。</summary>
    internal static void StopIfOurs(string source)
    {
        if(!EngagedByUs || !Player.Available || !IsAutoRunning()) return;
        PluginLog.Information($"[Automove] {source}：自動前進是 Lifestream 開的，送出 /automove off。");
        Off();
    }

    internal static void Tick()
    {
        if(!EngagedByUs) return;
        if(!Player.Available || !IsAutoRunning())
        {
            // 換區／登出時遊戲自己會停；看到已停（我們的 off、使用者或互動停的）就不再是我們的。
            EngagedByUs = false;
            StaleSince = long.MaxValue;
            return;
        }
        if(P.TaskManager.IsBusy)
        {
            StaleSince = long.MaxValue;
            return;
        }
        if(StaleSince == long.MaxValue)
        {
            StaleSince = Environment.TickCount64;
            return;
        }
        if(Environment.TickCount64 - StaleSince < StaleGraceMs) return;
        if(!EzThrottler.Throttle("Lifestream.AutomoveOwner.Rescue", 1000)) return;
        if(Rescues >= MaxRescues)
        {
            PluginLog.Warning($"[Automove] 已補送 {MaxRescues} 次 /automove off 仍讀到自動前進中，不再補送。");
            EngagedByUs = false;
            return;
        }
        Rescues++;
        PluginLog.Information("[Automove] 任務佇列已空但自動前進還開著（負責關閉的那一步被中止丟掉了），送出 /automove off。");
        Chat.ExecuteCommand("/automove off");
    }

    /// <summary>位址解不出來時當成「還在前進」：寧可多送一次 off，也不讓角色一直跑。</summary>
    private static bool IsAutoRunning()
    {
        try
        {
            return InputManager.IsAutoRunning();
        }
        catch(Exception e)
        {
            if(EzThrottler.Throttle("Lifestream.AutomoveOwner.SigFailure", 600000))
            {
                PluginLog.Warning($"[Automove] 讀不到自動前進狀態（{e.Message}），當成仍在前進。");
            }
            return true;
        }
    }
}
