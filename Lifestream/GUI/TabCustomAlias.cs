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
public static class TabCustomAlias
{
    /// <summary>
    /// 「切換世界」這個別名指令選的是<b>傳送目的地</b>，所以要濾掉去不了的世界。
    /// 刻意自己開一個 selector 而不是用 <c>WorldSelector.Instance</c> —— 那是共用單例，
    /// 在它身上設 <c>ShouldHideWorld</c> 會一路影響到地址簿與傳送封鎖的世界欄，
    /// 讓既有的拉姆條目變得無法編輯。
    /// </summary>
    private static readonly WorldSelector ChangeWorldSelector = new("##changeworld")
    {
        ShouldHideWorld = PublicWorlds.IsUnavailable,
    };

    private static ImGuiEx.RealtimeDragDrop<CustomAliasCommand> DragDrop = new("CusACmd", x => x.ID);
    private static readonly Vector4[] ChainColors = [ImGuiColors.DalamudRed, ImGuiColors.ParsedOrange, ImGuiColors.DalamudYellow, ImGuiColors.ParsedGreen, ImGuiColors.TankBlue, ImGuiColors.ParsedPurple];

    public static void Draw()
    {
        var selector = S.CustomAliasFileSystemManager.FileSystem.Selector;
        selector.Draw(150f);
        ImGui.SameLine();
        if(ImGui.BeginChild("Child"))
        {
            if(selector.Selected != null)
            {
                var item = selector.Selected;
                DrawAlias(item);
            }
            else
            {
                ImGuiEx.TextWrapped("To begin, select an alias you want to edit or create a new one.".Loc());
            }
        }
        ImGui.EndChild();
    }

