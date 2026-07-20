using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Reflection;
using ECommons.SplatoonAPI;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.Services;
using NightmareUI;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;

namespace Lifestream.GUI;
#nullable enable
public static unsafe class UIHouseReg
{
    public static ImGuiEx.RealtimeDragDrop<Vector3> PathDragDrop = new("UIHouseReg", (x) => x.ToString());

    public static void Draw()
    {
        if(Player.Available)
        {
            NuiTools.ButtonTabs([[new("私人宅邸", DrawPrivate), new("部隊宅邸", DrawFC), new("自訂宅邸", DrawCustom), new("總覽", DrawOverview)]]);
        }
        else
        {
            ImGuiEx.TextWrapped("請先登入以建立與編輯註冊資料。");
            DrawOverview();
        }
    }

    private static ImGuiEx.RealtimeDragDrop<(ulong CID, HousePathData? Private, HousePathData? FC)> DragDropPathData = new("DragDropHPD", (x) => x.CID.ToString());
    private static string Search = "";
    private static int World = 0;
    private static WorldSelector WorldSelector = new()
    {
        DisplayCurrent = true,
        ShouldHideWorld = (x) => !C.HousePathDatas.Any(s => Utils.GetWorldFromCID(s.CID) == ExcelWorldHelper.GetName(x)),
        EmptyName = "所有世界",
        DefaultAllOpen = true,
    };

    private static void DrawOverview()
    {
        ImGuiEx.InputWithRightButtonsArea(() =>
        {
            ImGui.InputTextWithHint("##search", "搜尋...", ref Search, 50);
        }, () =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            WorldSelector.Draw(ref World);
        });
        List<(ulong CID, HousePathData? Private, HousePathData? FC)> charaDatas = [];
        foreach(var x in C.HousePathDatas.Select(x => x.CID).Distinct())
        {
            charaDatas.Add((x, C.HousePathDatas.FirstOrDefault(z => z.IsPrivate && z.CID == x), C.HousePathDatas.FirstOrDefault(z => !z.IsPrivate && z.CID == x)));
        }
        DragDropPathData.Begin();
        if(ImGuiEx.BeginDefaultTable("##charaTable", ["##move", "~名稱或 CID", "私人宅邸", "##privateCtl", "##privateCtl2", "##privateDlm", "部隊宅邸", "##FCCtl", "工房", "##workshopCtl", "##fcCtl", "##fcCtl2"]))
        {
            for(var i = 0; i < charaDatas.Count; i++)
            {
                var charaData = charaDatas[i];
                var charaName = Utils.GetCharaName(charaData.CID);
                if(Search != "" && !charaName.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
                if(World != 0 && Utils.GetWorldFromCID(charaData.CID) != ExcelWorldHelper.GetName(World)) continue;
                ImGui.PushID($"{charaData}");
                var priv = charaData.Private;
                var fc = charaData.FC;
                var entry = (priv ?? fc)!;
                ImGui.TableNextRow();
                DragDropPathData.SetRowColor(entry.CID.ToString());
                ImGui.TableNextColumn();
                DragDropPathData.NextRow();
                DragDropPathData.DrawButtonDummy(charaData.CID.ToString(), charaDatas, i);
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{charaName}");
                ImGui.TableNextColumn();
                if(priv != null)
                {
                    NuiTools.RenderResidentialIcon((uint)priv.ResidentialDistrict.GetResidentialTerritory());
                    ImGui.SameLine();
                    ImGuiEx.Text($"W{priv.Ward + 1}, P{priv.Plot + 1}{(priv.PathToEntrance.Count > 0 ? "\uff0c+\u8def\u5f91" : "")}");
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton((FontAwesomeIcon)'\ue50b', "DelePrivate", enabled: ImGuiEx.Ctrl))
                    {
                        new TickScheduler(() => C.HousePathDatas.RemoveAll(z => z.IsPrivate && z.CID == charaData.CID));
                    }
                    ImGuiEx.Tooltip("\u79fb\u9664\u79c1\u4eba\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599\u3002\u6309\u4f4f CTRL \u4e26\u9ede\u64ca\u3002");
                    if(priv.PathToEntrance.Count > 0)
                    {
                        ImGui.SameLine();
                        if(ImGuiEx.IconButton((FontAwesomeIcon)'\ue566', "DelePrivatePath", enabled: ImGuiEx.Ctrl))
                        {
                            priv.PathToEntrance.Clear();
                        }
                        ImGuiEx.Tooltip("\u79fb\u9664\u524d\u5f80\u79c1\u4eba\u5b85\u90b8\u7684\u8def\u5f91\u3002\u6309\u4f4f CTRL \u4e26\u9ede\u64ca\u3002");
                    }

                    ImGui.SameLine();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Copy, "CopyPrivatePath"))
                    {
                        Copy(EzConfig.DefaultSerializationFactory.Serialize(priv)!);
                    }
                    ImGuiEx.Tooltip("\u8907\u88fd\u79c1\u4eba\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599\u5230\u526a\u8cbc\u7c3f");
                    ImGui.SameLine();
                }
                else
                {
                    ImGuiEx.TextV(ImGuiColors.DalamudGrey3, "\u5c1a\u672a\u8a3b\u518a");
                    ImGui.TableNextColumn();
                }

