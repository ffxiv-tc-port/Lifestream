using ECommons.GameHelpers;
using Lifestream.Paissa;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;

namespace Lifestream.GUI;
public static class TabUtility
{
    public static int TargetWorldID = 0;
    private static WorldSelector WorldSelector = new()
    {
        DisplayCurrent = false,
        EmptyName = "Disabled".Loc(),
        ShouldHideWorld = (x) => x == Player.Object?.CurrentWorld.RowId
    };
    private static PaissaImporter PaissaImporter = new();

    public static void Draw()
    {
        new NuiBuilder()
            .Section("Shutdown game upon arriving to the world".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                WorldSelector.Draw(ref TargetWorldID);
            })
            .Section("Import house listings from PaissaDB".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                PaissaImporter.Draw();
            })
            .Draw();
    }
}