    private static List<Action> PostTableActions = [];
    private static void DrawAlias(CustomAlias selected)
    {
        AssignAllChainGroups(selected);
        DrawSplatoon(selected);
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add new".Loc()))
        {
            selected.Commands.Add(new() { Territory = Player.Available ? Player.Territory : 0 });
        }
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Paste, "Paste".Loc()))
        {
            try
            {
                var result = JsonConvert.DeserializeObject<CustomAliasCommand>(Paste());
                if(result == null) throw new NullReferenceException();
                selected.Commands.Add(result);
            }
            catch(Exception e)
            {
                Notify.Error(e.Message);
                e.Log();
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("##en", ref selected.Enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f.Scale());
        if(!selected.Enabled) ImGui.BeginDisabled();
        ImGui.InputText($"##Alias", ref selected.Alias, 50);
        if(!selected.Enabled) ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGuiEx.SetTooltip("Enabled".Loc());
        ImGui.SameLine();
        ImGuiEx.HelpMarker("Will be available via \"/li ??\" command".Loc(selected.Alias));
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "Run".Loc(), enabled: !Utils.IsBusy()))
        {
            selected.Enqueue();
        }
        ImGui.SameLine();
        ImGuiEx.Text("Visualisation:".Loc());
        ImGuiEx.PluginAvailabilityIndicator([new("Splatoon")]);
        DragDrop.Begin();
        var cursor = ImGui.GetCursorPos();
        foreach(var x in PostTableActions)
        {
            x();
        }
        ImGui.SetCursorPos(cursor);
        PostTableActions.Clear();
        if(ImGuiEx.BeginDefaultTable(["Control".Loc(), "~" + "Command".Loc()], false))
        {
            for(var i = 0; i < selected.Commands.Count; i++)
            {
                var x = selected.Commands[i];
                var row = GetRowCache(x);

                ImGui.TableNextRow();
                DragDrop.SetRowColor(x.ID);
                ImGui.TableNextColumn();
                DragDrop.NextRow();
                DragDrop.DrawButtonDummy(x, selected.Commands, i);
                ImGui.TableNextColumn();
                var curpos = ImGui.GetCursorPos() + ImGui.GetContentRegionAvail() with { Y = 0 };
                if(x.Kind is CustomAliasKind.Move_to_point or CustomAliasKind.Navmesh_to_point or CustomAliasKind.Circular_movement)
                {
                    PostTableActions.Add(GetInsertButtonAction(row, x, selected, i + 1, curpos));
                }

                if(!x.CanExecute(out var problem))
                {
                    ImGui.AlignTextToFramePadding();
                    ImGuiEx.Text(EColor.YellowBright, UiBuilder.IconFont, FontAwesomeIcon.ExclamationTriangle.ToIconString());
                    ImGuiEx.Tooltip(problem);
                    ImGui.SameLine();
                }
                ImGuiEx.TreeNodeCollapsingHeader(GetHeader(row, x, i), GetDrawCommandAction(row, x, selected, i), ImGuiTreeNodeFlags.CollapsingHeader);
                DrawSplatoon(x, i);
            }
            ImGui.EndTable();
        }
        DragDrop.End();
    }

    // 插入按鈕的回呼在下一次 DrawAlias 開頭才執行，鏈色與 x 的欄位在那時才讀。
    private static void DrawInsertButton(CustomAliasCommand x, CustomAlias selected, int insertIndex, Vector2 curpos)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var pos = curpos
            - ImGuiHelpers.GetButtonSize(FontAwesomeIcon.ArrowCircleDown.ToIconString()) with { Y = 0 }
            - new Vector2(ImGui.CalcTextSize(FontAwesomeIcon.Link.ToIconString()).X + ImGui.GetStyle().ItemSpacing.X, 0);
        ImGui.SetCursorPos(pos);
        ImGui.PopFont();
        ImGui.AlignTextToFramePadding();
        // 色盤只有 6 色，第 7 條以後的鏈輪流重用，不讓圖示變成透明。
        var col = x.ChainGroup == 0 ? Vector4.Zero : ChainColors[(x.ChainGroup - 1) % ChainColors.Length];
        ImGuiEx.Text(col, UiBuilder.IconFont, FontAwesomeIcon.Link.ToIconString());
        if(x.ChainGroup != 0) ImGuiEx.Tooltip("Consecutive movement commands with the same link color are walked as one continuous path.".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowCircleDown, x.ID, enabled: Player.Available))
        {
            InsertMoveCommandNextTick(x, selected, insertIndex);
        }
        ImGuiEx.Tooltip("Create move command after this command with player's position and territory".Loc());
    }

    // 獨立成方法：參數只在按下時才被捕獲，DrawInsertButton 每幀執行不配置。
    private static void InsertMoveCommandNextTick(CustomAliasCommand x, CustomAlias selected, int insertIndex)
    {
        new TickScheduler(() =>
        {
            selected.Commands.Insert(insertIndex, new()
            {
                Kind = CustomAliasKind.Move_to_point,
                UseFlight = x.UseFlight,
                Point = Player.Position,
                Territory = Player.Territory == x.Territory ? x.Territory : (Player.Available ? Player.Territory : 0),
            });
        });
    }

    // 每個指令一份：標頭字串與兩個繪製回呼，輸入全部相同才沿用。只在框架執行緒（繪製）用。
    private sealed class RowCache
    {
        public string Header;
        public int HeaderIndex;
        public string Language;
        public CultureInfo Culture;
        public string ID;
        public CustomAliasKind Kind;
        public Vector3 Point;
        public uint Territory;
        public uint Aetheryte;
        public int World;
        public uint DataID;
        public Action DrawCommand;
        public CustomAlias DrawSelected;
        public int DrawIndex;
        public Action InsertButton;
        public CustomAlias InsertSelected;
        public int InsertIndex;
        public Vector2 InsertPos;
    }

    // 以指令物件為鍵、不延長它的壽命：刪掉的指令連同快取一起回收。
    private static readonly ConditionalWeakTable<CustomAliasCommand, RowCache> RowCaches = new();

    private static RowCache GetRowCache(CustomAliasCommand x) => RowCaches.GetValue(x, static _ => new RowCache());

    // 標頭只取決於下列欄位、序號、語言（.Loc()）與文化（{Point:F1}）；地名、傳送點名稱表在一次執行中不變。
    private static string GetHeader(RowCache c, CustomAliasCommand x, int i)
    {
        var language = Localization.CurrentLanguage;
        var culture = CultureInfo.CurrentCulture;
        if(c.Header == null || c.HeaderIndex != i || c.Language != language || c.Culture != culture || !ReferenceEquals(c.ID, x.ID)
            || c.Kind != x.Kind || !SameBits(c.Point, x.Point) || c.Territory != x.Territory || c.Aetheryte != x.Aetheryte
            || c.World != x.World || c.DataID != x.DataID)
        {
            c.Header = "Command ??:".Loc(i + 1) + $" {GetKindLabel(x.Kind)}{GetExtraText(x)}###{x.ID}";
            c.HeaderIndex = i;
            c.Language = language;
            c.Culture = culture;
            c.ID = x.ID;
            c.Kind = x.Kind;
            c.Point = x.Point;
            c.Territory = x.Territory;
            c.Aetheryte = x.Aetheryte;
            c.World = x.World;
            c.DataID = x.DataID;
        }
        return c.Header;
    }

    // 逐位元比：-0 與 0 經 F1 格式化的結果不同。
    private static bool SameBits(Vector3 a, Vector3 b)
        => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
        && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
        && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);

    private static Action GetDrawCommandAction(RowCache c, CustomAliasCommand x, CustomAlias selected, int index)
    {
        if(c.DrawCommand == null || c.DrawSelected != selected || c.DrawIndex != index)
        {
            c.DrawCommand = MakeDrawCommandAction(x, selected, index);
            c.DrawSelected = selected;
            c.DrawIndex = index;
        }
        return c.DrawCommand;
    }

    private static Action MakeDrawCommandAction(CustomAliasCommand x, CustomAlias selected, int index) => () => DrawCommand(x, selected, index);

    private static Action GetInsertButtonAction(RowCache c, CustomAliasCommand x, CustomAlias selected, int insertIndex, Vector2 curpos)
    {
        if(c.InsertButton == null || c.InsertSelected != selected || c.InsertIndex != insertIndex
            || BitConverter.SingleToUInt32Bits(c.InsertPos.X) != BitConverter.SingleToUInt32Bits(curpos.X)
            || BitConverter.SingleToUInt32Bits(c.InsertPos.Y) != BitConverter.SingleToUInt32Bits(curpos.Y))
        {
            c.InsertButton = MakeInsertButtonAction(x, selected, insertIndex, curpos);
            c.InsertSelected = selected;
            c.InsertIndex = insertIndex;
            c.InsertPos = curpos;
        }
        return c.InsertButton;
    }

    private static Action MakeInsertButtonAction(CustomAliasCommand x, CustomAlias selected, int insertIndex, Vector2 curpos)
        => () => DrawInsertButton(x, selected, insertIndex, curpos);

    // 英文顯示名同時是 ini 的鍵；查不到翻譯時退回與舊版相同的英文。
    private static string GetKindName(CustomAliasKind kind) => kind.ToString().Replace('_', ' ').Loc();

    private static Dictionary<CustomAliasKind, string> KindNames;
    private static string KindNamesLanguage;

    // 只在框架執行緒（繪製）呼叫。名稱經過 .Loc()，所以 Localization.CurrentLanguage 一變就重建。
    private static Dictionary<CustomAliasKind, string> GetKindNames()
    {
        var language = Localization.CurrentLanguage;
        if(KindNames == null || KindNamesLanguage != language)
        {
            KindNames = Enum.GetValues<CustomAliasKind>().ToDictionary(x => x, GetKindName);
            KindNamesLanguage = language;
        }
        return KindNames;
    }

    // 設定檔裡可能有列舉以外的值（例：新版存的種類），查不到就照舊現算。
    private static string GetKindLabel(CustomAliasKind kind) => GetKindNames().TryGetValue(kind, out var name) ? name : GetKindName(kind);

    private static string GetExtraText(CustomAliasCommand x)
    {
        if(x.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point))
        {
            return x.Territory == 0 ? $" {x.Point:F1}" : $" {x.Point:F1} [{ExcelTerritoryHelper.GetName(x.Territory)}]";
        }
        else if(x.Kind == CustomAliasKind.Circular_movement)
        {
            if(x.Territory != 0)
            {
                return $" [{ExcelTerritoryHelper.GetName(x.Territory)}]";
            }
        }
        else if(x.Kind == CustomAliasKind.Teleport_to_Aetheryte)
        {
            if(AetherytePlaceNames.TryGetValue(x.Aetheryte, out var ret))
            {
                return $" [{ret}]";
            }
        }
        else if(x.Kind == CustomAliasKind.Use_Aethernet)
        {
            if(x.Aetheryte != 0)
            {
                return $" [{Utils.KnownAetherytes.SafeSelect(x.Aetheryte, x.Aetheryte.ToString())}]";
            }
        }
        else if(x.Kind == CustomAliasKind.Change_world)
        {
            if(x.World != 0)
            {
                return $" [{ExcelWorldHelper.GetName(x.World)}]";
            }
        }
        else if(x.Kind == CustomAliasKind.Interact)
        {
            return $" [{x.DataID}]";
        }
        return "";
    }

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


    private static readonly uint[] Aetherytes = Svc.Data.GetExcelSheet<Aetheryte>().Where(x => x.PlaceName.ValueNullable?.Name.ToString().IsNullOrEmpty() == false && x.IsAetheryte).Select(x => x.RowId).ToArray();
    private static readonly Dictionary<uint, string> AetherytePlaceNames = Aetherytes.Select(Svc.Data.GetExcelSheet<Aetheryte>().GetRow).ToDictionary(x => x.RowId, x => $"{x.PlaceName.Value.Name} ({x.RowId})");

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

    /// <summary>依 <see cref="Utils.IsChainedWithNext"/> 把會併成同一條路徑的連續指令編成同一組（1 起算，0＝不在任何鏈裡）。</summary>
    public static void AssignAllChainGroups(CustomAlias alias)
    {
        var commands = alias.Commands;
        if(commands == null || commands.Count == 0)
        {
            return;
        }

        var nextChainGroup = 1;
        for(var i = 0; i < commands.Count;)
        {
            if(i < commands.Count - 1 && Utils.IsChainedWithNext(alias, i))
            {
                var start = i;
                var end = i;
                while(end < commands.Count - 1 && Utils.IsChainedWithNext(alias, end))
                {
                    end++;
                }
                for(var j = start; j <= end; j++)
                {
                    commands[j].ChainGroup = nextChainGroup;
                }
                nextChainGroup++;
                i = end + 1;
            }
            else
            {
                commands[i].ChainGroup = 0;
                i++;
            }
        }
    }
}
