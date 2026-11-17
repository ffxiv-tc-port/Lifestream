using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods.TerritorySelection;
using ECommons.MathHelpers;
using ECommons.SplatoonAPI;
using Lifestream.Data;
using Lifestream.Tasks.SameWorld;
using Newtonsoft.Json;
using System.Globalization;
using System.Runtime.CompilerServices;
using NightmareUI.ImGuiElements;
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace Lifestream.GUI;

public static partial class TabCustomAlias
{
    // 鏈的連線：綠線＝散布範圍邊緣（沒有散布就是中線），黃線＝再往外 0.25 米。
    private static void DrawSplatoon(CustomAlias alias)
    {
        if(!Splatoon.IsConnected()) return;
        foreach(var x in Utils.GenerateGroupConnectionLines(alias))
        {
            var line = S.Ipc.SplatoonManager.GetNextLine(EColor.GreenBright, 1f);
            line.SetRefCoord(x.Start);
            line.SetOffCoord(x.End);
            Splatoon.DisplayOnce(line);
        }
        foreach(var x in Utils.GenerateGroupConnectionLines(alias, 0.25f))
        {
            var line = S.Ipc.SplatoonManager.GetNextLine(EColor.YellowBright, 1f);
            line.SetRefCoord(x.Start);
            line.SetOffCoord(x.End);
            Splatoon.DisplayOnce(line);
        }
    }

