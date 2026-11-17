using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Enums;
using SortMode = Lifestream.Data.SortMode;

namespace Lifestream.GUI;

/// <summary>
/// 地址簿表格（<see cref="TabAddressBook"/>）與 PaissaDB 匯入表格共用的排序與每列繪製。
/// 呼叫端負責 BeginTable／欄位設定／每列的 PushID／PopID，以及兩表各自的額外欄位與拖放目標。
/// </summary>
internal static class AddressBookTable
{
    /// <summary>依排序方式回傳新清單；原清單不動。</summary>
    public static List<T> Sorted<T>(List<T> entries, SortMode sortMode) where T : AddressBookEntry
    {
        List<T> entryArray;
        if(sortMode.EqualsAny(SortMode.Name, SortMode.NameReversed)) entryArray = [.. entries.OrderBy(x => x.Name.NullWhenEmpty() ?? x.GetAutoName()).ThenBy(x => x.Ward).ThenBy(x => x.Plot)];
        else if(sortMode.EqualsAny(SortMode.World, SortMode.WorldReversed)) entryArray = [.. entries.OrderBy(x => ExcelWorldHelper.GetName(x.World)).ThenBy(x => x.Name.NullWhenEmpty() ?? x.GetAutoName())];
        else if(sortMode.EqualsAny(SortMode.Ward, SortMode.WardReversed)) entryArray = [.. entries.OrderBy(x => x.Ward).ThenBy(x => x.Plot).ThenBy(x => x.Name.NullWhenEmpty() ?? x.GetAutoName())];
        else if(sortMode.EqualsAny(SortMode.Plot, SortMode.PlotReversed)) entryArray = [.. entries.OrderBy(x => x.Plot).ThenBy(x => x.Ward).ThenBy(x => x.Name.NullWhenEmpty() ?? x.GetAutoName())];
        else entryArray = [.. entries];
        if(sortMode.EqualsAny(SortMode.PlotReversed, SortMode.NameReversed, SortMode.WardReversed, SortMode.WorldReversed))
        {
            entryArray.Reverse();
        }
        return entryArray;
    }

