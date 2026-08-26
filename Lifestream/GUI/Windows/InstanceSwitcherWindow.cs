using ECommons.GameHelpers;
using ECommons.SimpleGui;
using Lifestream.Tasks;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Utility;

namespace Lifestream.GUI.Windows;

/// <summary>
/// DTR 分線快捷選單:點擊伺服器資訊列的分線圖示即開啟,列出各分線(標記目前所在),點選直接切線
/// (走既有 TaskChangeInstance 流程,含「先傳送至以太之光」與「切完回騎」選項)。
/// 尚無分線資料時提供一鍵初始化按鈕,取代上游「請先手動點一次以太之光」的死路提示。
/// </summary>
public class InstanceSwitcherWindow : Window
{
    private InstanceSwitcherWindow() : base("Lifestream: Instances".Loc(), ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        EzConfigGui.WindowSystem.AddWindow(this);
    }

    public override void Draw()
    {
        var current = S.InstanceHandler.GetInstance();
        if(current == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "This area is not divided into instances.".Loc());
            return;
        }

        var confirmed = S.InstanceHandler.IsInstanceCountConfirmed();
        var count = S.InstanceHandler.GetKnownInstanceCount();

        ImGuiEx.Text("Current instance: ??".Loc(current));

        if(!confirmed)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "The number of instances in this area is not known yet - the game only reveals it when the aetheryte's instance menu is opened.".Loc());
            if(ImGuiEx.Button("Read instance list from aetheryte".Loc(), TaskInitInstanceData.CanInitialize()))
            {
                TaskInitInstanceData.EnqueueWithTeleport();
            }
            if(!TaskInitInstanceData.CanInitialize())
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "Requires standing near an aetheryte (or enable \"teleport to the zone's aetheryte first\" in settings).".Loc());
            }
            ImGui.Separator();
        }

        for(var i = 1; i <= Math.Min(Math.Max(count, current), 9); i++)
        {
            var isCurrent = i == current;
            var label = $"{TaskChangeInstance.InstanceNumbers[i]} {"Instance ??".Loc(i)}";
            if(isCurrent) label += $" {"(current)".Loc()}";
            if(ImGuiEx.Button($"{label}##inst{i}", !isCurrent && S.InstanceHandler.CanChangeInstance()))
            {
                TaskRemoveAfkStatus.Enqueue();
                TaskChangeInstance.Enqueue(i);
            }
        }

        if(!S.InstanceHandler.CanChangeInstance() && confirmed)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "Cannot change instance right now.".Loc());
        }
    }
}