    private static void DrawSplatoon(CustomAliasCommand command, int index)
    {
        if(!Splatoon.IsConnected()) return;
        if(command.Kind == CustomAliasKind.Circular_movement)
        {
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Circular movement");
                point.SetRefCoord(command.CenterPoint.ToVector3());
                Splatoon.DisplayOnce(point);
            }
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Circular exit");
                point.SetRefCoord(command.CircularExitPoint);
                Splatoon.DisplayOnce(point);
            }
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint();
                point.SetRefCoord(command.CenterPoint.ToVector3());
                point.Filled = false;
                point.radius = command.Clamp == null ? Math.Clamp(Player.DistanceTo(command.CenterPoint), 1f, 10f) : (command.Clamp.Value.Min + command.Clamp.Value.Max) / 2f;
                Splatoon.DisplayOnce(point);
            }
        }
        else if(command.Kind == CustomAliasKind.Move_to_point)
        {
            var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Walk to");
            point.SetRefCoord(command.Point);
            point.radius = command.Scatter;
            Splatoon.DisplayOnce(point);
        }
        else if(command.Kind == CustomAliasKind.Navmesh_to_point)
        {
            var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Navmesh to");
            point.SetRefCoord(command.Point);
            Splatoon.DisplayOnce(point);
        }
    }

    private static void DrawCommand(CustomAliasCommand command, CustomAlias selected, int index)
    {
        ImGui.PushID(command.ID);
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Copy, "Copy".Loc()))
        {
            Copy(EzConfig.DefaultSerializationFactory.Serialize(command, false));
        }
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Trash, "Delete".Loc(), ImGuiEx.Ctrl))
        {
            new TickScheduler(() => selected.Commands.Remove(command));
        }
        ImGuiEx.Tooltip("Press CTRL and click".Loc());

        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "Run".Loc(), enabled: !Utils.IsBusy()))
        {
            selected.Enqueue(inclusiveStart: index, exclusiveEnd: index + 1);
        }
        ImGuiEx.Tooltip("Run only this command".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.AngleDoubleDown, "Run and continue".Loc(), enabled: !Utils.IsBusy()))
        {
            selected.Enqueue(inclusiveStart: index);
        }
        ImGuiEx.Tooltip("Run this command and every command after it".Loc());

        ImGui.Separator();
        ImGui.SetNextItemWidth(150f.Scale());
        ImGuiEx.EnumCombo("Alias kind".Loc(), ref command.Kind, names: GetKindNames());

        if(command.Kind == CustomAliasKind.Teleport_to_Aetheryte)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.Combo("Select aetheryte to teleport to".Loc(), ref command.Aetheryte, Aetherytes, names: AetherytePlaceNames);
            ImGui.SetNextItemWidth(60f.Scale());
            ImGui.DragFloat("Skip teleport if already at aetheryte within this range".Loc(), ref command.SkipTeleport, 0.01f);
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point))
        {
            Utils.DrawVector3Selector($"walktopoint{command.ID}", ref command.Point);
            ImGui.SameLine();
            ImGuiEx.Text(UiBuilder.IconFont, FontAwesomeIcon.ArrowsLeftRight.ToIconString());
            ImGui.SameLine(0, 1);
            ImGui.SetNextItemWidth(50f.Scale());
            ImGuiEx.SliderFloat($"##scatter", ref command.Scatter, 0f, 2f);
            ImGuiEx.Tooltip("Scatter. Double-click to input manually.".Loc());
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Move_to_point))
        {
            drawFlight();
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point, CustomAliasKind.Circular_movement))
        {
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("Restrict movement to zone".Loc(), command.Territory == 0 ? "No Restriction".Loc() : ExcelTerritoryHelper.GetName(command.Territory)))
            {
                _ = new TerritorySelector((TerritorySelector sel, uint territory) =>
                {
                    command.Territory = territory;
                })
                {
                    Mode = TerritorySelector.DisplayMode.PlaceNameDutyUnion,
                    SelectedCategory = TerritorySelector.Category.All,
                };
                ImGui.CloseCurrentPopup();
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Eraser, enabled: command.Territory != 0))
            {
                command.Territory = 0;
            }
            ImGuiEx.Tooltip("Remove Requirement".Loc());
            ImGui.SameLine(0, 1);
            if(ImGuiEx.IconButton(FontAwesomeIcon.MapPin, enabled: Player.Available))
            {
                command.Territory = Player.Territory;
            }
            ImGuiEx.Tooltip("Set to ??".Loc(ExcelTerritoryHelper.GetName(Player.Territory)));
            ImGui.SameLine(0, 1);
            if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowDownUpAcrossLine))
            {
                ImGui.OpenPopup("SpreadTerritory");
            }
            ImGuiEx.Tooltip("Copy this property to adjacent commands...".Loc());
            if(ImGui.BeginPopup("SpreadTerritory"))
            {
                ImGuiEx.Text("Copy territory requirement:\n??".Loc(command.Territory == 0 ? "No Restriction".Loc() : ExcelTerritoryHelper.GetName(command.Territory)));
                ImGuiEx.TextV("Up or down until command number:".Loc());
                ImGui.SetNextItemWidth(150f.Scale());
                ImGuiEx.FilteringInputInt("##cmdNum", out var cmdNum);
                ImGui.SameLine();
                if(ImGui.Button("OK".Loc()))
                {
                    selected.CopyTerritoryRange(index, cmdNum);
                    ImGui.CloseCurrentPopup();
                }
                if(ImGui.Selectable("To all the commands within this alias".Loc()))
                {
                    selected.Commands.Each(x => x.Territory = command.Territory);
                }
                ImGui.EndPopup();
            }
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Navmesh_to_point))
        {
            ImGui.SameLine(0, 1);
            ImGuiEx.ButtonCheckbox(FontAwesomeIcon.FastForward, ref command.UseTA, EColor.Green);
            ImGuiEx.Tooltip("Use TextAdvance for movement. Flight settings are inherited from TextAdvance.".Loc());
            if(!command.UseTA)
            {
                drawFlight();
            }
        }

        if(command.Kind == CustomAliasKind.Move_to_point)
        {
            command.ExtraPoints ??= [];
            if(command.ExtraPoints.Count > 0)
            {
                ImGuiEx.Text("Extra Points:".Loc());
            }
            ImGui.Indent();
            for(var i = 0; i < command.ExtraPoints.Count; i++)
            {
                var pointIndex = i;
                ImGui.PushID($"Point{i}");
                var x = command.ExtraPoints[i];
                Utils.DrawVector3Selector($"walktopointextra{i}{command.ID}", ref x);
                if(x != command.ExtraPoints[i])
                {
                    command.ExtraPoints[i] = x;
                }
                ImGui.SameLine(0, 1);
                if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                {
                    new TickScheduler(() => command.ExtraPoints.RemoveAt(pointIndex));
                }
                ImGui.PopID();
            }
            ImGui.Unindent();

            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add Extra Point".Loc()))
            {
                command.ExtraPoints.Add(new());
            }
            ImGuiEx.Tooltip("Random point will be selected. Scatter will remain the same across all points.".Loc());
        }

        void drawFlight()
        {
            ImGui.SameLine(0, 1);
            ImGuiEx.ButtonCheckbox(FontAwesomeIcon.Plane, ref command.UseFlight, EColor.Green);
            ImGuiEx.Tooltip("Fly for movement. Don't forget to use \"Mount Up\" command before. ".Loc());
        }

        if(command.Kind == CustomAliasKind.Change_world)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ChangeWorldSelector.Draw(ref command.World);
            ImGui.SameLine();
            ImGuiEx.Text("Select world".Loc());
        }

        if(command.Kind == CustomAliasKind.Use_Aethernet)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("Select aethernet shard to teleport to".Loc(), command.Aetheryte == 0 ? "- Not selected -".Loc() : Utils.KnownAetherytes.SafeSelect(command.Aetheryte, command.Aetheryte.ToString()), ImGuiComboFlags.HeightLarge))
            {
                ref var filter = ref Ref<string>.Get($"Filter{command.ID}");
                ImGui.SetNextItemWidth(200f);
                ImGui.InputTextWithHint("##filter", "Filter".Loc(), ref filter, 50);
                foreach(var x in Utils.KnownAetherytesByCategories)
                {
                    bool shouldHide(ref string filter, KeyValuePair<uint, string> v) => filter.Length > 0 && !v.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) && !x.Key.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    foreach(var v in x.Value)
                    {
                        if(!shouldHide(ref filter, v)) goto Display;
                    }
                    continue;
                Display:
                    ImGuiEx.Text(EColor.YellowBright, $"{x.Key}:");
                    ImGui.Indent();
                    foreach(var v in x.Value)
                    {
                        if(shouldHide(ref filter, v)) continue;
                        var sel = command.Aetheryte == v.Key;
                        if(sel && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                        if(ImGui.Selectable($"{v.Value}##{v.Key}", sel))
                        {
                            command.Aetheryte = v.Key;
                        }
                    }
                    ImGui.Unindent();
                    ImGui.Separator();
                }
                ImGui.EndCombo();
            }
        }

        if(command.Kind == CustomAliasKind.Circular_movement)
        {
            if(ImGui.BeginTable("circular", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("1");
                ImGui.TableSetupColumn("1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Center point: ".Loc());
                ImGui.TableNextColumn();
                Utils.DrawVector2Selector("center", ref command.CenterPoint);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Exit point: ".Loc());
                ImGui.TableNextColumn();
                Utils.DrawVector3Selector($"exit{command.ID}", ref command.CircularExitPoint);
                ImGui.Checkbox("Finish by walking to exit point".Loc(), ref command.WalkToExit);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Precision: ".Loc());
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.DragFloat("##precision", ref command.Precision.ValidateRange(4f, 100f), 0.01f);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Tolerance: ".Loc());
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.DragInt("##tol", ref command.Tolerance.ValidateRange(1, (int)(command.Precision * 0.75f)), 0.01f);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Distance limit: ".Loc());
                ImGui.TableNextColumn();
                var en = command.Clamp != null;
                if(ImGui.Checkbox($"##clamp", ref en))
                {
                    if(en)
                    {
                        command.Clamp = (0, 10);
                    }
                    else
                    {
                        command.Clamp = null;
                    }
                }
                if(command.Clamp != null)
                {
                    var v = command.Clamp.Value;
                    ImGui.SameLine(0, 1);
                    ImGui.SetNextItemWidth(50f.Scale());
                    ImGui.DragFloat("##prec1", ref v.Min, 0.01f);
                    ImGui.SameLine(0, 1);
                    ImGuiEx.Text("-");
                    ImGui.SameLine(0, 1);
                    ImGui.SetNextItemWidth(50f.Scale());
                    ImGui.DragFloat("##prec2", ref v.Max, 0.01f);
                    if(v.Min < v.Max)
                    {
                        command.Clamp = v;
                    }
                    if(Svc.Targets.Target != null)
                    {
                        ImGui.SameLine(0, 1);
                        ImGuiEx.Text("To target: ??".Loc(Player.DistanceTo(Svc.Targets.Target).ToString("F1")));
                    }
                }

                ImGui.EndTable();
            }
        }
        if(command.Kind == CustomAliasKind.Interact)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.InputUint("Data ID".Loc(), ref command.DataID);
            ImGui.SameLine(0, 1);
            // 先解成 uint 再比：直接寫 Target?.BaseId != 0，沒有目標時是 null != 0 ⇒ true，按鈕會可按。
            var targetBaseId = Svc.Targets.Target?.BaseId ?? 0;
            if(ImGuiEx.Button("Target".Loc(), targetBaseId != 0))
            {
                command.DataID = targetBaseId;
            }
            // ImGuiEx.Button 停用時只淡化、不進 BeginDisabled，說明框畫在它還原透明度之後，本來就是全亮的。
            if(targetBaseId == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGuiEx.SetTooltip("Needs a target with a data ID - target an NPC or object first.".Loc());
            }
            ImGuiEx.InputFloat(100f.Scale(), "Approach before interacting to this distance".Loc(), ref command.InteractDistance, 1, 1);
            ImGuiEx.Tooltip("Checked: first auto-run toward the object until closer than this distance (yalms), then interact. Unchecked: interact from where you stand.".Loc());
        }
        if(command.Kind.EqualsAny(CustomAliasKind.Select_Yes, CustomAliasKind.Select_List_Option))
        {
            ImGuiEx.TextWrapped("List entries that you would like to select/confirm:".Loc());
            if(ImGuiEx.BeginDefaultTable("ItemLst", ["~1", "2"], false))
            {
                for(var i = 0; i < command.SelectOption.Count; i++)
                {
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    var str = command.SelectOption[i];
                    if(ImGui.InputText("##selectOpt", ref str, 500))
                    {
                        command.SelectOption[i] = str;
                    }
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                    {
                        var idx = i;
                        new TickScheduler(() => command.SelectOption.RemoveAt(idx));
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add New Option".Loc()))
                {
                    command.SelectOption.Add("");
                }
            }
        }
        if(command.Kind.EqualsAny(CustomAliasKind.Select_Yes, CustomAliasKind.Select_List_Option, CustomAliasKind.Confirm_Contents_Finder))
        {
            ImGui.Checkbox("Skip on screen fade".Loc(), ref command.StopOnScreenFade);
        }

        if(command.Kind == CustomAliasKind.Wait_for_Transition)
        {
            ImGui.Checkbox("Require territory change".Loc(), ref command.RequireTerritoryChange);
            ImGuiEx.Tooltip("Unchecked: the wait ends after any loading screen. Checked: it only ends once you are in a different zone than when the wait started.".Loc());
        }
        ImGui.PopID();
    }
}
