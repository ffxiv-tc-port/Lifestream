using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lifestream.Data;
using Lifestream.Enums;
using SortMode = Lifestream.Data.SortMode;

namespace Lifestream.GUI;
public static unsafe class TabAddressBook
{
    public static readonly Dictionary<ResidentialAetheryteKind, string> ResidentialNames = new()
    {
        [ResidentialAetheryteKind.Gridania] = "Lavender Beds".Loc(),
        [ResidentialAetheryteKind.Limsa] = "Mist".Loc(),
        [ResidentialAetheryteKind.Uldah] = "Goblet".Loc(),
        [ResidentialAetheryteKind.Kugane] = "Shirogane".Loc(),
        [ResidentialAetheryteKind.Foundation] = "Empyreum".Loc(),
    };

    public static readonly Dictionary<SortMode, string> SortModeNames = new()
    {
        [SortMode.Manual] = "Manual (drag and drop)".Loc(),
        [SortMode.Name] = "Name (A-Z)".Loc(),
        [SortMode.NameReversed] = "Name (Z-A)".Loc(),
        [SortMode.World] = "World (A-Z)".Loc(),
        [SortMode.WorldReversed] = "World (Z-A)".Loc(),
        [SortMode.Plot] = "Plot (1-9)".Loc(),
        [SortMode.PlotReversed] = "Plot (9-1)".Loc(),
        [SortMode.Ward] = "Ward (1-9)".Loc(),
        [SortMode.WardReversed] = "Ward (9-1)".Loc(),
    };
    private static Guid CurrentDrag = Guid.Empty;

    public static void Draw()
    {
        InputWardDetailDialog.Draw();
        var selector = S.AddressBookFileSystemManager.FileSystem.Selector;
        selector.Draw(150f.Scale());
        ImGui.SameLine();
        if(C.AddressBookFolders.Count == 0)
        {
            var book = new AddressBookFolder() { IsDefault = true };
            S.AddressBookFileSystemManager.FileSystem.Create(book, "Default Book", out _);
        }
        if(ImGui.BeginChild("Child"))
        {
            if(selector.Selected != null)
            {
                var book = selector.Selected;
                DrawBook(book);
            }
            else
            {
                if(C.AddressBookFolders.TryGetFirst(x => x.IsDefault, out var value))
                {
                    selector.SelectByValue(value);
                }
                ImGuiEx.TextWrapped("To begin, select an address book to use.".Loc());
            }
        }
        ImGui.EndChild();
    }

    private static AddressBookEntry GetNewAddressBookEntry()
    {
        var entry = new AddressBookEntry();
        var h = HousingManager.Instance();
        if(h != null)
        {
            entry.Ward = h->GetCurrentWard() + 1;
            if(P.Territory.EqualsAny(Houses.Ingleside_Apartment, Houses.Kobai_Goten_Apartment, Houses.Lily_Hills_Apartment, Houses.Sultanas_Breath_Apartment, Houses.Topmast_Apartment))
            {
                entry.PropertyType = PropertyType.Apartment;
                entry.ApartmentSubdivision = h->GetCurrentDivision() == 2;
                entry.Apartment = h->GetCurrentRoom();
                entry.Apartment.ValidateRange(1, 9999);
            }
            else
            {
                entry.Plot = h->GetCurrentPlot() + 1;
                entry.Ward.ValidateRange(1, 30);
                entry.Plot.ValidateRange(1, 60);
            }
            if(Player.Available)
            {
                entry.World = (int)Player.Object.CurrentWorld.RowId;
            }
            var ra = Utils.GetResidentialAetheryteByTerritoryType(P.Territory);
            if(ra != null)
            {
                entry.City = ra.Value;
            }
        }
        return entry;
    }

    public static void DrawBook(AddressBookFolder book, bool readOnly = false)
    {
        if(!readOnly)
        {
            ImGuiEx.LineCentered(() =>
            {
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add New".Loc()))
                {
                    var h = HousingManager.Instance();
                    var entry = GetNewAddressBookEntry();
                    book.Entries.Add(entry);
                    InputWardDetailDialog.Entry = entry;
                }
                ImGui.SameLine();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Paste, "Paste".Loc()))
                {
                    try
                    {
                        var entry = EzConfig.DefaultSerializationFactory.Deserialize<AddressBookEntry>(Paste());
                        if(entry != null)
                        {
                            if(!entry.IsValid(out var error))
                            {
                                Notify.Error(LocText.CouldNotPasteFromClipboard.Loc(error));
                            }
                            else
                            {
                                book.Entries.Add(entry);
                            }
                        }
                        else
                        {
                            Notify.Error("Could not paste from clipboard".Loc());
                        }
                    }
                    catch(Exception e)
                    {
                        if(Utils.TryParseAddressBookEntry(Paste(), out var entry))
                        {
                            book.Entries.Add(entry);
                        }
                        else
                        {
                            Notify.Error(LocText.CouldNotPasteFromClipboard.Loc(e.Message));
                        }
                    }
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGuiEx.EnumCombo("##sort", ref book.SortMode, SortModeNames);
                ImGuiEx.Tooltip("Select sort mode for this address book".Loc());
                ImGui.SameLine();
                if(ImGui.Checkbox("Default".Loc(), ref book.IsDefault))
                {
                    if(book.IsDefault)
                    {
                        C.AddressBookFolders.Where(z => z != book).Each(z => z.IsDefault = false);
                    }
                }
                ImGuiEx.Tooltip("Default book automatically opens when you open plugin first time in a game session.".Loc());
            });
        }

        if(ImGui.BeginTable($"##addressbook", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Name".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("World".Loc());
            ImGui.TableSetupColumn("Ward".Loc());
            ImGui.TableSetupColumn("Plot".Loc());
            List<(Vector2 RowPos, Action AcceptDraw)> MoveCommands = [];
            ImGui.TableHeadersRow();

            var entryArray = AddressBookTable.Sorted(book.Entries, book.SortMode);

            for(var i = 0; i < entryArray.Count; i++)
            {
                var entry = entryArray[i];
                ImGui.PushID($"House{entry.GUID}");
                var rowPos = AddressBookTable.DrawNameCell(entry, book.Entries, book.SortMode, ref CurrentDrag);

                var moveItemIndex = i;
                MoveCommands.Add((rowPos, () =>
                {
                    if(book.SortMode == SortMode.Manual)
                    {
                        if(ImGui.BeginDragDropTarget())
                        {
                            if(ImGuiDragDrop.AcceptDragDropPayload("MoveRule", out Guid payload, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect))
                            {
                                MoveItemToPosition(book.Entries, (x) => x.GUID == payload, moveItemIndex);
                            }
                            ImGui.EndDragDropTarget();
                        }
                    }
                }
                ));

                ImGui.TableNextColumn();

                AddressBookTable.DrawLocationCells(entry);

                ImGui.PopID();
            }

            ImGui.EndTable();

            if(!readOnly)
            {
                foreach(var x in MoveCommands)
                {
                    ImGui.SetCursorPos(x.RowPos);
                    ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, ImGuiHelpers.GetButtonSize(" ").Y));
                    x.AcceptDraw();
                }
            }
        }
    }
}
