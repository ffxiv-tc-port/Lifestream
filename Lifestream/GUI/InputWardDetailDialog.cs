using ECommons.Configuration;
using Lifestream.Data;
using NightmareUI.ImGuiElements;

namespace Lifestream.GUI;
public static class InputWardDetailDialog
{
    public static AddressBookEntry Entry = null;
    public static bool Open = false;
    public static void Draw()
    {
        if(Entry != null)
        {
            if(!ImGui.IsPopupOpen($"###ABEEditModal"))
            {
                Open = true;
                ImGui.OpenPopup($"###ABEEditModal");
            }
            if(ImGui.BeginPopupModal($"編輯 {Entry.Name}###ABEEditModal", ref Open, ImGuiWindowFlags.AlwaysAutoResize))
            {
                if(ImGui.BeginTable($"ABEEditTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Edit1", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("Edit2", ImGuiTableColumnFlags.WidthFixed, 250);

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"名稱：");
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    ImGui.InputTextWithHint($"##name", Entry.GetAutoName(), ref Entry.Name, 150);

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"別名：");
                    ImGuiEx.HelpMarker($"若啟用並設定別名，你可以透過「/li 別名」指令使用它。別名不區分大小寫。");
                    ImGui.TableNextColumn();
                    ImGui.Checkbox($"##alias", ref Entry.AliasEnabled);
                    if(Entry.AliasEnabled)
                    {
                        ImGui.SameLine();
                        ImGuiEx.InputWithRightButtonsArea(() => ImGui.InputText($"##aliasname", ref Entry.Alias, 150), () =>
                        {
                            AddressBookEntry existing = null;
                            if(Entry.Alias != "" && C.AddressBookFolders.Any(b => b.Entries.TryGetFirst(a => a != Entry && a.AliasEnabled && a.Alias.EqualsIgnoreCase(Entry.Alias), out existing)))
                            {
                                ImGuiEx.HelpMarker($"發現別名衝突：此別名已被 {existing?.Name.NullWhenEmpty() ?? existing?.GetAutoName()} 使用", EColor.RedBright, FontAwesomeIcon.ExclamationTriangle.ToIconString());
                            }
                        });
                    }

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"世界：");
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    WorldSelector.Instance.Draw(ref Entry.World);

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"住宅區：");
                    ImGui.TableNextColumn();
                    if(Entry.City.RenderIcon()) ImGui.SameLine(0, 1);
                    ImGuiEx.SetNextItemFullWidth();
                    Utils.ResidentialAetheryteEnumSelector($"##resdis", ref Entry.City);

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"房區：");
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    ImGui.InputInt($"##ward", ref Entry.Ward.ValidateRange(1, 30));

                    ImGui.TableNextColumn();
                    ImGuiEx.TextV($"房產類型：");
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    ImGuiEx.EnumRadio(ref Entry.PropertyType, true);

                    if(Entry.PropertyType == Enums.PropertyType.Apartment)
                    {
                        ImGui.TableNextColumn();
                        ImGuiEx.TextV($"");
                        ImGui.TableNextColumn();
                        ImGui.Checkbox("分租", ref Entry.ApartmentSubdivision);

                        ImGui.TableNextColumn();
                        ImGuiEx.TextV($"房號：");
                        ImGui.TableNextColumn();
                        ImGuiEx.SetNextItemFullWidth();
                        ImGui.InputInt($"##room", ref Entry.Apartment.ValidateRange(1, 99999));
                    }

                    if(Entry.PropertyType == Enums.PropertyType.House)
                    {
                        ImGui.TableNextColumn();
                        ImGuiEx.TextV($"房號：");
                        ImGui.TableNextColumn();
                        ImGuiEx.SetNextItemFullWidth();
                        ImGui.InputInt($"##plot", ref Entry.Plot.ValidateRange(1, 60));
                    }

                    ImGui.EndTable();
                }
                ImGuiEx.LineCentered(() =>
                {
                    if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Save, "儲存並關閉"))
                    {
                        Open = false;
                        EzConfig.Save();
                    }
                });
                ImGui.EndPopup();
            }
        }
        if(!Open) Entry = null;
    }
}
