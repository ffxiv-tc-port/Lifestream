using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.Configuration;
using Callback = ECommons.Automation.Callback;
using ECommons.ExcelServices;
using ECommons.EzSharedDataManager;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.AtkReaders;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.Schedulers;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.Residential;
using Lifestream.Tasks;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using NightmareUI.ImGuiElements;
using Path = System.IO.Path;

namespace Lifestream.GUI;

internal static unsafe partial class UIDebug
{
    internal static uint DebugTerritory = 0;
    internal static TinyAetheryte? DebugAetheryte = null;
    internal static int DC = 0;
    internal static int Destination = 0;
    internal static List<Vector3> DebugPath = [];
    internal static void Draw()
    {
        ImGuiEx.EzTabBar("debug",
            InternalLog.ImGuiTab(),
            ("Data editor".Loc(), Editor, null, true),
            ("Housing data".Loc(), Housing, null, true),
            ("AtkReader", Reader, null, true),
            ("Debug".Loc(), Debug, null, true),
            ("Multipath".Loc(), TabMultipath.Draw, null, true)
            );
    }

    private static int Resize = 60;
    private static int LastPlot = -1;
    private static bool doCurPlot = false;
    private static bool ShowPathes = false;
    private static bool ShowFirstPoint = true;
    private static int u;
    private static int v;
    private static List<Vector3> CurrentPath = null;
    private static bool DoAutotest = false;
    private static int AutotestPlot = 0;
    private static int AutotestWard = 30;
    private static ResidentialAetheryteKind AutotestKind = ResidentialAetheryteKind.Gridania;
    private static List<long> StuckRecords = [];
    private static Vector3? LastPosition = null;

    /// <summary>
    /// 把一個乙太之光候選在「主執行緒的當下這一幀」抄成純資料 —— 這裡就是離開遊戲記憶體的邊界。
    /// 回傳的兩個成員都是值:Vector3 是複製,ResidentialAetheryte 是 struct 且 Name 在建構時就算好,
    /// 之後不管跨執行緒還是跨幀使用,都不會再碰到 ObjectTable 的共用包裝(它會被就地改寫 Address)。
    /// 🔴 呼叫端必須在列舉 Svc.Objects 的同一幀就 .ToArray():LINQ 是延後求值,不落地等於沒抄。
    /// </summary>
    private static (Vector3 Position, ResidentialAetheryte Aetheryte) SnapshotAethernetCandidate(IGameObject obj)
        => (obj.Position, S.Data.ResidentialAethernet.GetFromIGameObject(obj).Value);

    private static void Reader()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
            {
                var r = new ReaderLobbyDKTWorldList(addon);
                ImGuiEx.Text($"Source: {r.Source}");
                ImGuiEx.Text($"Destination: {r.Destination}");
                foreach(var dc in r.Regions)
                {
                    ImGuiEx.Text($"  {dc.RegionTitle}");
                    foreach(var world in dc.DataCenters)
                    {
                        ImGuiEx.Text($"    {world.Id}/{world.Name}");
                    }
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var addon) && IsAddonReady(addon))
            {
                var reader = new ReaderTelepotTown(addon);
                for(var i = 0; i < reader.DestinationData.Count; i++)
                {
                    var data = reader.DestinationData[i];
                    var name = reader.DestinationName[i];
                    ImGuiEx.Text($"{data.Type}|{data.State}|{data.CallbackData}|{data.IconID}|{name.Name}");
                }
            }
        }
    }

    private static int index = 0;
    private static string str = "";
    private static string str2 = "";
    private static string str3 = "";
    private static string World = "";
    private static ResidentialAetheryteKind ResiA;
    private static int Ward = 1;
    private static Vector2 uv0;
    private static Vector2 uv1;
    private static Vector2 size;
    private static string addr = "";
    private static string CharaName = "";
    private static int WorldSel;

    private static void Editor()
    {
        var bsize = ImGuiHelpers.GetButtonSize("A") with { X = 280 };
        if(ImGui.Button("Save".Loc()))
        {
            ImGui.SetClipboardText(JsonConvert.SerializeObject(S.Data.DataStore.StaticData));
            S.Data.DataStore.StaticData.SaveConfiguration(Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName, S.Data.DataStore.FileName));
        }
        foreach(var x in S.Data.DataStore.Aetherytes)
        {
            ImGui.Separator();
            if(ImGui.Button($"{x.Key.Name}", bsize))
            {
                DebugAetheryte = x.Key;
            }
            {
                {
                    ImGui.SameLine();
                    if(!S.Data.DataStore.StaticData.SortOrder.ContainsKey(x.Key.ID)) S.Data.DataStore.StaticData.SortOrder[x.Key.ID] = 0;
                    var d = (int)S.Data.DataStore.StaticData.SortOrder[x.Key.ID];
                    ImGui.SetNextItemWidth(100f.Scale());
                    if(ImGui.InputInt($"##{x.Key.Name}{x.Key.ID}sort", ref d))
                    {
                        S.Data.DataStore.StaticData.SortOrder[x.Key.ID] = (uint)d;
                    }
                }
                if(ImGui.GetIO().KeyCtrl)
                {
                    ImGui.SameLine();
                    ImGuiEx.Text($"{x.Key.Position}");
                }
                if(Svc.Targets.Target != null)
                {
                    ImGui.SameLine();
                    if(ImGui.Button("Pos".Loc() + "##" + x.Key.ID))
                    {
                        S.Data.DataStore.StaticData.CustomPositions[x.Key.ID] = Svc.Targets.Target.Position;
                        DuoLog.Information("Written ?? for ??".Loc(Svc.Targets.Target.Position, x.Key.ID));
                    }
                }
            }
            foreach(var l in x.Value)
            {
                if(ImGui.Button($"    {l.Name}", bsize)) DebugAetheryte = l;
                {
                    {
                        ImGui.SameLine();
                        if(!S.Data.DataStore.StaticData.SortOrder.ContainsKey(l.ID)) S.Data.DataStore.StaticData.SortOrder[l.ID] = 0;
                        var d = (int)S.Data.DataStore.StaticData.SortOrder[l.ID];
                        ImGui.SetNextItemWidth(100f.Scale());
                        if(ImGui.InputInt($"##{l.Name}{l.ID}sort", ref d))
                        {
                            S.Data.DataStore.StaticData.SortOrder[l.ID] = (uint)d;
                        }
                    }
                    if(ImGui.GetIO().KeyCtrl)
                    {
                        ImGui.SameLine();
                        ImGuiEx.Text($"{l.Position}");
                    }
                    if(Svc.Targets.Target != null)
                    {
                        ImGui.SameLine();
                        if(ImGui.Button("Pos".Loc() + "##" + l.ID))
                        {
                            S.Data.DataStore.StaticData.CustomPositions[l.ID] = Svc.Targets.Target.Position;
                            DuoLog.Information("Written ?? for ??".Loc(Svc.Targets.Target.Position, l.ID));
                        }
                    }
                }
            }
        }
        ImGuiEx.Text(Utils.GetAvailableAethernetDestinations().Join("\n"));
        if(ImGui.Button("null".Loc())) DebugAetheryte = null;
    }
}
