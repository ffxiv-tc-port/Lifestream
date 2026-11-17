using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons.Automation;
using ECommons.ChatMethods;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.ExcelServices.Sheets;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.Interop;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.Residential;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using NightmareUI;
using System;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using TerraFX.Interop.Windows;
using static FFXIVClientStructs.FFXIV.Client.UI.AddonAirShipExploration;
using Action = System.Action;
using CharaData = (string Name, ushort World);
using FXWindows = TerraFX.Interop.Windows.Windows;

namespace Lifestream;

internal static unsafe partial class Utils
{
    public static void DrawWorldSelector(ICollection<int> worldList)
    {
        ImGuiEx.CollectionCheckbox("All", PublicWorlds.Get().Select(x => (int)x.RowId), worldList);
        ImGui.Indent();
        // 用 PublicWorlds.AllRegions() 而不是 Enum.GetValues<Region>()：ECommons 的列舉沒有台服的 8，
        // 直接迭代列舉會讓陸行鳥資料中心與其底下的世界整組消失——而同一個畫面上方的
        // PublicWorlds.Get()（全部聚合）是含台服的，兩者會不一致。
        var regions = PublicWorlds.AllRegions();
        foreach(var r in regions)
        {
            ImGuiEx.CollectionCheckbox(PublicWorlds.RegionDisplayName(r), PublicWorlds.Get(r).Select(x => (int)x.RowId), worldList);
            var dc = ExcelWorldHelper.GetDataCenters(r);
            ImGui.Indent();
            foreach(var d in dc)
            {
                var worlds = PublicWorlds.Get(d.RowId);
                ImGuiEx.CollectionCheckbox(d.Name.ToString(), worlds.Select(x => (int)x.RowId), worldList);
                ImGui.Indent();
                foreach(var w in worlds.OrderBy(x => x.Name.ToString()))
                {
                    ImGuiEx.CollectionCheckbox(w.Name.ToString(), (int)w.RowId, worldList);
                }
                ImGui.Unindent();
            }
            ImGui.Unindent();
        }
        ImGui.Unindent();
    }

