using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.SimpleGui;
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
    /// 左鍵:開/關分線快捷選單(尚無分線資料時,選單裡有初始化按鈕);右鍵:開/關 Lifestream 設定。
    /// 刻意不在點擊上直接初始化分線資料:初始化可能先傳送到本區以太之光,只放在選單按鈕上。
    /// </summary>
    private void OnDtrClick(DtrInteractionEvent ev)
    {
        if(ev.ClickType != MouseClickType.Left)
        {
            EzConfigGui.Toggle();
            return;
        }
        S.Gui.InstanceSwitcherWindow.IsOpen ^= true;
    }

    public void Refresh() => lastShownInstance = -1;

    private void OnUpdate(IFramework framework)
    {
        if(!EzThrottler.Throttle("LifestreamDtrRefresh", 500)) return;
        // 圖示字典的 key 是分線編號(1–9)不是 territory id;
        // 同地圖切換分線不會觸發 TerritoryChanged,所以輪詢分線編號。
        var instance = C.EnableDtrBar ? S.InstanceHandler.GetInstance() : 0;
        if(instance == lastShownInstance) return;
        lastShownInstance = instance;
        var str = InstanceNumbers.SafeSelect(instance);
        if(instance > 0 && str != null)
        {
            Entry.Text = str;
            Entry.Tooltip = $"{"You are in instance ??".Loc(instance)}\n{"Left click: open/close instance menu".Loc()}\n{"Right click: open/close Lifestream settings".Loc()}";
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