    /// <summary>
    /// 開新列並畫名稱欄：前往按鈕、右鍵選單（刪除從 <paramref name="owner"/> 移除）、拖曳來源、快速傳送／別名標示。
    /// 回傳名稱欄起點游標位置（給拖放目標用）。
    /// </summary>
    public static Vector2 DrawNameCell<T>(T entry, List<T> owner, SortMode sortMode, ref Guid currentDrag) where T : AddressBookEntry
    {
        ImGui.TableNextRow();
        if(currentDrag == entry.GUID)
        {
            var color = GradientColor.Get(EColor.Green, EColor.Green with { W = EColor.Green.W / 4 }, 500).ToUint();
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, color);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, color);
        }
        ImGui.TableNextColumn();
        var rowPos = ImGui.GetCursorPos();
        var bsize = ImGuiHelpers.GetButtonSize("A") with { X = ImGui.GetContentRegionAvail().X };
        ImGui.PushStyleColor(ImGuiCol.Button, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, Vector2.Zero);
        var col = entry.IsHere();
        if(col) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);
        if(ImGui.Button($"{entry.Name.NullWhenEmpty() ?? entry.GetAutoName()}###entry", bsize))
        {
            if(Player.Interactable && !P.TaskManager.IsBusy)
            {
                entry.GoTo();
            }
        }
        if(col) ImGui.PopStyleColor();
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup($"ABMenu {entry.GUID}");
        }
        if(ImGui.BeginPopup($"ABMenu {entry.GUID}"))
        {
            if(ImGui.MenuItem(LocText.CopyChatFriendlyName.Loc()))
            {
                Copy(entry.GetAddressString());
            }
            ImGui.Separator();
            if(ImGui.MenuItem("Export to Clipboard".Loc()))
            {
                Copy(EzConfig.DefaultSerializationFactory.Serialize(entry, false));
            }
            if(entry.Alias != "")
            {
                ImGui.MenuItem("Enable Alias: ??".Loc(entry.Alias), ref entry.AliasEnabled);
            }
            if(ImGui.MenuItem("Edit...".Loc()))
            {
                InputWardDetailDialog.Entry = entry;
            }
            if(ImGui.MenuItem("Delete".Loc()))
            {
                if(ImGuiEx.Ctrl)
                {
                    new TickScheduler(() => owner.Remove(entry));
                }
                else
                {
                    Svc.Toasts.ShowError(LocText.HoldCtrlToDeleteEntry.Loc());
                }
            }
            ImGuiEx.Tooltip("Hold CTRL and click to delete".Loc());
            ImGui.EndPopup();
        }
        if(ImGui.BeginDragDropSource())
        {
            ImGuiDragDrop.SetDragDropPayload("MoveRule", entry.GUID);
            currentDrag = entry.GUID;
            InternalLog.Verbose($"DragDropSource = {entry.GUID}");
            if(sortMode == SortMode.Manual)
            {
                ImGui.SetTooltip(LocText.ReorderOrMoveToOtherFolder.Loc());
            }
            else
            {
                ImGui.SetTooltip("Move to other folder".Loc());
            }
            ImGui.EndDragDropSource();
        }
        else if(currentDrag == entry.GUID)
        {
            InternalLog.Verbose($"Current drag reset!");
            currentDrag = Guid.Empty;
        }

        if(entry.IsQuickTravelAvailable())
        {
            ImGui.PushFont(UiBuilder.IconFont);
            var size = ImGui.CalcTextSize(FontAwesomeIcon.BoltLightning.ToIconString());
            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - size.X - ImGui.GetStyle().FramePadding.X);
            ImGuiEx.Text(ImGuiColors.DalamudYellow, FontAwesomeIcon.BoltLightning.ToIconString());
            ImGui.PopFont();
        }
        else if(entry.AliasEnabled)
        {
            var size = ImGui.CalcTextSize(entry.Alias);
            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - size.X - ImGui.GetStyle().FramePadding.X);
            ImGuiEx.Text(ImGuiColors.DalamudGrey3, entry.Alias);
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        return rowPos;
    }

    /// <summary>世界、區、地號三欄；呼叫端已移到世界欄（先呼叫過 <c>TableNextColumn</c>）。</summary>
    public static void DrawLocationCells(AddressBookEntry entry)
    {
        var wcol = ImGuiColors.DalamudGrey;
        if(Player.Available && Player.Object.CurrentWorld.ValueNullable?.DataCenter.RowId == ExcelWorldHelper.Get((uint)entry.World)?.DataCenter.RowId)
        {
            wcol = ImGuiColors.DalamudGrey;
        }
        else
        {
            if(!S.Data.DataStore.DCWorlds.Contains(ExcelWorldHelper.GetName(entry.World))) wcol = ImGuiColors.DalamudGrey3;
        }
        if(Player.Available && Player.Object.CurrentWorld.RowId == entry.World) wcol = new Vector4(0.9f, 0.9f, 0.9f, 1f);

        ImGuiEx.TextV(wcol, ExcelWorldHelper.GetName(entry.World));

        ImGui.TableNextColumn();
        if(entry.City.RenderIcon())
        {
            ImGuiEx.Tooltip($"{TabAddressBook.ResidentialNames.SafeSelect(entry.City)}");
            ImGui.SameLine(0, 1);
        }

        ImGuiEx.Text($"{entry.Ward.FancyDigits()}");

        ImGui.TableNextColumn();

        if(entry.PropertyType == PropertyType.House)
        {
            ImGuiEx.Text(Colors.TabGreen, Lang.SymbolPlot);
            ImGuiEx.Tooltip("Plot".Loc());
            ImGui.SameLine(0, 0);
            ImGuiEx.Text($"{entry.Plot.FancyDigits()}");
        }
        if(entry.PropertyType == PropertyType.Apartment)
        {
            if(!entry.ApartmentSubdivision)
            {
                ImGuiEx.Text(Colors.TabYellow, Lang.SymbolApartment);
                ImGuiEx.Tooltip("Apartment".Loc());
            }
            else
            {
                ImGuiEx.Text(Colors.TabYellow, Lang.SymbolSubdivision);
                ImGuiEx.Tooltip("Subdivision Apartment".Loc());
            }
            ImGui.SameLine(0, 0);
            ImGuiEx.Text($"{entry.Apartment.FancyDigits()}");
        }
    }
}
