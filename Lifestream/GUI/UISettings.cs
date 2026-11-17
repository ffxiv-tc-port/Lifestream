using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Systems;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;
using NightmareUI;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;
using System.Globalization;
using Action = System.Action;

namespace Lifestream.GUI;

internal static unsafe partial class UISettings
{
    private static string AddNew = "";

    /// <summary>「去不了的世界」區塊裡待加入的世界，0 = 還沒選。</summary>
    private static int AddUnavailableWorld = 0;

    /// <summary>
    /// 挑世界加進排除清單用。刻意自己開一個而不是用共用單例 <c>WorldSelector.Instance</c>：
    /// 這裡要列出<b>全部</b>世界(含已經被排除的)，而單例會被別的分頁改設定。
    /// </summary>
    private static readonly WorldSelector UnavailableWorldSelector = new("##addunavailableworld")
    {
        EmptyName = "Select a world".Loc(),
    };
    internal static void Draw()
    {
        NuiTools.ButtonTabs([[new("General".Loc(), () => Wrapper(DrawGeneral)), new("Overlay".Loc(), () => Wrapper(DrawOverlay)), new("Teleport Panel".Loc(), () => Wrapper(DrawTeleportPanel))], [new("Expert".Loc(), () => Wrapper(DrawExpert)), new("Service Accounts".Loc(), () => Wrapper(UIServiceAccount.Draw)), new("Travel Block".Loc(), TabTravelBan.Draw)]]);
    }

    private static void Wrapper(Action action)
    {
        ImGui.Dummy(new(5f));
        action();
    }

