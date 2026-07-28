using ECommons.Funding;

namespace Lifestream.GUI;

internal static unsafe class MainGui
{
    internal static void Draw()
    {
        PatreonBanner.DrawRight();
        ImGuiEx.EzTabBar("LifestreamTabs", PatreonBanner.Text,
            ("Address Book".Loc(), TabAddressBook.Draw, null, true),
            ("House Registration".Loc(), UIHouseReg.Draw, null, true),
            ("Custom Alias".Loc(), TabCustomAlias.Draw, null, true),
            ("Utility".Loc(), TabUtility.Draw, null, true),
            ("Settings".Loc(), UISettings.Draw, null, true),
            ("Help".Loc(), DrawHelp, null, true),
            ("Debug".Loc(), UIDebug.Draw, ImGuiColors.DalamudGrey3, true)
            );
    }

    private static void DrawHelp()
    {
        ImGuiEx.TextWrapped(Lang.Help);
    }
}
