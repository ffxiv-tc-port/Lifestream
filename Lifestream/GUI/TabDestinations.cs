using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Tasks.Utility;

namespace Lifestream.GUI;

/// <summary>
/// 自訂座標地點管理:名稱+區域+座標,可從當前位置一鍵儲存;
/// 執行 = 傳送到該區最近以太之光 + vnavmesh 走路(沒裝則標旗點)。指令:/li goto 名稱
/// </summary>
public static class TabDestinations
{
    private static string NewName = "";

    public static void Draw()
    {
        ImGuiEx.TextWrapped("Save named map positions and travel to them with \"/li goto <name>\". Lifestream picks the closest travel point to the saved position - including city aethernet shards, not just the main aetheryte - then walks the rest via vnavmesh. If vnavmesh is not installed, the point is flagged on the map instead. This is teleport + navigation, not position writing.".Loc());
        ImGui.Separator();

        ImGui.SetNextItemWidth(200f.Scale());
        ImGui.InputTextWithHint("##newDestName", "Name".Loc(), ref NewName, 64);
        ImGui.SameLine();
        if(ImGuiEx.Button("Save current position".Loc(), NewName.Trim() != "" && Player.Interactable))
        {
            var name = NewName.Trim();
            if(C.CustomDestinations.Any(x => x.Name.EqualsIgnoreCase(name)))
            {
                Notify.Error("A destination with this name already exists".Loc());
            }
            else
            {
                C.CustomDestinations.Add(new() { Name = name, Territory = P.Territory, Position = Player.Position });
                NewName = "";
                EzConfig.Save();
            }
        }

        if(C.CustomDestinations.Count == 0)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "No destinations saved yet.".Loc());
            return;
        }

        if(ImGui.BeginTable("##destinations", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Name".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Zone".Loc(), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Position".Loc());
            ImGui.TableSetupColumn("##actions");
            ImGui.TableHeadersRow();

            CustomDestinationRemove = null;
            foreach(var dest in C.CustomDestinations)
            {
                ImGui.PushID($"dest{dest.Name}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiEx.TextV(dest.Name);
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{ExcelTerritoryHelper.GetName(dest.Territory)}");
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{dest.Position.X:F1}, {dest.Position.Y:F1}, {dest.Position.Z:F1}");
                ImGui.TableNextColumn();
                if(ImGuiEx.Button("Go".Loc(), !P.TaskManager.IsBusy && Player.Interactable))
                {
                    TaskGotoDestination.Enqueue(dest);
                }
                ImGui.SameLine();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, enabled: ImGuiEx.Ctrl))
                {
                    CustomDestinationRemove = dest;
                }
                ImGuiEx.Tooltip("Hold CTRL and click to delete".Loc());
                ImGui.PopID();
            }
            ImGui.EndTable();

            if(CustomDestinationRemove != null)
            {
                C.CustomDestinations.Remove(CustomDestinationRemove);
                CustomDestinationRemove = null;
                EzConfig.Save();
            }
        }
    }

    private static Data.CustomDestination CustomDestinationRemove = null;
}