    /// <summary>
    /// 傳送面板的設定，以及兩個有帳號風險、預設關閉的進階選項。
    /// 這兩個選項的說明文字刻意寫得很直白 —— 使用者是在知情的前提下自行承擔風險，
    /// 所以說明不能含糊。
    /// </summary>
    private static void DrawTeleportPanel()
    {
        new NuiBuilder()
        .Section("Teleport Panel".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("A searchable teleport window with favorites, renames and a map preview. Open it with \"/li panel\".".Loc());
            if(ImGui.Button("Open teleport panel".Loc())) S.Gui.TeleportPanelWindow.IsOpen = true;
            ImGui.SameLine();
            if(ImGui.Button($"★ {"Open favorites window".Loc()}")) S.Gui.TeleportFavoritesWindow.IsOpen = true;
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "The favorites window (\"/li fav\") is a separate list where you can put your favorites in your own order and sort them into your own categories. The teleport panel itself is unchanged.".Loc());
            ImGui.Checkbox("Show map preview".Loc(), ref C.TeleportPanelShowMap);
            ImGui.Checkbox("Hide city aethernet shards while in a party".Loc(), ref C.TeleportPanelHideAethernetInParty);
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Favorites and renames are shared with the Lifestream overlay - anything you star here also stars there.".Loc());
        })
        .Draw();

        new NuiBuilder()
        .Section("Import from DailyRoutines".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("Imports favorites, remarks and custom landing positions from DailyRoutines' BetterTeleport module. Both plugins key this data by aetheryte row id, so it transfers 1:1. Existing entries are never overwritten.".Loc());
            var exists = DailyRoutinesImport.Exists;
            if(ImGuiEx.Button("Import BetterTeleport.json".Loc(), exists))
            {
                DailyRoutinesImport.Import();
            }
            if(!exists)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, $"{"File not found:".Loc()} {DailyRoutinesImport.DefaultPath}");
            }
            if(DailyRoutinesImport.LastResult != null)
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, DailyRoutinesImport.LastResult);
            }
        })
        .Draw();

        new NuiBuilder()
        .Section("Custom landing positions".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("You can save a custom landing spot for each aetheryte. After teleporting there, Lifestream will bring you to that spot instead of leaving you at the crystal. Set them from the right-click menu in the teleport panel.".Loc());
            ImGui.Checkbox("Enable custom landing positions".Loc(), ref C.EnableAetheryteLanding);
            ImGuiEx.HelpMarker("Off by default. When enabled, the safe route is used: normal teleport, then vnavmesh walks you to the spot. Nothing is written to game memory.".Loc());

            if(C.EnableAetheryteLanding)
            {
                ImGui.Indent();
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Safe mode: teleport + vnavmesh pathfinding. If vnavmesh is not installed the spot is flagged on your map instead.".Loc());
                ImGui.Dummy(new(5f));

                // 落點離某座城內乙太之光比離剛落地的主水晶近很多時，先搭一段都市傳送網再走完最後一段。
                // 0 = 關(從主水晶一路走過去)。
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderFloat("Take the aethernet first when it saves at least, yalms".Loc(), ref C.AetheryteLandingRelayGain, 0f, 200f, "%.0f");
                ImGuiEx.HelpMarker("When your landing spot is much closer to one of the city's aethernet shards than to the aetheryte you teleported to, Lifestream rides the aethernet there first and only walks the last stretch - that is the difference between crossing Solution Nine on foot and a short walk. 0 turns this off and always walks the whole way. Straight-line distance, not path length. Needs vnavmesh; without it the spot is just flagged on the map as before.".Loc());
                ImGui.Dummy(new(5f));

                ImGui.Checkbox("Use direct position write instead of walking".Loc(), ref C.AetheryteLandingDirectWrite);
                ImGui.Indent();
                ImGuiEx.TextWrapped(EColor.RedBright, LocText.MemoryTeleportWarning.Loc());
                ImGuiEx.TextWrapped(EColor.RedBright, "DailyRoutines, where this feature comes from, keeps its own list of zones with server-side movement-speed detection, and on the CN/TC clients it refuses to position-teleport inside those zones unless the account is premium - that is their own evidence that it is detectable.".Loc());
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, LocText.MemoryTeleportRefusalNote.Loc());
                ImGui.Unindent();
                ImGui.Unindent();
            }

            if(C.AetheryteLandings.Count > 0)
            {
                ImGui.Dummy(new(5f));
                ImGuiEx.Text($"{"Saved landing positions".Loc()}: {C.AetheryteLandings.Count}");
                uint toRemove = 0;
                foreach(var (id, pos) in C.AetheryteLandings)
                {
                    var row = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(id);
                    var name = C.Renames.TryGetValue(id, out var rn) && rn != "" ? rn
                        : row?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } pn ? pn
                        : row?.AethernetName.ValueNullable?.Name.ToString() is { Length: > 0 } an ? an
                        : $"#{id}";
                    ImGuiEx.Text($"{name}  ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
                    ImGui.SameLine();
                    if(ImGui.SmallButton($"{"Delete".Loc()}##landing{id}")) toRemove = id;
                }
                if(toRemove > 0)
                {
                    C.AetheryteLandings.Remove(toRemove);
                    EzConfig.Save();
                }
            }
        })
        .Draw();

        new NuiBuilder()
        .Section("Teleport to the aethernet shard you are standing on".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("The game refuses an aethernet teleport when the destination is the shard you are already standing at (\"This is your current location.\"). This option patches that check out, which is useful together with custom landing positions - you can re-teleport to snap back to your saved spot.".Loc());
            ImGuiEx.TextWrapped(EColor.RedBright, "WARNING - use at your own risk. This is a memory patch: it rewrites two bytes of the game's code so it stops refusing, and the client then sends a teleport request the unmodified client would never send. Off by default.".Loc());
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "The \"you have not attuned to this aetheryte\" check is left untouched. The patch is verified before it is written: each signature must match exactly once and the byte found must be the expected conditional jump, otherwise nothing is patched at all. The original bytes are restored when you turn this off or unload the plugin.".Loc());

            ImGuiEx.TextWrapped(EColor.RedBright, SuspendedNotice.Loc());

            var enabled = C.SameAethernetTeleport;
            if(ImGui.Checkbox("Allow teleporting to the aethernet shard you are standing on".Loc(), ref enabled))
            {
                // 暫停中只存設定值：Enable／Disable 都不寫記憶體，也不把使用者的勾選退回。
                C.SameAethernetTeleport = enabled;
                EzConfig.Save();
                if(enabled) SameAethernetTeleportPatch.Enable();
                else SameAethernetTeleportPatch.Disable();
            }
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(SuspendedNotice.Loc());

            if(SameAethernetTeleportPatch.IsApplied)
            {
                ImGuiEx.Text(ImGuiColors.HealerGreen, "Patch active.".Loc());
            }
            else if(SameAethernetTeleportPatch.ResolveError != null && SameAethernetTeleportPatch.ResolveError != SameAethernetTeleportPatch.SuspendedReason)
            {
                ImGuiEx.TextWrapped(EColor.RedBright, $"{"Patch not applied:".Loc()} {SameAethernetTeleportPatch.ResolveError}");
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "This usually means the game updated and the signature moved. Nothing was written - the feature is simply off.".Loc());
            }
        })
        .Draw();
    }

    private const string SuspendedNotice = "Currently has no effect - suspended pending a decision. It used to write to the wrong place in memory and never worked, so it is turned off for now; your choice here is kept.";

    // 坐騎名稱表在執行中不會變（語言不在執行中切換），第一次畫設定頁時建一次。
    private static Dictionary<int, string> MountNames;

    internal static Dictionary<int, string> BuildMountNames(IEnumerable<Mount> sheet, string rouletteLabel)
        => [new KeyValuePair<int, string>(0, rouletteLabel), .. sheet.Where(x => x.Singular != "").ToDictionary(x => (int)x.RowId, x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Singular.GetText()))];

    private static void DrawExpert()
    {
        new NuiBuilder()
        .Section("Expert Settings".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Slow down aetheryte teleporting".Loc(), ref C.SlowTeleport);
            ImGuiEx.HelpMarker("Slows down aethernet teleportation by specified amount.".Loc());
            if(C.SlowTeleport)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.DragInt("Teleport delay (ms)".Loc(), ref C.SlowTeleportThrottle);
                ImGui.Unindent();
            }
            ImGuiEx.CheckboxInverted("Skip waiting until game screen is ready".Loc(), ref C.WaitForScreenReady);
            ImGuiEx.HelpMarker("Enable this option for faster teleports but be careful that you may get stuck.".Loc());
            ImGui.Checkbox("Hide progress bar".Loc(), ref C.NoProgressBar);
            ImGuiEx.HelpMarker("Hiding progress bar leaves you with no way to stop Lifestream from executing it's tasks.".Loc());
            ImGuiEx.CheckboxInverted("Don't walk to nearby aetheryte on world change command from greater distance".Loc(), ref C.WalkToAetheryte);
            ImGui.Checkbox("Progress overlay at top of the sreen".Loc(), ref C.ProgressOverlayToTop);
            if(ImGui.Button("Reset progress bar".Loc()))
            {
                C.NoProgressBar = false;
                ProgressOverlay.ResetOverlay();
                Notify.Success("Progress bar has been reset.".Loc());
            }
            ImGuiEx.HelpMarker("Use this if the progress bar has stopped showing up. It re-enables the bar and re-creates its window, which also clears the error state that Dalamud locks a window into after a drawing error. The bar's position is recalculated from the current screen size every frame, so it can not get stuck off-screen.".Loc());
            ImGui.Checkbox("Allow custom alias and house alias to override built-in commands".Loc(), ref C.AllowCustomOverrides);
            ImGui.Indent();
            ImGuiEx.TextWrapped(EColor.RedBright, "Warning! Other plugins may rely on built-in commands. Ensure that it is not the case if you decide to enable this option and override commands.".Loc());
            ImGui.Unindent();
        })
        .Draw();
    }
}
