using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Game;
using Lifestream.Tasks.SameWorld;
using Lifestream.Paissa;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;

namespace Lifestream.GUI;
public static class TabUtility
{
    private static WorldSelector WorldSelector = new()
    {
        DisplayCurrent = false,
        EmptyName = "Disabled".Loc(),
        // 去不了的世界永遠不會抵達，所以也不該被選成「抵達就關遊戲」的目標。
        ShouldHideWorld = (x) => x == Player.Object?.CurrentWorld.RowId || PublicWorlds.IsUnavailable(x)
    };
    private static PaissaImporter PaissaImporter = new();
    private static ulong FriendEstateContentId;
    private static FriendEstateKind? FriendEstateTarget;

    public static void Draw()
    {
        new NuiBuilder()
            .Section("Shutdown game upon arriving to the world".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                WorldSelector.Draw(ref Movement.ShutdownOnArrival.TargetWorldID);
            })
            .Section("Teleport to a friend's estate".Loc())
            .Widget(DrawFriendEstate)
            .Section("Import house listings from PaissaDB".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                PaissaImporter.Draw();
            })
            .Draw();
    }

    private static void DrawFriendEstate()
    {
        if(!TaskFriendEstateTeleport.IsSupported)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Teleporting to a friend's estate is not available in this game version.".Loc());
            return;
        }
        var friends = TaskFriendEstateTeleport.SnapshotFriends();
        if(friends.Count == 0)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Your friend list is not loaded. Open the in-game friend list once and try again.".Loc());
            return;
        }
        static string Label(TaskFriendEstateTeleport.FriendRef f) => $"{f.Name} @ {ExcelWorldHelper.GetName(f.HomeWorld)}";
        var selected = friends.FirstOrDefault(x => x.ContentId == FriendEstateContentId);
        ImGui.SetNextItemWidth(220f.Scale());
        if(ImGui.BeginCombo("##friendEstateFriend", selected.ContentId == 0 ? "Select a friend".Loc() : Label(selected), ImGuiComboFlags.HeightLarge))
        {
            foreach(var f in friends.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if(ImGui.Selectable($"{Label(f)}##{f.ContentId}", f.ContentId == FriendEstateContentId)) FriendEstateContentId = f.ContentId;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f.Scale());
        if(ImGui.BeginCombo("##friendEstateKind", TaskFriendEstateTeleport.KindName(FriendEstateTarget)))
        {
            foreach(var k in (FriendEstateKind?[])[null, FriendEstateKind.Private, FriendEstateKind.Apartment, FriendEstateKind.FreeCompany])
            {
                if(ImGui.Selectable(TaskFriendEstateTeleport.KindName(k), k == FriendEstateTarget)) FriendEstateTarget = k;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if(ImGuiEx.Button("Teleport".Loc(), selected.ContentId != 0 && !P.TaskManager.IsBusy && Player.Interactable))
        {
            TaskFriendEstateTeleport.Enqueue(selected, FriendEstateTarget);
        }
        ImGuiEx.HelpMarker("Uses the game's friend list estate teleport. Automatic picks the private estate first, then the apartment, then the FC estate. If your friend is on another world, travels there first. Teleport fees are paid as usual; the aetheryte ticket prompt is left for you to answer.".Loc());
    }
}
