using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;
using NightmareUI;
using NightmareUI.PrimaryUI;
using System.Globalization;
using Action = System.Action;

namespace Lifestream.GUI;

internal static unsafe class UISettings
{
    private static string AddNew = "";
    internal static void Draw()
    {
        NuiTools.ButtonTabs([[new("General".Loc(), () => Wrapper(DrawGeneral)), new("Overlay".Loc(), () => Wrapper(DrawOverlay))], [new("Expert".Loc(), () => Wrapper(DrawExpert)), new("Service Accounts".Loc(), () => Wrapper(UIServiceAccount.Draw)), new("Travel Block".Loc(), TabTravelBan.Draw)]]);
    }

    private static void Wrapper(Action action)
    {
        ImGui.Dummy(new(5f));
        action();
    }

    private static void DrawGeneral()
    {
        new NuiBuilder()
        .Section("Teleport Configuration".Loc())
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo("Teleport world change gateway".Loc(), ref C.WorldChangeAetheryte, Lang.WorldChangeAetherytes);
            ImGuiEx.HelpMarker("Where would you like to teleport for world changes".Loc());
            ImGui.Checkbox("Teleport to specific aethernet destination after world/dc visit".Loc(), ref C.WorldVisitTPToAethernet);
            if(C.WorldVisitTPToAethernet)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(250f.Scale());
                ImGui.InputText("Aethernet destination, as if you'd use in \"/li\" command".Loc(), ref C.WorldVisitTPTarget, 50);
                ImGui.Checkbox("Only teleport from command but not from overlay".Loc(), ref C.WorldVisitTPOnlyCmd);
                ImGui.Unindent();
            }
            ImGui.Checkbox("Add firmament location into Foundation aetheryte".Loc(), ref C.Firmament);
            ImGui.Checkbox("Automatically leave non cross-world party upon changing world".Loc(), ref C.LeavePartyBeforeWorldChange);
            ImGui.Checkbox("Show teleport destination in chat".Loc(), ref C.DisplayChatTeleport);
            ImGui.Checkbox("Show teleport destination in popup notifications".Loc(), ref C.DisplayPopupNotifications);
            ImGui.Checkbox("Retry same-world failed world visits".Loc(), ref C.RetryWorldVisit);
            ImGui.Indent();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt("Interval between retries, seconds".Loc() + "##2", ref C.RetryWorldVisitInterval.ValidateRange(1, 120));
            ImGui.SameLine();
            ImGuiEx.Text("+ up to".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt("seconds".Loc() + "##2", ref C.RetryWorldVisitIntervalDelta.ValidateRange(0, 120));
            ImGuiEx.HelpMarker("To make it appear less bot-like".Loc());
            ImGui.Unindent();
            //ImGui.Checkbox("Use Return instead of Teleport when possible", ref C.UseReturn);
            //ImGuiEx.HelpMarker("This includes any IPC calls");
            ImGui.Checkbox("Enable tray notifications upon travel completion".Loc(), ref C.EnableNotifications);
            ImGuiEx.PluginAvailabilityIndicator([new("NotificationMaster")]);
        })

        .Section("Shortcuts".Loc())
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo("\"/li\" command behavior".Loc(), ref C.LiCommandBehavior);
            ImGui.Checkbox("When teleporting to your own apartment, enter inside".Loc(), ref C.EnterMyApartment);
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.EnumCombo("When teleporting to your/fc house, perform this action".Loc(), ref C.HouseEnterMode);
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("Preferred Inn".Loc(), Utils.GetInnNameFromTerritory(C.PreferredInn), ImGuiComboFlags.HeightLarge))
            {
                foreach(var x in (uint[])[0, .. TaskPropertyShortcut.InnData.Keys])
                {
                    if(ImGui.Selectable(Utils.GetInnNameFromTerritory(x), x == C.PreferredInn)) C.PreferredInn = x;
                }
                ImGui.EndCombo();
            }
            if(Player.CID != 0)
            {
                ImGui.SetNextItemWidth(150f.Scale());
                var pref = C.PreferredSharedEstates.SafeSelect(Player.CID);
                var name = pref switch
                {
                    (0, 0, 0) => "First available".Loc(),
                    (-1, 0, 0) => "Disable".Loc(),
                    _ => $"{ExcelTerritoryHelper.GetName((uint)pref.Territory)}, W{pref.Ward}, P{pref.Plot}"
                };
                if(ImGui.BeginCombo("Preferred shared estate for ??".Loc(Player.NameWithWorld), name))
                {
                    foreach(var x in Svc.AetheryteList.Where(x => x.IsSharedHouse))
                    {
                        if(ImGui.RadioButton("First available".Loc(), pref == default))
                        {
                            C.PreferredSharedEstates.Remove(Player.CID);
                        }
                        if(ImGui.RadioButton("Disable".Loc(), pref == (-1, 0, 0)))
                        {
                            C.PreferredSharedEstates[Player.CID] = (-1, 0, 0);
                        }
                        if(ImGui.RadioButton("??, Ward ??, Plot ??".Loc(ExcelTerritoryHelper.GetName(x.TerritoryId), x.Ward, x.Plot), pref == ((int)x.TerritoryId, x.Ward, x.Plot)))
                        {
                            C.PreferredSharedEstates[Player.CID] = ((int)x.TerritoryId, x.Ward, x.Plot);
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.Separator();
            ImGuiEx.Text("\"/li auto\" command priority:".Loc());
            ImGui.SameLine();
            if(ImGui.SmallButton("Reset".Loc())) C.PropertyPrio.Clear();
            var dragDrop = Ref<ImGuiEx.RealtimeDragDrop<AutoPropertyData>>.Get(() => new("apddd", x => x.Type.ToString()));
            C.PropertyPrio.AddRange(Enum.GetValues<TaskPropertyShortcut.PropertyType>().Where(x => x != TaskPropertyShortcut.PropertyType.Auto && !C.PropertyPrio.Any(s => s.Type == x)).Select(x => new AutoPropertyData(false, x)));
            dragDrop.Begin();
            for(var i = 0; i < C.PropertyPrio.Count; i++)
            {
                var d = C.PropertyPrio[i];
                ImGui.PushID($"c{i}");
                dragDrop.NextRow();
                dragDrop.DrawButtonDummy(d, C.PropertyPrio, i);
                ImGui.SameLine();
                ImGui.Checkbox($"{d.Type}", ref d.Enabled);
                ImGui.PopID();
            }
            dragDrop.End();
            ImGui.Separator();
        })

        .Section("Map Integration".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Click Aethernet Shard on map for quick teleport".Loc(), ref C.UseMapTeleport);
            ImGui.Checkbox("Only process when next to aetheryte in the same map".Loc(), ref C.DisableMapClickOtherTerritory);
        })

        .Section("Command completion".Loc())
        .Widget(() =>
        {
            ImGuiEx.Text("Suggest autocompletion when typing Lifestream commands in chat".Loc());
            ImGui.Checkbox("Enable".Loc(), ref C.EnableAutoCompletion);
            ImGui.Checkbox("Display popup window at fixed position".Loc(), ref C.AutoCompletionFixedWindow);
            ImGui.Indent();
            ImGui.SetNextItemWidth(200f.Scale());
            ImGui.DragFloat2("Position".Loc(), ref C.AutoCompletionWindowOffset, 1f);
            ImGuiEx.RadioButtonBool("From bottom".Loc(), "From top".Loc(), ref C.AutoCompletionWindowBottom, sameLine: true, inverted: true);
            ImGuiEx.RadioButtonBool("From right".Loc(), "From left".Loc(), ref C.AutoCompletionWindowRight, sameLine: true, inverted: true);
            ImGui.Unindent();
        })

        .Section("Cross-Datacenter".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Allow travelling to another data center".Loc(), ref C.AllowDcTransfer);
            ImGui.Checkbox("Leave party before switching data center".Loc(), ref C.LeavePartyBeforeLogout);
            ImGui.Checkbox("Teleport to gateway aetheryte before switching data center if not in sanctuary".Loc(), ref C.TeleportToGatewayBeforeLogout);
            ImGui.Checkbox("Teleport to gateway aetheryte after completing data center travel".Loc(), ref C.DCReturnToGateway);
            ImGui.Checkbox("Allow alternative world during DC transfer".Loc(), ref C.DcvUseAlternativeWorld);
            ImGuiEx.HelpMarker("If destination world isn't available but some other world on targeted data center is, it will be selected instead. Normal world visit will be enqueued after logging in.".Loc());
            ImGui.Checkbox("Retry data center transfer if destination world is not available".Loc(), ref C.EnableDvcRetry);
            ImGui.Indent();
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt("Max retries".Loc(), ref C.MaxDcvRetries.ValidateRange(1, int.MaxValue));
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt("Interval between retries, seconds".Loc(), ref C.DcvRetryInterval.ValidateRange(10, 1000));
            ImGui.Unindent();
        })

        .Section("Address Book".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Disable pathing to a plot".Loc(), ref C.AddressNoPathing);
            ImGuiEx.HelpMarker("You will be left at a closest aetheryte to the ward".Loc());
            ImGui.Checkbox("Disable entering an apartment".Loc(), ref C.AddressApartmentNoEntry);
            ImGuiEx.HelpMarker("You will be left at an entry confirmation dialogue".Loc());
        })

        .Section("Movement".Loc())
        .Checkbox("Use Mount when auto-moving".Loc(), () => ref C.UseMount)
        .Widget(() =>
        {
            Dictionary<int, string> mounts = [new KeyValuePair<int, string>(0, "Mount roulette".Loc()), .. Svc.Data.GetExcelSheet<Mount>().Where(x => x.Singular != "").ToDictionary(x => (int)x.RowId, x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Singular.GetText()))];
            ImGui.SetNextItemWidth(200f);
            ImGuiEx.Combo("Preferred Mount".Loc(), ref C.Mount, mounts.Keys, names: mounts);
        })
        .Checkbox("Use Sprint when auto-moving".Loc(), () => ref C.UseSprintPeloton)
        .Checkbox("Use Peloton when auto-moving".Loc(), () => ref C.UsePeloton)

        .Section("Character Select Menu".Loc())
        .Checkbox("Enable Data center and World visit from Character Select Menu".Loc(), () => ref C.AllowDCTravelFromCharaSelect)
        .Checkbox("Use world visit instead of DC visit to travel to same world on guest DC".Loc(), () => ref C.UseGuestWorldTravel)

        .Section("Wotsit Integration".Loc())
        .Widget(() =>
        {
            var anyChanged = ImGui.Checkbox("Enable Wotsit Integration for teleporting to Aethernet destinations".Loc(), ref C.WotsitIntegrationEnabled);
            ImGuiEx.PluginAvailabilityIndicator([new("Dalamud.FindAnything", "Wotsit")]);

            if(C.WotsitIntegrationEnabled)
            {
                ImGui.Indent();
                if(ImGui.Checkbox("Include world select window".Loc(), ref C.WotsitIntegrationIncludes.WorldSelect))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include auto-teleport to property".Loc(), ref C.WotsitIntegrationIncludes.PropertyAuto))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to private estate".Loc(), ref C.WotsitIntegrationIncludes.PropertyPrivate))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to free company estate".Loc(), ref C.WotsitIntegrationIncludes.PropertyFreeCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to apartment".Loc(), ref C.WotsitIntegrationIncludes.PropertyApartment))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to inn room".Loc(), ref C.WotsitIntegrationIncludes.PropertyInn))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to grand company".Loc(), ref C.WotsitIntegrationIncludes.GrandCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to market board".Loc(), ref C.WotsitIntegrationIncludes.MarketBoard))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to island sanctuary".Loc(), ref C.WotsitIntegrationIncludes.IslandSanctuary))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include auto-teleport to aethernet destinations".Loc(), ref C.WotsitIntegrationIncludes.AetheryteAethernet))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include address book entries".Loc(), ref C.WotsitIntegrationIncludes.AddressBook))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include custom aliases".Loc(), ref C.WotsitIntegrationIncludes.CustomAlias))
                {
                    anyChanged = true;
                }
                ImGui.Unindent();
            }

            if(anyChanged)
            {
                PluginLog.Debug("Wotsit integration settings changed, re-initializing immediately");
                S.Ipc.WotsitManager.TryClearWotsit();
                S.Ipc.WotsitManager.MaybeTryInit(true);
            }
        })

        .Draw();
    }

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
                    foreach(var x in RaptureAtkUnitManager.Instance()->FocusedUnitsList.Entries)
                    {
                        if(x.Value == null) continue;
                        focused.Add(x.Value->NameString);
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

    private static void DrawExpert()
    {
        new NuiBuilder()
        .Section("Expert Settings".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Slow down aetheryte teleporting".Loc(), ref C.SlowTeleport);
            ImGuiEx.HelpMarker("Slows down aethernet teleportation by specified amount.".Loc());
            if(C.SlowTeleport)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.DragInt("Teleport delay (ms)".Loc(), ref C.SlowTeleportThrottle);
                ImGui.Unindent();
            }
            ImGuiEx.CheckboxInverted("Skip waiting until game screen is ready".Loc(), ref C.WaitForScreenReady);
            ImGuiEx.HelpMarker("Enable this option for faster teleports but be careful that you may get stuck.".Loc());
            ImGui.Checkbox("Hide progress bar".Loc(), ref C.NoProgressBar);
            ImGuiEx.HelpMarker("Hiding progress bar leaves you with no way to stop Lifestream from executing it's tasks.".Loc());
            ImGuiEx.CheckboxInverted("Don't walk to nearby aetheryte on world change command from greater distance".Loc(), ref C.WalkToAetheryte);
            ImGui.Checkbox("Progress overlay at top of the sreen".Loc(), ref C.ProgressOverlayToTop);
            ImGui.Checkbox("Allow custom alias and house alias to override built-in commands".Loc(), ref C.AllowCustomOverrides);
            ImGui.Indent();
            ImGuiEx.TextWrapped(EColor.RedBright, "Warning! Other plugins may rely on built-in commands. Ensure that it is not the case if you decide to enable this option and override commands.".Loc());
            ImGui.Unindent();
        })
        .Draw();
    }
}
