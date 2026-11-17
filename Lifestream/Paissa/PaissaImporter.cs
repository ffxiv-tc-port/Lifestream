using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using static Lifestream.Paissa.PaissaData;

namespace Lifestream.Paissa;

public class PaissaImporter
{
    private static Guid CurrentDrag = Guid.Empty;
    private string ID;
    private string folderText = "No folder yet...".Loc();
    private bool buttonDisabled = false;
    private bool textToCopy = false;
    private DateTime disableEndTime;
    private const int BUTTON_DISABLE_TIME = 5; // in seconds
    private PaissaStatus status = PaissaStatus.Idle;
    public static Dictionary<string, PaissaAddressBookFolder> Folders = [];
    private Task<PaissaResult>? _importTask = null;

    public PaissaImporter(string id = "##paissa")
    {
        ID = id;
    }

    public void Draw()
    {
        ImGui.PushID(ID);

        if(buttonDisabled && DateTime.Now >= disableEndTime)
        {
            buttonDisabled = false;
            status = PaissaStatus.Idle;
        }
        var isDisabled = buttonDisabled;
        if(isDisabled) ImGui.BeginDisabled();

        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Download, "Import from PaissaDB".Loc(), enabled: Player.Available))
        {
            PluginLog.Debug("PaissaDB import process initiated!");
            buttonDisabled = true;
            disableEndTime = DateTime.Now.AddSeconds(BUTTON_DISABLE_TIME);
            status = PaissaStatus.Progress;

            _importTask = PaissaUtils.ImportFromPaissaDBAsync();
        }

        if(isDisabled) ImGui.EndDisabled();

        if(_importTask != null && _importTask.IsCompleted)
        {
            try
            {
                var result = _importTask.Result;
                folderText = result.FolderText;
                status = result.Status;
            }
            catch(Exception ex)
            {
                PluginLog.Error($"PaissaDB import failed: {ex}");
                status = PaissaStatus.Error;
            }

            _importTask = null;
        }

        ImGui.SameLine();

        ImGui.TextColored(PaissaUtils.GetStatusColorFromStatus(status), PaissaUtils.GetStatusStringFromStatus(status));

        if(Player.Available && Folders.TryGetValue(Player.CurrentWorld, out var book))
        {
            DrawPaissaBook(book);
        }

        ImGui.PopID();
    }

    public static void DrawPaissaBook(PaissaAddressBookFolder book)
    {
        if(book.Entries.Count == 0)
        {
            ImGuiEx.Text("No houses are currently available for bidding!".Loc());
        }
        else if(ImGui.BeginTable($"##addressbook", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Name".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Size".Loc());
            ImGui.TableSetupColumn("Bids".Loc());
            ImGui.TableSetupColumn("Allowed Tenants".Loc());
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

                // Size

                ImGuiEx.Text($"{PaissaUtils.GetSizeString(entry.Size)}");
                ImGui.SameLine();
                ImGuiEx.Tooltip("Size".Loc());

                ImGui.TableNextColumn();

                // Bids

                ImGuiEx.Text($"{entry.Bids}");
                ImGui.SameLine();
                ImGuiEx.Tooltip("Bids".Loc());

                ImGui.TableNextColumn();

                // Allowed Tenants

                ImGuiEx.Text($"{PaissaUtils.GetAllowedTenantsStringFromPurchaseSystem(entry.AllowedTenants)}");
                ImGui.SameLine();
                ImGuiEx.Tooltip("Allowed Tenants".Loc());

                ImGui.TableNextColumn();

                AddressBookTable.DrawLocationCells(entry);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }
}
