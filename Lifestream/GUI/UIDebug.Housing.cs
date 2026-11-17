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
    private static void Housing()
    {
        if(CurrentPath != null)
        {
            if(ImGui.Begin($"Lifestream {"Edit Path".Loc()}"))
            {
                if(ImGui.Button("Finish".Loc()))
                {
                    Svc.Framework.RunOnFrameworkThread(() =>
                    {
                        Utils.SaveGeneratedHousingData();
                    });
                    CurrentPath = null;
                }
                if(CurrentPath != null)
                {
                    UIHouseReg.DrawPathEditor(CurrentPath);
                }
            }
            ImGui.End();
        }
        if(ImGui.Button("Load from config folder".Loc()))
        {
            var d = EzConfig.LoadConfiguration<HousingData>("GeneratedHousingData.json", true);
            if(d != null) S.Data.ResidentialAethernet.HousingData = d;
        }
        var data = S.Data.ResidentialAethernet.HousingData.Data;
        if(ImGui.CollapsingHeader("Autotest".Loc()))
        {
            if(DoAutotest)
            {
                if(Utils.IsBusy())
                {
                    EzThrottler.Throttle("Autotest", 500, true);
                }
                else
                {
                    if(AutotestPlot >= 60)
                    {
                        DuoLog.Information("Autotest complete".Loc());
                        DoAutotest = false;
                    }
                    else if(EzThrottler.Throttle("Autotest"))
                    {
                        AutotestPlot++;
                        DuoLog.Information($"{"Now going to plot".Loc()} {AutotestPlot}");
                        TaskTpAndGoToWard.Enqueue(Player.CurrentWorld, AutotestKind, AutotestWard, AutotestPlot - 1, false, false);
                    }
                }
            }
            ImGui.Checkbox("Autotest active".Loc(), ref DoAutotest);
            ImGuiEx.EnumCombo("Autotest aetheryte".Loc(), ref AutotestKind);
            ImGui.InputInt("Autotest ward".Loc(), ref AutotestWard);
            ImGui.InputInt("Autotest current plot".Loc(), ref AutotestPlot);
            if(DoAutotest && EzThrottler.Throttle("StuckAutocheck", 1000))
            {
                if(P.FollowPath.Waypoints.Count > 0)
                {
                    if(LastPosition != null && Vector3.DistanceSquared(LastPosition.Value, Player.Position) < 1)
                    {
                        StuckRecords.Add(Environment.TickCount64);
                        StuckRecords.RemoveAll(x => Environment.TickCount64 - x > 10000);
                        if(StuckRecords.Count > 1)
                        {
                            DuoLog.Information($"{"Stuck at".Loc()} {AutotestPlot} - {AutotestKind}");
                            DoAutotest = false;
                            P.FollowPath.Stop();
                            Utils.TryNotify("Stuck".Loc());
                        }
                    }
                    LastPosition = Player.Position;
                }
                else
                {
                    LastPosition = null;
                }
            }
        }
        if(data.TryGetValue(P.Territory, out var plots))
        {
            if(ImGui.CollapsingHeader("Control".Loc()))
            {
                ImGui.Checkbox($"{"Show pathes".Loc()}", ref ShowPathes);
                ImGui.SameLine();
                ImGui.Checkbox("Show first point".Loc(), ref ShowFirstPoint);
                if(ShowPathes)
                {
                    var aetheryte = S.Data.ResidentialAethernet.ActiveAetheryte ?? S.Data.ResidentialAethernet.GetFromIGameObject(Svc.Targets.Target);
                    if(aetheryte != null)
                    {
                        foreach(var x in plots)
                        {
                            if(x.AethernetID == aetheryte.Value.ID && x.Path.Count > 0)
                            {
                                S.Ipc.SplatoonManager.RenderPath(ShowFirstPoint ? x.Path : x.Path[1..]);
                            }
                        }
                    }
                }
                // HousingManager.Instance() 是 [StaticAddress(..., isPointer: true)],不在住宅區/登入前是 null。
                var housingManager = HousingManager.Instance();
                if(housingManager == null)
                {
                    // 「讀不到」和「不在地皮上」是兩件事:後者 GetCurrentPlot() 回 -1 會顯示成 Plot: 0,
                    // 這裡不沿用那個顯示,免得把未知畫成一個看起來像數值的東西。LastPlot 也維持前值不動。
                    ImGuiEx.Text("Plot: (housing manager unavailable)".Loc());
                }
                else
                {
                    var curPlot = housingManager->GetCurrentPlot();
                    if(curPlot != -1) LastPlot = curPlot;
                    ImGuiEx.Text($"{"Plot:".Loc()}{curPlot + 1}");
                }
                ImGui.SetNextItemWidth(150f.Scale());
                ImGui.InputInt($"{"Resize".Loc()}", ref Resize);
                ImGui.SameLine();
                if(ImGui.Button("Resize arrays".Loc()))
                {
                    while(plots.Count > Resize) plots.RemoveAt(plots.Count - 1);
                    while(plots.Count < Resize) plots.Add(new());
                }
                if(ImGui.Button("Begin path calculation".Loc()))
                {
                    Chat.ExecuteCommand("/clearlog");
                    var aetheryte = S.Data.ResidentialAethernet.ActiveAetheryte ?? S.Data.ResidentialAethernet.GetFromIGameObject(Svc.Targets.Target);
                    if(aetheryte != null)
                    {
                        P.TaskManager.Enqueue(() => S.Ipc.VnavmeshIPC.Rebuild());
                        P.TaskManager.Enqueue(() => S.Ipc.VnavmeshIPC.IsReady(), TaskSettings.TimeoutInfinite);
                        for(var i = 0; i < plots.Count; i++)
                        {
                            var x = plots[i];
                            if(x.AethernetID == aetheryte.Value.ID)
                            {
                                var index = i;
                                TaskGeneratePath.Enqueue(i, x);
                            }
                        }
                        for(var i = 0; i < plots.Count; i++)
                        {
                            var x = plots[i];
                            if(x.AethernetID != aetheryte.Value.ID && x.Path.Count > 0)
                            {
                                var index = i;
                                TaskGeneratePath.EnqueueValidate(i, x, aetheryte.Value);
                            }
                        }
                        P.TaskManager.Enqueue(() => P.NotificationMasterApi.DisplayTrayNotification("Path Completed".Loc()));
                    }
                }
                if(ImGui.Button($"{"For plot".Loc()} {LastPlot + 1}"))
                {
                    doCurPlot = true;
                }
            }
            List<ImGuiEx.EzTableEntry> entries = [];
            for(var i = 0; i < plots.Count; i++)
            {
                var index = i;
                var plot = plots[i];
                entries.Add(
                    new("Num".Loc(), () => ImGuiEx.Text($"{index + 1}")),
                    new("Front".Loc(), () => ImGuiEx.Text($"{plot.Front}")),
                    new("Aethernet".Loc(), () => ImGuiEx.Text($"{Svc.Data.GetExcelSheet<HousingAethernet>().GetRowOrDefault(plot.AethernetID)?.PlaceName.ValueNullable?.Name ?? plot.AethernetID.ToString()}")),
                    new("Edit".Loc(), () =>
                    {
                        if(ImGui.Button($"{"Edit".Loc()}{index + 1}"))
                        {
                            CurrentPath = plots[index].Path;
                        }
                    }),
                    new("Action".Loc(), () =>
                    {
                        if(ImGui.Button($"{"Set".Loc()}{index + 1}") || (doCurPlot && index == LastPlot))
                        {
                            LastPlot = -1;
                            doCurPlot = false;
                            Chat.ExecuteCommand("/clearlog");
                            DuoLog.Information($"{"For plot".Loc()} {index + 1}");
                            plot.Front = Player.Object.Position;
                            // 🔴 Svc.Objects 的元素是共用包裝(存取時就地改寫 Address),而 .Where 是延後求值:留到下面 Task.Run 的
                            //    執行緒池執行緒上才列舉,讀到的可能已經是別的物件(錯座標寫回設定檔),也可能已經懸空(攔不到的 AccessViolationException)。
                            //    ⇒ 在主執行緒(這裡就是 Draw 回呼)當場把要用的值抄成純資料再交給 Task.Run。
                            //    🔴 只加 .ToList() 不夠:那固化的是包裝物件的清單,包裝本身照樣會被就地改寫。要抄的是值。
                            var candidates = Svc.Objects
                                .Where(x => x.BaseId.EqualsAny(Utils.AethernetShards) && Vector3.Distance(plot.Front, x.Position) < 100f && S.Data.ResidentialAethernet.GetFromIGameObject(x) != null)
                                .Select(SnapshotAethernetCandidate)
                                .ToArray();
                            Task.Run(() =>
                            {
                                var currentDistance = float.MaxValue;
                                var currentAetheryte = -1;
                                foreach(var x in candidates)
                                {
                                    DuoLog.Information($"Candidate: {x.Aetheryte.Name}");
                                    var path = S.Ipc.VnavmeshIPC.Pathfind(plot.Front, x.Position, false);
                                    path.Wait();
                                    if(path.Result != null)
                                    {
                                        var distance = Utils.CalculatePathDistance([.. path.Result]);
                                        DuoLog.Information($"-- Distance: {distance} - best: {distance < currentDistance}");
                                        if(distance < currentDistance)
                                        {
                                            currentDistance = distance;
                                            currentAetheryte = (int)x.Aetheryte.ID;
                                        }
                                    }
                                    else
                                    {
                                        DuoLog.Information($"-- Failed to calculate distance");
                                    }
                                }
                                Svc.Framework.RunOnFrameworkThread(() =>
                                {
                                    plot.AethernetID = (uint)currentAetheryte;
                                    Utils.SaveGeneratedHousingData();
                                });
                            });
                        }
                    }),
                    new("Path".Loc(), () =>
                    {
                        ImGuiEx.Text($"{"Points".Loc()}: {plot.Path.Count}, {"Distance".Loc()}: {Utils.CalculatePathDistance([Player.Object.Position, .. plot.Path])}");
                        if(ImGui.IsItemHovered())
                        {
                            S.Ipc.SplatoonManager.RenderPath(plot.Path);
                        }
                    })
                    );
            }
            if(ImGui.BeginChild("Table"))
            {
                ImGuiEx.EzTable(entries);
            }
            ImGui.EndChild();
        }
        else
        {
            if(ImGui.Button($"{"Create data for".Loc()} {ExcelTerritoryHelper.GetName(P.Territory)}"))
            {
                data[P.Territory] = [];
            }
        }
    }
}