                ImGui.TableNextColumn();

                if(ImGuiEx.IconButton(FontAwesomeIcon.Paste, "PastePriva"))
                {
                    ImportFromClipboard(charaData.CID, true);
                }
                ImGuiEx.Tooltip("\u5f9e\u526a\u8cbc\u7c3f\u8cbc\u4e0a\u79c1\u4eba\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599");

                ImGui.TableNextColumn();
                //delimiter
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetStyle().Colors[(int)ImGuiCol.TableBorderLight].ToUint());

                ImGui.TableNextColumn();
                if(fc != null)
                {
                    NuiTools.RenderResidentialIcon((uint)fc.ResidentialDistrict.GetResidentialTerritory());
                    ImGui.SameLine();
                    ImGuiEx.Text($"W{fc.Ward + 1}, P{fc.Plot + 1}{(fc.PathToEntrance.Count > 0 ? "\uff0c+\u8def\u5f91" : "")}");
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton((FontAwesomeIcon)'\ue50b', "DeleFc", enabled: ImGuiEx.Ctrl))
                    {
                        new TickScheduler(() => C.HousePathDatas.RemoveAll(z => !z.IsPrivate && z.CID == charaData.CID));
                    }
                    ImGuiEx.Tooltip("\u79fb\u9664\u90e8\u968a\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599\u3002\u6309\u4f4f CTRL \u4e26\u9ede\u64ca\u3002");
                    if(fc.PathToEntrance.Count > 0)
                    {
                        ImGui.SameLine();
                        if(ImGuiEx.IconButton((FontAwesomeIcon)'\ue566', "DeleFcPath", enabled: ImGuiEx.Ctrl))
                        {
                            fc.PathToEntrance.Clear();
                        }
                        ImGuiEx.Tooltip("\u79fb\u9664\u524d\u5f80\u90e8\u968a\u5b85\u90b8\u7684\u8def\u5f91\u3002\u6309\u4f4f CTRL \u4e26\u9ede\u64ca\u3002");
                    }
                }
                else
                {
                    ImGuiEx.TextV(ImGuiColors.DalamudGrey3, "\u5c1a\u672a\u8a3b\u518a");
                    ImGui.TableNextColumn();
                }

                ImGui.TableNextColumn();
                if(fc == null || fc.PathToWorkshop.Count == 0)
                {
                    ImGuiEx.TextV(ImGuiColors.DalamudGrey3, "\u5c1a\u672a\u8a3b\u518a");
                    ImGui.TableNextColumn();
                }
                else
                {
                    ImGuiEx.TextV($"{fc.PathToWorkshop.Count} \u500b\u5ea7\u6a19\u9ede");
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton((FontAwesomeIcon)'\ue566', "DeleFcWorkshopPath", enabled: ImGuiEx.Ctrl))
                    {
                        fc.PathToWorkshop.Clear();
                    }
                    ImGuiEx.Tooltip("\u79fb\u9664\u524d\u5f80\u5de5\u623f\u7684\u8def\u5f91\u3002\u6309\u4f4f CTRL \u4e26\u9ede\u64ca\u3002");
                }

                ImGui.TableNextColumn();

                if(fc != null)
                {
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Copy, "CopyFCPath"))
                    {
                        Copy(EzConfig.DefaultSerializationFactory.Serialize(fc)!);
                    }
                    ImGuiEx.Tooltip("\u8907\u88fd\u90e8\u968a\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599\u5230\u526a\u8cbc\u7c3f");
                    ImGui.SameLine();
                }

                ImGui.TableNextColumn();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Paste, "PasteFC"))
                {
                    ImportFromClipboard(charaData.CID, false);
                }
                ImGuiEx.Tooltip("\u5f9e\u526a\u8cbc\u7c3f\u8cbc\u4e0a\u90e8\u968a\u5b85\u90b8\u8a3b\u518a\u8cc7\u6599");
                ImGui.PopID();
            }

            ImGui.EndTable();
            DragDropPathData.End();
        }
        C.HousePathDatas.Clear();
        foreach(var x in charaDatas)
        {
            if(x.Private != null) C.HousePathDatas.Add(x.Private);
            if(x.FC != null) C.HousePathDatas.Add(x.FC);
        }
    }

    private static void ImportFromClipboard(ulong cid, bool isPrivate)
    {
        new TickScheduler(() =>
        {
            try
            {
                var data = EzConfig.DefaultSerializationFactory.Deserialize<HousePathData>(Paste()!) ?? throw new NullReferenceException("No suitable data forund in clipboard");
                if(!data.GetType().GetFieldPropertyUnions().All(x => x.GetValue(data) != null)) throw new NullReferenceException("Clipboard contains invalid data");
                var existingData = C.HousePathDatas.FirstOrDefault(x => x.CID == cid && x.IsPrivate == isPrivate);
                var same = existingData != null && existingData.Ward == data.Ward && existingData.Plot == data.Plot && existingData.ResidentialDistrict == data.ResidentialDistrict;
                if(same || ImGuiEx.Ctrl)
                {
                    data.CID = cid;
                    var index = C.HousePathDatas.IndexOf(s => s.CID == data.CID && s.IsPrivate == isPrivate);
                    if(index == -1)
                    {
                        C.HousePathDatas.Add(data);
                    }
                    else
                    {
                        C.HousePathDatas[index] = data;
                    }
                }
                else
                {
                    Notify.Error($"此角色已註冊了不同的{(isPrivate ? "私人宅邸房號" : "部隊宅邸房號")}。若要覆蓋，請按住 CTRL 並點擊貼上按鈕。");
                }
            }
            catch(Exception e)
            {
                Notify.Error(e.Message);
                e.Log();
            }
        });
    }

    private static void DrawFC()
    {
        var data = Utils.GetFCPathData();
        DrawHousingData(data, false);
    }

    private static void DrawPrivate()
    {
        var data = Utils.GetPrivatePathData();
        DrawHousingData(data, true);
    }

    private static void DrawCustom()
    {
        if(TryGetCurrentPlotInfo(out var kind, out var ward, out var plot))
        {
            if(C.HousePathDatas.TryGetFirst(x => x.ResidentialDistrict == kind && x.Ward == ward && x.Plot == plot, out var regData))
            {
                ImGuiEx.TextWrapped($"這棟宅邸已被角色 {Utils.GetCharaName(regData.CID)} 註冊為{(regData.IsPrivate ? "私人宅邸" : "部隊宅邸")}，無法再註冊為自訂宅邸。");
            }
            else
            {
                var data = C.CustomHousePathDatas.FirstOrDefault(x => x.Ward == ward && x.Plot == plot && x.ResidentialDistrict == kind);
                if(data == null)
                {
                    if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "將此宅邸註冊為自訂宅邸"))
                    {
                        C.CustomHousePathDatas.Add(new()
                        {
                            ResidentialDistrict = kind,
                            Plot = plot,
                            Ward = ward
                        });
                    }
                }
                else
                {
                    if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Trash, "取消註冊此宅邸", ImGuiEx.Ctrl))
                    {
                        new TickScheduler(() => C.CustomHousePathDatas.Remove(data));
                    }
                    DrawHousingData_DrawPath(data, false, kind, ward, plot);
                }
            }
        }
        else
        {
            ImGuiEx.TextWrapped($"請前往該房號以將其註冊為自訂宅邸。註冊自訂宅邸後，其路徑可用於共用宅邸傳送與通訊錄傳送。");
        }
    }

    private static void DrawHousingData(HousePathData? data, bool isPrivate)
    {
        var plotDataAvailable = TryGetCurrentPlotInfo(out var kind, out var ward, out var plot);
        if(data == null)
        {
            ImGuiEx.Text($"找不到資料。");
            if(plotDataAvailable && Player.IsInHomeWorld)
            {
                if(ImGui.Button($"將 {kind.GetName()} 第 {ward + 1} 區第 {plot + 1} 號註冊為{(isPrivate ? "私人" : "部隊")}宅邸。"))
                {
                    var newData = new HousePathData()
                    {
                        CID = Player.CID,
                        Plot = plot,
                        Ward = ward,
                        ResidentialDistrict = kind,
                        IsPrivate = isPrivate
                    };
                    C.HousePathDatas.Add(newData);
                }
            }
            else
            {
                ImGuiEx.Text($"請前往你的房號以註冊資料。");
            }
        }
        else
        {
            ImGuiEx.TextWrapped(ImGuiColors.ParsedGreen, $"{data.ResidentialDistrict.GetName()} 第 {data.Ward + 1} 區第 {data.Plot + 1} 號已註冊為{(data.IsPrivate ? "私人" : "部隊")}宅邸。");
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Trash, "移除註冊", ImGuiEx.Ctrl))
            {
                C.HousePathDatas.Remove(data);
            }
            ImGui.Checkbox("覆蓋傳送行為", ref data.EnableHouseEnterModeOverride);
            if(data.EnableHouseEnterModeOverride)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150f.Scale());
                ImGuiEx.EnumCombo("##override", ref data.EnterModeOverride);
            }
            DrawHousingData_DrawPath(data, isPrivate, kind, ward, plot);
        }
    }

    public static void DrawHousingData_DrawPath(HousePathData data, bool isPrivate, ResidentialAetheryteKind kind, int ward, int plot)
    {
        if(data.ResidentialDistrict == kind && data.Ward == ward && data.Plot == plot)
        {
            if(!Utils.IsInsideHouse())
            {
                var path = data.PathToEntrance;
                new NuiBuilder()
                    .Section("前往宅邸的路徑")
                    .Widget(() =>
                    {
                        ImGuiEx.TextWrapped($"建立從房號入口到宅邸入口的路徑。路徑的第一個點應該稍微位於你的房號範圍內，讓你在傳送後可以直線跑到該點；最後一個點則應位於宅邸入口旁，讓你可以從該處進入宅邸。");

                        ImGui.PushID($"path{isPrivate}");
                        DrawPathEditor(path, data);
                        ImGui.PopID();

                    }).Draw();
            }
            else if(!isPrivate)
            {
                var path = data.PathToWorkshop;
                new NuiBuilder()
                    .Section("前往工房的路徑")
                    .Widget(() =>
                    {
                        ImGuiEx.TextWrapped($"建立從宅邸入口到工房／私人房間入口的路徑。");

                        ImGui.PushID($"workshop");
                        DrawPathEditor(path, data);
                        ImGui.PopID();

                    }).Draw();
            }
            else
            {
                ImGuiEx.TextWrapped("請前往已註冊的房號以編輯路徑");
            }
        }
        else
        {
            ImGuiEx.TextWrapped("請前往已註冊的房號以編輯路徑");
        }
    }

    public static void DrawPathEditor(List<Vector3> path, HousePathData? data = null)
    {
        if(!TerritoryWatcher.IsDataReliable())
        {
            ImGuiEx.Text(EColor.RedBright, $"目前無法編輯宅邸路徑。\n請退出並重新進入你的宅邸。");
            return;
        }
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "新增至清單末端"))
        {
            path.Add(Player.Position);
        }
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "新增至清單開頭"))
        {
            path.Insert(0, Player.Position);
        }
        if(data != null)
        {
            var entryPoint = Utils.GetPlotEntrance(data.ResidentialDistrict.GetResidentialTerritory(), data.Plot);
            if(entryPoint != null)
            {
                ImGui.SameLine();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "測試", data.ResidentialDistrict.GetResidentialTerritory() == P.Territory && Vector3.Distance(Player.Position, entryPoint.Value) < 10f))
                {
                    P.FollowPath.Move(data.PathToEntrance, true);
                }
                ImGui.SameLine();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "測試工房路徑", data.PathToWorkshop.Count > 0 && Utils.IsInsideHouse()))
                {
                    P.FollowPath.Move(data.PathToWorkshop, true);
                }
                if(ImGui.IsItemHovered())
                {
                    ImGuiEx.Tooltip($"""
                        住宅區地圖：{data.ResidentialDistrict.GetResidentialTerritory()}
                        玩家所在地圖：{P.Territory}
                        距入口點的距離：{Vector3.Distance(Player.Position, entryPoint.Value)}
                        """);
                }
            }
        }
        PathDragDrop.Begin();
        if(ImGui.BeginTable($"pathtable", 4, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##num");
            ImGui.TableSetupColumn("##move");
            ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##control");
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGuiEx.Text($"房號入口");

            for(var i = 0; i < path.Count; i++)
            {
                ImGui.PushID($"point{i}");
                var p = path[i];
                ImGui.TableNextRow();
                PathDragDrop.SetRowColor(p.ToString());
                ImGui.TableNextColumn();
                PathDragDrop.NextRow();
                ImGuiEx.TextV($"{i + 1}");
                ImGui.TableNextColumn();
                PathDragDrop.DrawButtonDummy(p, path, i);
                Visualise();
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{p:F1}");
                Visualise();

                ImGui.TableNextColumn();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.MapPin, "移至我的位置"))
                {
                    path[i] = Player.Position;
                }
                Visualise();
                ImGui.SameLine();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Trash, "刪除", ImGuiEx.Ctrl))
                {
                    var toRem = i;
                    new TickScheduler(() => path.RemoveAt(toRem));
                }
                Visualise();
                ImGui.PopID();

                void Visualise()
                {
                    if(ImGui.IsItemHovered() && Splatoon.IsConnected())
                    {
                        var e = new Element(ElementType.CircleAtFixedCoordinates);
                        e.SetRefCoord(p);
                        e.Filled = false;
                        e.thicc = 2f;
                        e.radius = (Environment.TickCount64 % 1000f / 1000f) * 2f;
                        Splatoon.DisplayOnce(e);
                    }
                }
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGuiEx.Text($"宅邸入口");

            ImGui.EndTable();
        }
        PathDragDrop.End();

        S.Ipc.SplatoonManager.RenderPath(path, false, true);
    }

    private static bool IsOutside()
    {
        return S.Data.ResidentialAethernet.ZoneInfo.ContainsKey(P.Territory);
    }

    public static bool TryGetCurrentPlotInfo(out ResidentialAetheryteKind kind, out int ward, out int plot)
    {
        var h = HousingManager.Instance();
        if(h != null)
        {
            ward = h->GetCurrentWard();
            plot = h->GetCurrentPlot();
            if(ward < 0 || plot < 0)
            {
                kind = default;
                return false;
            }
            kind = Utils.GetResidentialAetheryteByTerritoryType(P.Territory) ?? 0;
            return kind != 0;
        }
        kind = default;
        ward = default;
        plot = default;
        return false;
    }
}
