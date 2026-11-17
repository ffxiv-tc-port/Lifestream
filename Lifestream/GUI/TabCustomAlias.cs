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

    private static readonly uint[] Aetherytes = Svc.Data.GetExcelSheet<Aetheryte>().Where(x => x.PlaceName.ValueNullable?.Name.ToString().IsNullOrEmpty() == false && x.IsAetheryte).Select(x => x.RowId).ToArray();
    private static readonly Dictionary<uint, string> AetherytePlaceNames = Aetherytes.Select(Svc.Data.GetExcelSheet<Aetheryte>().GetRow).ToDictionary(x => x.RowId, x => $"{x.PlaceName.Value.Name} ({x.RowId})");

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
