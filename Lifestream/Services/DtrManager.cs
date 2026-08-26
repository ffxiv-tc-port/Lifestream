using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Services;
public class DtrManager : IDisposable
{
    public static Dictionary<int, SeString> InstanceNumbers = new()
    {
        [1] = "\ue0b1",
        [2] = "\ue0b2",
        [3] = "\ue0b3",
        [4] = "\ue0b4",
        [5] = "\ue0b5",
        [6] = "\ue0b6",
        [7] = "\ue0b7",
        [8] = "\ue0b8",
        [9] = "\ue0b9",
    };
    public static string Name = "LifestreamInstance";
    public IDtrBarEntry Entry;
    private int lastShownInstance = -1;

    private DtrManager()
    {
        Entry = Svc.DtrBar.Get(Name);
        Entry.Shown = false;
        Entry.OnClick = OnDtrClick;
        Svc.Framework.Update += OnUpdate;
        Refresh();
    }

    /// <summary>
    /// 左鍵:開/關分線快捷選單(尚無分線資料時,選單裡直接提供初始化按鈕);
    /// 右鍵:若還沒有分線資料就直接執行初始化,否則開選單。
    /// </summary>
    private void OnDtrClick(DtrInteractionEvent ev)
    {
        if(ev.ClickType == MouseClickType.Right && !S.InstanceHandler.IsInstanceCountConfirmed() && TaskInitInstanceData.CanInitialize())
        {
            TaskInitInstanceData.EnqueueWithTeleport();
            return;
        }
        S.Gui.InstanceSwitcherWindow.IsOpen ^= true;
    }

    public void Refresh() => lastShownInstance = -1;

    private void OnUpdate(IFramework framework)
    {
        if(!EzThrottler.Throttle("LifestreamDtrRefresh", 500)) return;
        // 上游 bug 修正:原本以 territory id 當 key 查 1–9 的分線圖示字典,DTR 永遠不會顯示;
        // 且同地圖切換分線不會觸發 TerritoryChanged,故改為輪詢分線編號。
        var instance = C.EnableDtrBar ? S.InstanceHandler.GetInstance() : 0;
        if(instance == lastShownInstance) return;
        lastShownInstance = instance;
        var str = InstanceNumbers.SafeSelect(instance);
        if(instance > 0 && str != null)
        {
            Entry.Text = str;
            Entry.Tooltip = $"{"You are in instance ??".Loc(instance)}\n{"Click to switch instance".Loc()}";
            Entry.Shown = true;
        }
        else
        {
            Entry.Shown = false;
            if(S.Gui.InstanceSwitcherWindow != null) S.Gui.InstanceSwitcherWindow.IsOpen = false;
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        Entry.Remove();
        Entry = null;
    }
}
