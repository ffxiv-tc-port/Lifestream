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
            // 目的地就在同一區又很近時,乾脆用走的 —— 省下整整一次讀取畫面。
            // 🔴 預設 0(關):這會改變語意(點了傳送面板卻用走的),所以必須由使用者自己開,而且門檻可調。
            ImGui.SetNextItemWidth(200f.Scale());
            ImGui.SliderFloat("Walk instead of using the aethernet when closer than, yalms".Loc(), ref C.SkipAethernetIfCloserThan, 0f, 150f, "%.0f");
            ImGuiEx.HelpMarker("0 turns this off (default). When the destination aethernet shard is in the zone you are already in and closer than this, Lifestream simply walks there instead of taking the aethernet - that saves a whole loading screen. Straight-line distance, not path length. If it cannot walk there in time it falls back to the normal aethernet route.".Loc());
            ImGui.Checkbox("Add firmament location into Foundation aetheryte".Loc(), ref C.Firmament);
            ImGuiEx.HelpMarker("Also lists the Firmament and its eight city aethernet shards in the teleport panel, grouped under the Foundation aetheryte - that is what lets you add them to your favorites.".Loc());
            ImGui.Checkbox("Add Sinus Ardorum location into Bestways Burrow aetheryte".Loc(), ref C.SinusArdorum);
            ImGuiEx.HelpMarker("Also lists Sinus Ardorum in the teleport panel, grouped under the Bestways Burrow aetheryte - that is what lets you add it to your favorites.".Loc());
            ImGui.Checkbox("Automatically leave non cross-world party upon changing world".Loc(), ref C.LeavePartyBeforeWorldChange);
            ImGui.Checkbox("Show teleport destination in chat".Loc(), ref C.DisplayChatTeleport);
            ImGui.Checkbox("Show teleport destination in popup notifications".Loc(), ref C.DisplayPopupNotifications);
            ImGui.Checkbox("Retry same-world failed world visits".Loc(), ref C.RetryWorldVisit);
            ImGui.Indent();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt(LocText.IntervalBetweenRetriesSeconds.Loc() + "##2", ref C.RetryWorldVisitInterval.ValidateRange(1, 120));
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
                // CID 在換世界／切換區域時仍非 0，但那幾幀 LocalPlayer 是 null、NameWithWorld 回 null（Loc 對 null 參數會擲）。
                if(ImGui.BeginCombo("Preferred shared estate for ??".Loc(Player.NameWithWorld ?? "?"), name))
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
            ImGui.InputInt(LocText.IntervalBetweenRetriesSeconds.Loc(), ref C.DcvRetryInterval.ValidateRange(10, 1000));
            ImGui.Unindent();
        })

        .Section("Unavailable worlds".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("Worlds listed here are never offered as a travel destination.".Loc());
            ImGuiEx.HelpMarker("This only controls where you can go. World names are still recognised everywhere else, so existing address book entries, name matching and the lobby world list keep working. Taiwan's Ramuh (4034) is excluded out of the box because that world was shut down.".Loc());
            if(C.UnavailableWorlds.Count == 0)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "Nothing is excluded - every world is offered as a destination.".Loc());
            }
            else
            {
                // ToArray:迴圈裡會 Remove，不可以直接走訪原集合。
                foreach(var worldId in C.UnavailableWorlds.ToArray())
                {
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, $"UnavailableWorld{worldId}"))
                    {
                        C.UnavailableWorlds.Remove(worldId);
                        EzConfig.Save();
                    }
                    ImGuiEx.Tooltip("Remove".Loc());
                    ImGui.SameLine();
                    var worldName = ExcelWorldHelper.GetName(worldId);
                    if(worldName.IsNullOrEmpty())
                    {
                        // 查不到名字要在列上看得見是「不知道」，不能默默只印 id。
                        ImGuiEx.Text(ImGuiColors.DalamudGrey, $"? ({worldId})");
                        ImGuiEx.Tooltip("This world id is not present in the game's world table.".Loc());
                    }
                    else
                    {
                        ImGuiEx.Text(ImGuiColors.DalamudOrange, $"{worldName} ({worldId})");
                    }
                }
            }
            ImGui.SetNextItemWidth(200f.Scale());
            UnavailableWorldSelector.Draw(ref AddUnavailableWorld);
            ImGui.SameLine();
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add".Loc(), enabled: AddUnavailableWorld != 0 && !C.UnavailableWorlds.Contains((uint)AddUnavailableWorld)))
            {
                C.UnavailableWorlds.Add((uint)AddUnavailableWorld);
                AddUnavailableWorld = 0;
                EzConfig.Save();
            }
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
            var mounts = MountNames ??= BuildMountNames(Svc.Data.GetExcelSheet<Mount>(), "Mount roulette".Loc());
            ImGui.SetNextItemWidth(200f);
            ImGuiEx.Combo("Preferred Mount".Loc(), ref C.Mount, mounts.Keys, names: mounts);
        })
        .Checkbox("Dismount upon arriving at housing plot".Loc(), () => ref C.AutoDismount)
        .Checkbox("Use Sprint when auto-moving".Loc(), () => ref C.UseSprintPeloton)
        .Checkbox("Use Peloton when auto-moving".Loc(), () => ref C.UsePeloton)
        .Widget(() =>
        {
            // 把走 vnavmesh 的野外導航(自訂落點 / /li goto)接上既有的坐騎流程(C.UseMount 那一套)。
            // 📌 預設開(使用者裁決)：不上坐騎的舊行為只是實作限制，不是想要的行為。
            ImGui.Checkbox("Mount up for outdoor navigation".Loc(), ref C.GotoUseMount);
            ImGuiEx.HelpMarker("Applies to custom landing positions and \"/li goto\". Uses the same mount settings as above. Whether mounting is possible at all is left to the game - in a city, indoors, in combat or in a duty it simply walks instead.".Loc());
            if(C.GotoUseMount)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderFloat("Minimum path length to mount, yalms".Loc(), ref C.GotoMountMinDistance, 10f, 300f);
                ImGuiEx.HelpMarker("Shorter paths are walked - getting on and off a mount costs more time than it saves. This is the length of the actual path, not the straight-line distance.".Loc());
                ImGui.Unindent();
            }
        })
        .Widget(() =>
        {
            ImGui.Checkbox("Fly for outdoor navigation where flying is allowed".Loc(), ref C.GotoAutoFly);
            ImGuiEx.HelpMarker("Applies to custom landing positions and \"/li goto\". In a field zone where you can already fly (aether currents complete), mounts up, takes off and lets vnavmesh fly you there. Independent of the mount option above. If taking off fails or vnavmesh finds no flight path, it walks instead.".Loc());
            if(C.GotoAutoFly)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderFloat("Minimum distance to fly, yalms".Loc(), ref C.GotoAutoFlyMinDistance, Tasks.Utility.FieldFlightDecision.DistanceFloor, 300f);
                ImGuiEx.HelpMarker("Destinations closer than this (straight-line, horizontal) are walked. Cannot go below 20.".Loc());
                ImGui.Unindent();
            }
        })
        .Widget(() =>
        {
            // 卡住偵測:動機與預設開的理由見 Config.MovementStuckRecovery 的說明。
            ImGui.Checkbox("Recover when movement gets stuck".Loc(), ref C.MovementStuckRecovery);
            ImGuiEx.HelpMarker("When auto-moving stops making progress for a few seconds, Lifestream jumps, and if that does not help it recalculates the path from where you are standing. It only ever runs when you are already stuck - without it the character keeps pushing against the obstacle until the 30 second timeout throws the whole trip away.".Loc());
            if(C.MovementStuckRecovery)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderInt("Maximum recovery attempts per path".Loc(), ref C.MovementStuckMaxRecoveries, 1, 20);
                ImGuiEx.HelpMarker("Jumping and recalculating are used alternately. Once these are used up the previous behaviour takes over, so terrain that genuinely cannot be passed still fails instead of looping forever.".Loc());
                ImGui.Unindent();
            }
        })

        .Section("Arrival Announcement".Loc())
        .Widget(() =>
        {
            // 單向通知，對方沒安裝時整條路徑都是 no-op(見 IPC/TataruPraiseIPC.cs)。
            // 📌 預設開：TataruPraise 自己的總開關預設是關的，所以這裡預設開不會讓任何人
            //    突然聽到聲音，只是「裝了就會用上」。
            ImGui.Checkbox("Ask Tataru to announce arrival".Loc(), ref C.TataruPraiseOnArrival);
            ImGuiEx.PluginAvailabilityIndicator([new("TataruPraise", "TataruPraise")]);
            ImGuiEx.HelpMarker("When a /li teleport or navigation chain runs all the way through and you have actually arrived, Lifestream asks TataruPraise to say one short line. It stays quiet when the chain is aborted or times out, and when auto-movement fails to reach the destination. Without TataruPraise installed this does nothing.".Loc());
        })

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
}
