using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Systems;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;
using NightmareUI;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;
using System.Globalization;
using Action = System.Action;

namespace Lifestream.GUI;

internal static unsafe partial class UISettings
{
    private static void DrawOverlay()
    {
        new NuiBuilder()
        .Section("General Overlay Settings".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Enable Overlay".Loc(), ref C.Enable);
            if(C.Enable)
            {
                ImGui.Indent();
                ImGui.Checkbox("Display Aethernet menu".Loc(), ref C.ShowAethernet);
                ImGui.Checkbox("Display World Visit menu".Loc(), ref C.ShowWorldVisit);
                ImGui.Checkbox("Display Housing Ward buttons".Loc(), ref C.ShowWards);

                UtilsUI.NextSection();

                ImGui.Checkbox("Fixed Lifestream Overlay position".Loc(), ref C.FixedPosition);
                if(C.FixedPosition)
                {
                    ImGui.Indent();
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("Horizontal base position".Loc(), ref C.PosHorizontal);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("Vertical base position".Loc(), ref C.PosVertical);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGui.DragFloat2("Offset".Loc(), ref C.Offset);

                    ImGui.Unindent();
                }

                UtilsUI.NextSection();

                ImGui.SetNextItemWidth(100f.Scale());
                fixed(int* ptr = &C.ButtonWidthArray[0])
                fixed(byte* sptr = System.Text.Encoding.UTF8.GetBytes("Button left/right padding".Loc() + "\0"))
                {
                    ImGuiNative.InputInt3(sptr, ptr, ImGuiInputTextFlags.None);
                }
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("Aetheryte button top/bottom padding".Loc(), ref C.ButtonHeightAetheryte);
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("World button top/bottom padding".Loc(), ref C.ButtonHeightWorld);
                ImGui.Unindent();

                ImGui.Checkbox("Left-align text on buttons".Loc(), ref C.LeftAlignButtons);
                if(C.LeftAlignButtons)
                {
                    ImGui.SetNextItemWidth(100f);
                    ImGui.DragInt("Left padding, spaces".Loc(), ref C.LeftAlignPadding, 0.1f, 0, 20);
                }
            }
        })

        .Section("Instance changer".Loc())
        .Checkbox("Enabled".Loc(), () => ref C.ShowInstanceSwitcher)
        .Checkbox("Retry on failure".Loc(), () => ref C.InstanceSwitcherRepeat)
        .Checkbox("Return to the ground when flying before changing instance".Loc(), () => ref C.EnableFlydownInstance)
        .Checkbox("Teleport to the zone's aetheryte first if none is nearby".Loc(), () => ref C.InstanceTpToAetheryte)
        .Checkbox("Summon mount again after changing instance".Loc(), () => ref C.InstanceRemount)
        .Widget("Display instance number in Server Info Bar".Loc(), (x) =>
        {
            if(ImGui.Checkbox(x, ref C.EnableDtrBar))
            {
                S.DtrManager.Refresh();
            }
        })
        .SliderInt(150f, "Extra button height".Loc(), () => ref C.InstanceButtonHeight, 0, 50)
        .Widget("Reset Instance Data".Loc(), (x) =>
        {
            if(ImGuiEx.Button(x, C.PublicInstances.Count > 0))
            {
                C.PublicInstances.Clear();
                EzConfig.Save();
            }
        })

        .Section("Game Window Integration".Loc())
        .Checkbox("Hide Lifestream if the following game windows are open".Loc(), () => ref C.HideAddon)
        .If(() => C.HideAddon)
        .Widget(() =>
        {
            if(ImGui.BeginTable("HideAddonTable", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("col2");

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiEx.SetNextItemFullWidth();
                ImGui.InputTextWithHint("##addnew", "Window name... /xldata ai - to find it".Loc(), ref AddNew, 100);
                ImGui.TableNextColumn();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
                {
                    C.HideAddonList.Add(AddNew);
                    AddNew = "";
                }

                List<string> focused = [];
                try
                {
                    // 🔴 外面這層 try/catch 對這一行是**假安全**：RaptureAtkUnitManager.Instance()
                    //    是 CS 的手寫包裝（RaptureAtkModule 為 null 時回 null），裸解參考產生的是
                    //    AccessViolationException —— 在 .NET Core 屬 corrupted-state exception，
                    //    catch(Exception) 攔不到，只能事前擋。try 保留給 NameString 那類受管理例外。
                    //    取不到就維持空清單，畫面顯示「沒有聚焦中的視窗」，不崩潰。
                    var raptureAtkUnitManager = RaptureAtkUnitManager.Instance();
                    if(raptureAtkUnitManager != null)
                    {
                        foreach(var x in raptureAtkUnitManager->FocusedUnitsList.Entries)
                        {
                            if(x.Value == null) continue;
                            focused.Add(x.Value->NameString);
                        }
                    }
                }
                catch(Exception e) { e.Log(); }

                if(focused != null)
                {
                    foreach(var name in focused)
                    {
                        if(name == null) continue;
                        if(C.HideAddonList.Contains(name)) continue;
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGuiEx.TextV(EColor.Green, "Focused: ??".Loc(name));
                        ImGui.TableNextColumn();
                        ImGui.PushID(name);
                        if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
                        {
                            C.HideAddonList.Add(name);
                        }
                        ImGui.PopID();
                    }
                }

                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, 0x88888888);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, 0x88888888);
                ImGui.TableNextColumn();
                ImGui.Dummy(new Vector2(5f));

                foreach(var s in C.HideAddonList)
                {
                    ImGui.PushID(s);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGuiEx.TextV(focused.Contains(s) ? EColor.Green : null, s);
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                    {
                        new TickScheduler(() => C.HideAddonList.Remove(s));
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        })
        .EndIf()
        .Draw();

        if(C.Hidden.Count > 0)
        {
            new NuiBuilder()
            .Section("Hidden Aetherytes".Loc())
            .Widget(() =>
            {
                uint toRem = 0;
                foreach(var x in C.Hidden)
                {
                    ImGuiEx.Text($"{Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(x)?.AethernetName.ValueNullable?.Name.ToString() ?? x.ToString()}");
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Delete".Loc() + $"##{x}"))
                    {
                        toRem = x;
                    }
                }
                if(toRem > 0)
                {
                    C.Hidden.Remove(toRem);
                }
            })
            .Draw();
        }
    }
}
