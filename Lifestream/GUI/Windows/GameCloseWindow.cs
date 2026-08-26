using NightmareUI.ImGuiElements;

namespace Lifestream.GUI.Windows;
public class GameCloseWindow : Window
{
    public int World = 0;
    private WorldSelector WorldSelector = new()
    {
        EmptyName = "Disabled".Loc(),
    };
    public GameCloseWindow() : base("Lifestream Scheduler".Loc() + "###LifestreamScheduler", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)
    {
        RespectCloseHotkey = false;
        ShowCloseButton = false;
    }

    public override void Draw()
    {
        if(World == 0)
        {
            ImGuiEx.Text("Inactive, select target world".Loc());
        }
        else
        {
            ImGuiEx.Text(EColor.RedBright, "Active".Loc());
        }
        ImGuiEx.Text("Shutdown game upon arriving to:".Loc());
        ImGui.SetNextItemWidth(200f.Scale());
        WorldSelector.Draw(ref World);
    }
}
