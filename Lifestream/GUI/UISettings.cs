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
        NuiTools.ButtonTabs([[new("一般", () => Wrapper(DrawGeneral)), new("疊加介面", () => Wrapper(DrawOverlay))], [new("進階", () => Wrapper(DrawExpert)), new("服務帳號", () => Wrapper(UIServiceAccount.Draw)), new("旅行封鎖", TabTravelBan.Draw)]]);
    }

    private static void Wrapper(Action action)
    {
        ImGui.Dummy(new(5f));
        action();
    }

    private static void DrawGeneral()
    {
        new NuiBuilder()
        .Section("傳送設定")
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo($"傳送世界切換閘道", ref C.WorldChangeAetheryte, Lang.WorldChangeAetherytes);
            ImGuiEx.HelpMarker($"世界切換時要傳送到哪裡");
            ImGui.Checkbox($"世界/資料中心轉移後傳送到指定的以太之光目的地", ref C.WorldVisitTPToAethernet);
            if(C.WorldVisitTPToAethernet)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(250f.Scale());
                ImGui.InputText("以太之光目的地，格式與「/li」指令相同", ref C.WorldVisitTPTarget, 50);
                ImGui.Checkbox($"僅從指令傳送，不從疊加介面傳送", ref C.WorldVisitTPOnlyCmd);
                ImGui.Unindent();
            }
            ImGui.Checkbox($"將菲爾梅特地點加入至法斯特傳送點", ref C.Firmament);
            ImGui.Checkbox($"切換世界前自動離開非跨服小隊", ref C.LeavePartyBeforeWorldChange);
            ImGui.Checkbox($"在聊天欄顯示傳送目的地", ref C.DisplayChatTeleport);
            ImGui.Checkbox($"在彈出通知中顯示傳送目的地", ref C.DisplayPopupNotifications);
            ImGui.Checkbox("重試同世界失敗的世界轉移", ref C.RetryWorldVisit);
            ImGui.Indent();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt("重試間隔，秒##2", ref C.RetryWorldVisitInterval.ValidateRange(1, 120));
            ImGui.SameLine();
            ImGuiEx.Text("+ 最多");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt("秒##2", ref C.RetryWorldVisitIntervalDelta.ValidateRange(0, 120));
            ImGuiEx.HelpMarker("讓行為看起來不那麼像機器人");
            ImGui.Unindent();
            //ImGui.Checkbox("Use Return instead of Teleport when possible", ref C.UseReturn);
            //ImGuiEx.HelpMarker("This includes any IPC calls");
            ImGui.Checkbox("旅行完成後啟用系統匣通知", ref C.EnableNotifications);
            ImGuiEx.PluginAvailabilityIndicator([new("NotificationMaster")]);
        })

        .Section("快捷指令")
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo("「/li」指令行為", ref C.LiCommandBehavior);
            ImGui.Checkbox("傳送至自己的公寓時，進入室內", ref C.EnterMyApartment);
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.EnumCombo("傳送至自己/部隊宅邸時，執行此動作", ref C.HouseEnterMode);
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("偏好旅館", Utils.GetInnNameFromTerritory(C.PreferredInn), ImGuiComboFlags.HeightLarge))
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
                    (0, 0, 0) => "第一個可用",
                    (-1, 0, 0) => "停用",
                    _ => $"{ExcelTerritoryHelper.GetName((uint)pref.Territory)}, W{pref.Ward}, P{pref.Plot}"
                };
                if(ImGui.BeginCombo($"{Player.NameWithWorld} 的偏好共用宅邸", name))
                {
                    foreach(var x in Svc.AetheryteList.Where(x => x.IsSharedHouse))
                    {
                        if(ImGui.RadioButton("第一個可用", pref == default))
                        {
                            C.PreferredSharedEstates.Remove(Player.CID);
                        }
                        if(ImGui.RadioButton("停用", pref == (-1, 0, 0)))
                        {
                            C.PreferredSharedEstates[Player.CID] = (-1, 0, 0);
                        }
                        if(ImGui.RadioButton($"{ExcelTerritoryHelper.GetName(x.TerritoryId)}, 房區 {x.Ward}, 房號 {x.Plot}", pref == ((int)x.TerritoryId, x.Ward, x.Plot)))
                        {
                            C.PreferredSharedEstates[Player.CID] = ((int)x.TerritoryId, x.Ward, x.Plot);
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.Separator();
            ImGuiEx.Text("「/li auto」指令優先順序：");
            ImGui.SameLine();
            if(ImGui.SmallButton("重置")) C.PropertyPrio.Clear();
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

        .Section("地圖整合")
        .Widget(() =>
        {
            ImGui.Checkbox("點擊地圖上的以太之光標記以快速傳送", ref C.UseMapTeleport);
            ImGui.Checkbox("僅在同一地圖內靠近傳送點時處理", ref C.DisableMapClickOtherTerritory);
        })

        .Section("指令自動完成")
        .Widget(() =>
        {
            ImGuiEx.Text($"在聊天欄輸入 Lifestream 指令時提示自動完成");
            ImGui.Checkbox("啟用", ref C.EnableAutoCompletion);
            ImGui.Checkbox("在固定位置顯示彈出視窗", ref C.AutoCompletionFixedWindow);
            ImGui.Indent();
            ImGui.SetNextItemWidth(200f.Scale());
            ImGui.DragFloat2("位置", ref C.AutoCompletionWindowOffset, 1f);
            ImGuiEx.RadioButtonBool("由下往上", "由上往下", ref C.AutoCompletionWindowBottom, sameLine: true, inverted: true);
            ImGuiEx.RadioButtonBool("由右往左", "由左往右", ref C.AutoCompletionWindowRight, sameLine: true, inverted: true);
            ImGui.Unindent();
        })

        .Section("跨資料中心")
        .Widget(() =>
        {
            ImGui.Checkbox($"允許前往其他資料中心", ref C.AllowDcTransfer);
            ImGui.Checkbox($"切換資料中心前離開小隊", ref C.LeavePartyBeforeLogout);
            ImGui.Checkbox($"若不在庇護所，切換資料中心前傳送至閘道傳送點", ref C.TeleportToGatewayBeforeLogout);
            ImGui.Checkbox($"完成資料中心旅行後傳送至閘道傳送點", ref C.DCReturnToGateway);
            ImGui.Checkbox($"資料中心轉移時允許替代世界", ref C.DcvUseAlternativeWorld);
            ImGuiEx.HelpMarker("如果目標世界不可用，但目標資料中心上有其他世界可用，將改為選擇該世界。登入後將排入正常的世界轉移。");
            ImGui.Checkbox($"若目標世界不可用則重試資料中心轉移", ref C.EnableDvcRetry);
            ImGui.Indent();
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt("最大重試次數", ref C.MaxDcvRetries.ValidateRange(1, int.MaxValue));
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt("重試間隔，秒", ref C.DcvRetryInterval.ValidateRange(10, 1000));
            ImGui.Unindent();
        })

        .Section("通訊錄")
        .Widget(() =>
        {
            ImGui.Checkbox($"停用前往房號的路徑規劃", ref C.AddressNoPathing);
            ImGuiEx.HelpMarker($"將會把你留在最靠近該房區的傳送點");
            ImGui.Checkbox($"停用進入公寓", ref C.AddressApartmentNoEntry);
            ImGuiEx.HelpMarker($"將會把你留在進入確認對話框");
        })

        .Section("移動")
        .Checkbox("自動移動時使用坐騎", () => ref C.UseMount)
        .Widget(() =>
        {
            Dictionary<int, string> mounts = [new KeyValuePair<int, string>(0, "隨機坐騎"), .. Svc.Data.GetExcelSheet<Mount>().Where(x => x.Singular != "").ToDictionary(x => (int)x.RowId, x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Singular.GetText()))];
            ImGui.SetNextItemWidth(200f);
            ImGuiEx.Combo("偏好坐騎", ref C.Mount, mounts.Keys, names: mounts);
        })
        .Checkbox("自動移動時使用疾行", () => ref C.UseSprintPeloton)
        .Checkbox("自動移動時使用陸行鳥吟遊之歌", () => ref C.UsePeloton)

        .Section("角色選擇選單")
        .Checkbox("在角色選擇選單啟用資料中心與世界轉移", () => ref C.AllowDCTravelFromCharaSelect)
        .Checkbox("前往訪客資料中心的同一世界時，使用世界轉移取代資料中心轉移", () => ref C.UseGuestWorldTravel)

        .Section("Wotsit 整合")
        .Widget(() =>
        {
            var anyChanged = ImGui.Checkbox("啟用 Wotsit 整合以傳送至以太之光目的地", ref C.WotsitIntegrationEnabled);
            ImGuiEx.PluginAvailabilityIndicator([new("Dalamud.FindAnything", "Wotsit")]);

            if(C.WotsitIntegrationEnabled)
            {
                ImGui.Indent();
                if(ImGui.Checkbox("包含世界選擇視窗", ref C.WotsitIntegrationIncludes.WorldSelect))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含自動傳送至房產", ref C.WotsitIntegrationIncludes.PropertyAuto))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至私人宅邸", ref C.WotsitIntegrationIncludes.PropertyPrivate))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至部隊宅邸", ref C.WotsitIntegrationIncludes.PropertyFreeCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至公寓", ref C.WotsitIntegrationIncludes.PropertyApartment))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至旅館房間", ref C.WotsitIntegrationIncludes.PropertyInn))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至部隊", ref C.WotsitIntegrationIncludes.GrandCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至市場管理委員會", ref C.WotsitIntegrationIncludes.MarketBoard))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含傳送至無人島", ref C.WotsitIntegrationIncludes.IslandSanctuary))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含自動傳送至以太之光目的地", ref C.WotsitIntegrationIncludes.AetheryteAethernet))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含通訊錄項目", ref C.WotsitIntegrationIncludes.AddressBook))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("包含自訂別名", ref C.WotsitIntegrationIncludes.CustomAlias))
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
        .Section("疊加介面一般設定")
        .Widget(() =>
        {
            ImGui.Checkbox("啟用疊加介面", ref C.Enable);
            if(C.Enable)
            {
                ImGui.Indent();
                ImGui.Checkbox($"顯示以太之光選單", ref C.ShowAethernet);
                ImGui.Checkbox($"顯示世界轉移選單", ref C.ShowWorldVisit);
                ImGui.Checkbox($"顯示房區按鈕", ref C.ShowWards);

                UtilsUI.NextSection();

                ImGui.Checkbox("固定 Lifestream 疊加介面位置", ref C.FixedPosition);
                if(C.FixedPosition)
                {
                    ImGui.Indent();
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("水平基準位置", ref C.PosHorizontal);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("垂直基準位置", ref C.PosVertical);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGui.DragFloat2("偏移", ref C.Offset);

                    ImGui.Unindent();
                }

                UtilsUI.NextSection();

                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt3("按鈕左右邊距", ref C.ButtonWidthArray[0]);
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("傳送點按鈕上下邊距", ref C.ButtonHeightAetheryte);
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("世界按鈕上下邊距", ref C.ButtonHeightWorld);
                ImGui.Unindent();

                ImGui.Checkbox("按鈕文字靠左對齊", ref C.LeftAlignButtons);
                if(C.LeftAlignButtons)
                {
                    ImGui.SetNextItemWidth(100f);
                    ImGui.DragInt("左邊距，空格數", ref C.LeftAlignPadding, 0.1f, 0, 20);
                }
            }
        })

        .Section("實例切換器")
        .Checkbox("啟用", () => ref C.ShowInstanceSwitcher)
        .Checkbox("失敗時重試", () => ref C.InstanceSwitcherRepeat)
        .Checkbox("切換副本前先降落到地面", () => ref C.EnableFlydownInstance)
        .Widget("在伺服器資訊列顯示副本編號", (x) =>
        {
            if(ImGui.Checkbox(x, ref C.EnableDtrBar))
            {
                S.DtrManager.Refresh();
            }
        })
        .SliderInt(150f, "額外按鈕高度", () => ref C.InstanceButtonHeight, 0, 50)
        .Widget("重置副本資料", (x) =>
        {
            if(ImGuiEx.Button(x, C.PublicInstances.Count > 0))
            {
                C.PublicInstances.Clear();
                EzConfig.Save();
            }
        })

        .Section("遊戲視窗整合")
        .Checkbox($"當以下遊戲視窗開啟時隱藏 Lifestream", () => ref C.HideAddon)
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
                ImGui.InputTextWithHint("##addnew", "視窗名稱... /xldata ai - 以查詢", ref AddNew, 100);
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
                        ImGuiEx.TextV(EColor.Green, $"目前焦點: {name}");
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
            .Section("已隱藏的傳送點")
            .Widget(() =>
            {
                uint toRem = 0;
                foreach(var x in C.Hidden)
                {
                    ImGuiEx.Text($"{Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(x)?.AethernetName.ValueNullable?.Name.ToString() ?? x.ToString()}");
                    ImGui.SameLine();
                    if(ImGui.SmallButton($"刪除##{x}"))
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
        .Section("進階設定")
        .Widget(() =>
        {
            ImGui.Checkbox($"放慢傳送點傳送速度", ref C.SlowTeleport);
            ImGuiEx.HelpMarker($"以指定的時間放慢以太之光傳送速度。");
            if(C.SlowTeleport)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.DragInt("傳送延遲（毫秒）", ref C.SlowTeleportThrottle);
                ImGui.Unindent();
            }
            ImGuiEx.CheckboxInverted($"跳過等待遊戲畫面就緒", ref C.WaitForScreenReady);
            ImGuiEx.HelpMarker($"啟用此選項可加快傳送速度，但要小心可能會卡住。");
            ImGui.Checkbox($"隱藏進度條", ref C.NoProgressBar);
            ImGuiEx.HelpMarker($"隱藏進度條後，你將無法中止 Lifestream 正在執行的任務。");
            ImGuiEx.CheckboxInverted($"從較遠距離執行世界切換指令時不要走到附近的傳送點", ref C.WalkToAetheryte);
            ImGui.Checkbox($"進度疊加顯示於螢幕頂部", ref C.ProgressOverlayToTop);
            ImGui.Checkbox("允許自訂別名與宅邸別名覆蓋內建指令", ref C.AllowCustomOverrides);
            ImGui.Indent();
            ImGuiEx.TextWrapped(EColor.RedBright, "警告！其他外掛可能依賴內建指令。若你決定啟用此選項並覆蓋指令，請先確認不會受到影響。");
            ImGui.Unindent();
        })
        .Draw();
    }
}
