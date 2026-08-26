namespace Lifestream.Tasks;

internal static unsafe class TaskDesktopNotification
{
    internal static void Enqueue(string s)
    {
        P.TaskManager.Enqueue(() =>
        {
            // Framework 是 isPointer: true 的靜態位址,合法回 null。
            // 拿不到就當作「視窗是作用中的」→ 不發桌面通知(fail-closed)。
            var framework = CSFramework.Instance();
            if(framework != null && framework->WindowInactive)
            {
                Utils.TryNotify(s);
            }
        }, "TaskNotify");
    }
}