    public static void ScreenToWorldSelector(string id, ref Vector3 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(isInWorldToScreen)
        {
            if(Svc.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out var worldPos))
            {
                point = worldPos;
            }
            ImGui.BeginTooltip();
            ImGuiEx.Text(LocText.PointLeftClickToFinish.Loc(point.ToString("F2")));
            ImGui.EndTooltip();
            if(IsKeyPressed((int)Keys.LButton))
            {
                isInWorldToScreen = false;
            }
        }
    }

    public static void BeginScreenToWorldSelection(string id, Vector3 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(Svc.GameGui.WorldToScreen(point, out var screenPos))
        {
            SetCursorTo((int)screenPos.X, (int)screenPos.Y);
        }
        isInWorldToScreen = true;
    }

    public static void ScreenToWorldSelector(string id, ref Vector2 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(isInWorldToScreen)
        {
            //PluginLog.Debug($"{ImGui.GetIO().MousePos}");
            if(Svc.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out var worldPos))
            {
                point = worldPos.ToVector2();
            }
            ImGui.BeginTooltip();
            ImGuiEx.Text(LocText.PointLeftClickToFinish.Loc(point.ToString("F2")));
            ImGui.EndTooltip();
            if(IsKeyPressed((int)Keys.LButton))
            {
                isInWorldToScreen = false;
            }
        }
    }

    public static void BeginScreenToWorldSelection(string id, Vector2 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(Svc.GameGui.WorldToScreen(point.ToVector3(Player.Position.Y), out var screenPos))
        {
            SetCursorTo((int)screenPos.X, (int)screenPos.Y);
        }
        isInWorldToScreen = true;
    }

    public static void SetCursorTo(int x, int y)
    {
        for(var i = 0; i < 1000; i++)
        {
            if(WindowFunctions.TryFindGameWindow(out var hwnd))
            {
                var point = new POINT() { x = x, y = y };
                if(FXWindows.ClientToScreen(hwnd, &point))
                {
                    FXWindows.SetCursorPos(point.x, point.y);
                }
                break;
            }
        }

    }

    public static void DrawVector2Selector(string id, ref Vector2 value)
    {
        ImGui.SetNextItemWidth(150f.Scale());
        ImGui.DragFloat2($"##vec{id}", ref value, 0.01f);
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MapPin, $"myPos{id}", enabled: Player.Interactable))
        {
            value = Player.Position.ToVector2();
        }
        ImGuiEx.Tooltip("To player positon".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.Crosshairs, $"target{id}", enabled: Svc.Targets.Target != null))
        {
            value = Svc.Targets.Target.Position.ToVector2();
        }
        ImGuiEx.Tooltip("To target positon".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.MousePointer, $"target{id}", enabled: Player.Interactable))
        {
            BeginScreenToWorldSelection(id, value);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("Select with mouse".Loc());
        ImGui.SameLine(0, 1);
        // AgentMap.Instance() 是 AgentGetterGenerator 產出的兩層可空取得器
        // (`agentModule == null ? null : GetAgentByInternalId(...)`)——合法回 null。
        // 這裡在繪製路徑每幀跑,取一次、判一次,拿不到就把按鈕停用(fail-closed)。
        var flagAgent = AgentMap.Instance();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Flag, $"flag{id}", enabled: Player.Interactable && flagAgent != null && flagAgent->FlagMarkerCount > 0))
        {
            var marker = flagAgent->FlagMapMarkers[0];
            value = new(marker.XFloat, marker.YFloat);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("To map flag".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.Brush, $"splatoon{id}") && TryGetSelectedSplatoonElementPosition(out var fromSplatoon))
        {
            value = fromSplatoon.ToVector2();
        }
        ImGuiEx.Tooltip("From selected Splatoon element".Loc());
    }

    // 讀 Splatoon 設定視窗目前選中的元素座標（反射它的內部靜態欄位，只在按鈕按下時做）；讀不到就提示並回 false。
    private static bool TryGetSelectedSplatoonElementPosition(out Vector3 position)
    {
        position = default;
        try
        {
            if(!DalamudReflector.TryGetDalamudPlugin("Splatoon", out var instance, true, true))
            {
                Notify.Error("Splatoon is not installed".Loc());
                return false;
            }
            var element = instance.GetStaticFoP("Splatoon.ConfigGui.CGuiLayouts.LayoutDrawSelector", "CurrentElement");
            if(element == null)
            {
                Notify.Error("Element is not selected".Loc());
                return false;
            }
            position = new Vector3(element.GetFoP<float>("refX"), element.GetFoP<float>("refZ"), element.GetFoP<float>("refY"));
            return true;
        }
        catch(Exception e)
        {
            e.Log();
            Notify.Error("An error occurred while retrieving element coordinates".Loc());
            return false;
        }
    }

    public static void DrawVector3Selector(string id, ref Vector3 value)
    {
        ImGui.SetNextItemWidth(150f.Scale());
        ImGui.DragFloat3($"##vec{id}", ref value, 0.01f);
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MapPin, $"myPos{id}", enabled: Player.Interactable))
        {
            value = Player.Position;
        }
        ImGuiEx.Tooltip("To player positon".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.Crosshairs, $"target{id}", enabled: Svc.Targets.Target != null))
        {
            value = Svc.Targets.Target.Position;
        }
        ImGuiEx.Tooltip("To target positon".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.MousePointer, $"target{id}", enabled: Player.Interactable))
        {
            BeginScreenToWorldSelection(id, value);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("Select with mouse".Loc());
        ImGui.SameLine(0, 1);
        // AgentMap 取得器合法回 null,取一次、判一次,拿不到就停用按鈕。
        var flagAgent = AgentMap.Instance();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Flag, $"flag{id}", enabled: Player.Interactable && flagAgent != null && flagAgent->FlagMarkerCount > 0))
        {
            var marker = flagAgent->FlagMapMarkers[0];
            value = new(marker.XFloat, 0, marker.YFloat);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("To map flag".Loc());
        ImGui.SameLine(0, 1);
        if(ImGuiEx.IconButton(FontAwesomeIcon.Brush, $"splatoon{id}") && TryGetSelectedSplatoonElementPosition(out var fromSplatoon))
        {
            value = fromSplatoon;
        }
        ImGuiEx.Tooltip("From selected Splatoon element".Loc());
    }

    public static bool ResidentialAetheryteEnumSelector(string name, ref ResidentialAetheryteKind refConfigField)
    {
        var ret = false;
        var names = TabAddressBook.ResidentialNames;
        if(ImGui.BeginCombo(name, names.SafeSelect(refConfigField) ?? $"{refConfigField}"))
        {
            var values = Enum.GetValues<ResidentialAetheryteKind>();
            foreach(var x in values)
            {
                var equals = x == refConfigField;
                if(x.RenderIcon(ImGui.CalcTextSize("A").Y)) ImGui.SameLine(0, 1);
                if(ImGui.Selectable(names.SafeSelect(x) ?? $"{x}", equals))
                {
                    ret = true;
                    refConfigField = x;
                }
                if(ImGui.IsWindowAppearing() && equals) ImGui.SetScrollHereY();
            }
            ImGui.EndCombo();
        }
        return ret;
    }
}
