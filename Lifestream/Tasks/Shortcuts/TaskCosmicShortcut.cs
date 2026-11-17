using ECommons.Automation;
using Callback = ECommons.Automation.Callback;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.Automation.UIInput;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.IPC;
using Lifestream.Schedulers;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Tasks.Shortcuts;

/// <summary>
/// 宇宙探索的直達：<c>/li cosmic</c>(別名 <c>ardorum</c>／<c>moon</c>)前往渴望灣(1237)、<c>/li phaenna</c> 前往行星法恩娜(1291)。
///
/// <para>兩條路線都從「傳送到嘆息海『最佳威兔洞』乙太之光(175)」開始,差別只在後半段:</para>
/// <list type="bullet">
/// <item><b>快版(這裡)</b>:互動乙太之光 → 選「前往渴望灣」／「前往行星法恩娜」→ 後續視窗(副本區、目的地選擇、確認)。</item>
/// <item><b>入口物件路線</b>:<see cref="StaticAlias.CosmicExploration"/> 的騎馬 → 走到入口物件(EObj 1052581)那幾步,
///   之後由這裡選入口選單(舊的「前往渴望灣」或 7.3 的「移動到探索區域」)並處理後續視窗。</item>
/// </list>
///
/// <para>⚠️ 快版**不是** <see cref="TaskSinusArdorumTeleport.Enqueue"/>。那一個是浮動視窗按鈕與
/// <c>/li 渴望灣</c> 用的,它從 <see cref="WorldChange.TargetValidAetheryte"/> 起手、**完全沒有傳送步驟**。
/// 這裡每一步都**逾時不中止**,好讓失敗時佇列還能走到退路(預設的 <c>AbortOnTimeout=true</c> 會連退路一起清掉)。</para>
///
/// <para>退路有兩個觸發點:連玄關乙太之光都沒站到(立刻退);站到了、但最後人不在目的地(走完快版才退)。
/// 例外:乙太之光選單有渴望灣卻沒有法恩娜＝尚未解鎖,直接停下並提示,不走退路。</para>
///
/// <para>選單與視窗一律依遊戲表的文字辨識、不按位置;認不出來就不按。單一佇列、入口有 <see cref="Utils.IsBusy"/> 擋重入,
/// 所以狀態用靜態欄位,每次 <see cref="Enqueue(Destination)"/> 重設。只在框架執行緒使用。</para>
/// </summary>
public static unsafe class TaskCosmicShortcut
{
    /// <summary>可以直達的探索區域。</summary>
    public sealed class Destination
    {
        /// <summary>log 用的英文名。</summary>
        public readonly string Name;
        public readonly uint Territory;
        internal readonly bool IsSinusArdorum;
        private string planetName;

        private Destination(string name, uint territory, bool isSinusArdorum)
        {
            Name = name;
            Territory = territory;
            IsSinusArdorum = isSinusArdorum;
        }

        public static readonly Destination SinusArdorum = new("Sinus Ardorum", TaskAetheryteAethernetTeleport.SinusArdorumTerritoryId, true);
        public static readonly Destination Phaenna = new("Phaenna", TaskAetheryteAethernetTeleport.PhaennaTerritoryId, false);

        /// <summary>遊戲裡的地名(TerritoryType → PlaceName),讀不到就用英文名。目的地選擇視窗也是拿它比對。</summary>
        public string PlanetName
        {
            get
            {
                if(planetName != null) return planetName;
                string name = null;
                try
                {
                    name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(Territory)?.PlaceName.ValueNullable?.Name.ExtractText()?.Trim();
                }
                catch(Exception e)
                {
                    e.Log();
                }
                planetName = string.IsNullOrEmpty(name) ? Name : name;
                return planetName;
            }
        }

        // 渴望灣沿用原本的字串陣列比對(行為不變);法恩娜只認遊戲表,不寫死中文。
        internal int FindAetheryteEntry(List<string> entries)
            => IsSinusArdorum ? entries.FindIndex(x => x.EqualsAny(Lang.TravelToSinusArdorum)) : Lang.TravelToPhaenna.FindIndex(entries, LegacyMatch.EqualsAny);

        internal bool TrySelectAetheryteEntry()
            => IsSinusArdorum
            ? Utils.TrySelectSpecificEntry(Lang.TravelToSinusArdorum, () => EzThrottler.Throttle("SelectString"))
            : Utils.TrySelectSpecificEntry(Lang.TravelToPhaenna, () => EzThrottler.Throttle("SelectString"));

        internal string DescribeAetheryteEntry() => IsSinusArdorum ? Lang.TravelToSinusArdorum.Print(" | ") : Lang.TravelToPhaenna.Describe();
    }

    private enum Outcome { Pending, NotUnlocked, HandedOver }

    private const uint EntranceObjectId = 1052581;

    /// <summary>乙太之光選單有渴望灣、沒有法恩娜的狀態要持續這麼久才判定尚未解鎖(選單剛開那幾幀可能還沒填完)。</summary>
    private const long NotUnlockedSettleMs = 1500;

    // WKSPlanetSelect 的 ULD 節點 ID(台服 7.3 的 .uld 解出):17＝目前目的地名稱、14／13＝移動到空閒副本區／移動、8／9＝切換鈕。
    private const uint PlanetLabelNodeId = 17;
    private static readonly (uint Node, uint AddonRow)[] PlanetMoveButtons = [(14, 17008), (13, 17007)];
    private static readonly uint[] PlanetSwitchButtons = [8, 9];
    private const int MaxSwitchPresses = 4;
    private const long SwitchSettleMs = 800;
    private const long PlanetGiveUpMs = 8000;

    private static TaskManagerConfiguration Lenient(int ms) => new(timeLimitMS: ms, abortOnTimeout: false);

    private static Destination Target = Destination.SinusArdorum;
    /// <summary>目的地那一項到底有沒有被選到;沒選到就不會有轉場,後面的等待直接短路。</summary>
    private static bool SelectedEntry;
    private static Outcome Result;
    private static string LastMenuLog;
    private static long MenuMismatchSince;
    private static nint PlanetAddon;
    private static string PlanetLastLabel;
    private static long PlanetSeenAt;
    private static long PlanetLastSwitchAt;
    private static int PlanetSwitchPresses;
    private static bool PlanetMovePressed;
    private static bool LoggedForeignYesno;
    private static bool LoggedYesAlreadyActive;

    /// <summary>
    /// <c>/li cosmic</c>、<c>/li phaenna</c> 與傳送面板的直達按鈕共用:不在原始世界就先回去,再走 <see cref="Enqueue(Destination)"/>。
    /// 呼叫端自己先判 <see cref="Utils.IsBusy"/>。
    /// </summary>
    public static void EnqueueReturnHomeFirst() => EnqueueReturnHomeFirst(Destination.SinusArdorum);

    /// <inheritdoc cref="EnqueueReturnHomeFirst()"/>
    public static void EnqueueReturnHomeFirst(Destination destination)
    {
        if(!Player.IsInHomeWorld)
        {
            P.TPAndChangeWorld(Player.HomeWorld, !Player.IsInHomeDC, null, true, null, false, false);
        }
        P.TaskManager.Enqueue(() => Player.Interactable && Player.IsInHomeWorld && IsScreenReady());
        Enqueue(destination);
    }

    public static void Enqueue() => Enqueue(Destination.SinusArdorum);

    public static void Enqueue(Destination destination)
    {
        Target = destination ?? Destination.SinusArdorum;
        SelectedEntry = false;
        Result = Outcome.Pending;
        LastMenuLog = null;
        MenuMismatchSince = 0;
        ResetPlanetSelect();
        PluginLog.Information($"[Cosmic] Destination {Target.Name} \"{Target.PlanetName}\" (territory {Target.Territory}). Taking the aetheryte-menu route: teleport to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} -> interact -> select [{Target.DescribeAetheryteEntry()}].");
        TaskRemoveAfkStatus.Enqueue();

        // 1) 需要時才傳送到玄關乙太之光(人已經站在它旁邊就跳過)。判定條件與 TaskAetheryteAethernetTeleport 相同。
        P.TaskManager.Enqueue(() =>
        {
            if(!IsAtGatewayAetheryte())
            {
                PluginLog.Information($"[Cosmic] Not standing next to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} (currently in territory {P.Territory}), teleporting there first.");
                P.TaskManager.InsertMulti(
                    new(() => S.TeleportService.TeleportToAetheryte(TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId), "CosmicTeleportToGatewayAetheryte"),
                    new(Utils.WaitForScreenFalse, "CosmicWaitTeleportStart", Lenient(30000)),
                    new(Utils.WaitForScreen, "CosmicWaitTeleportEnd", Lenient(60000))
                    );
            }
            else
            {
                PluginLog.Information($"[Cosmic] Already next to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId}, skipping the teleport.");
            }
        }, "CosmicConditionalTeleportToGatewayAetheryte");

        // 2) 讀取畫面結束與乙太之光變成可鎖定之間有幾幀落差,只查一次會把成功誤判成失敗。逾時就是下一步要處理的失敗訊號。
        P.TaskManager.Enqueue(() => Player.Interactable && IsAtGatewayAetheryte(), "CosmicWaitAtGatewayAetheryte", Lenient(10000));

        // 3) 傳送成功才排後面的步驟;沒成功就直接退路,不讓每一步空等到逾時。
        P.TaskManager.Enqueue(RouteDecision, "CosmicRouteDecision");
    }

    /// <summary>玄關乙太之光(175)就在身邊、摸得到嗎?判定與 <see cref="TaskAetheryteAethernetTeleport"/> 相同。</summary>
    private static bool IsAtGatewayAetheryte()
        => Svc.ClientState.TerritoryType == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteTerritoryId
        && Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.HasValue && ae.Value.ID == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId) != null;

    private static bool? RouteDecision()
    {
        if(!IsAtGatewayAetheryte())
        {
            PluginLog.Information($"[Cosmic] Could not get to the gateway aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} (currently in territory {P.Territory}) - the aetheryte-menu route cannot even start.");
            FallBackToEntranceObjectRoute();
            return true;
        }

        // 執行途中才確定要走的步驟一律 InsertStack:Enqueue 會排到佇列尾端,將來有人在後面加步驟就會靜默錯序。
        P.TaskManager.InsertStack(() =>
        {
            P.TaskManager.EnqueueDelay(10, true);
            P.TaskManager.Enqueue(WorldChange.TargetReachableMasterAetheryte, "CosmicTargetAetheryte", Lenient(15000));
            P.TaskManager.Enqueue(ConditionalWalkToAetheryte, "CosmicConditionalLockon");
            P.TaskManager.Enqueue(WorldChange.InteractWithTargetedAetheryte, "CosmicInteractAetheryte", Lenient(15000));
            P.TaskManager.Enqueue(SelectTravelTo, "CosmicSelectTravelTo", Lenient(20000));
            P.TaskManager.Enqueue(FollowUp, "CosmicFollowUpAndWaitArrival", Lenient(70000));
            P.TaskManager.Enqueue(VerifyArrivalOrFallBack, "CosmicVerifyArrivalOrFallBack");
        });
        return true;
    }

    /// <summary>摸得到但還沒站進互動距離時,鎖定 + 自動移動走過去。</summary>
    private static bool? ConditionalWalkToAetheryte()
    {
        if(P.ActiveAetheryte == null)
        {
            PluginLog.Information("[Cosmic] Aetheryte is targetable but out of interaction range, walking to it.");
            P.TaskManager.InsertMulti(
                new(WorldChange.LockOn, "CosmicLockOn"),
                new(WorldChange.EnableAutomove, "CosmicEnableAutomove"),
                new(WorldChange.WaitUntilMasterAetheryteExists, "CosmicWalkToAetheryte", Lenient(20000)),
                new(WorldChange.DisableAutomove, "CosmicDisableAutomove"),
                new FrameDelayTask(10)
                );
        }
        return true;
    }

    /// <summary>
    /// 在乙太之光選單選目的地那一項。⚠️ 選單剛開的那一兩幀 EntryCount 會是 0,那時不能當成「沒有這一項」。
    /// 法恩娜:選單有渴望灣、沒有法恩娜並持續 <see cref="NotUnlockedSettleMs"/> ＝ 尚未解鎖,關掉選單並停下。
    /// </summary>
    private static bool? SelectTravelTo()
    {
        if(!Player.Available) return false;
        if(!TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !IsAddonReady(&addon->AtkUnitBase))
        {
            MenuMismatchSince = 0;
            if(EzThrottler.Throttle("CosmicMenuWaitLog", 5000)) PluginLog.Information("[Cosmic] Waiting for the aetheryte menu to open.");
            return false;
        }
        var entries = Utils.GetEntries(addon);
        if(entries.Count == 0 || AddonPressGuard.AnyTextUnstable("SelectString", entries)) return false;
        LogMenuOnce("Aetheryte menu", entries);

        var index = Target.FindAetheryteEntry(entries);
        if(index < 0)
        {
            if(EzThrottler.Throttle("CosmicMenuMismatchLog", 5000))
            {
                PluginLog.Information($"[Cosmic] The aetheryte menu is open but has no {Target.Name} entry. Looked for [{Target.DescribeAetheryteEntry()}], menu shows [{entries.Print(" | ")}].");
            }
            if(!Target.IsSinusArdorum && entries.Any(x => x.EqualsAny(Lang.TravelToSinusArdorum)))
            {
                var now = Environment.TickCount64;
                if(MenuMismatchSince == 0) MenuMismatchSince = now;
                else if(now - MenuMismatchSince >= NotUnlockedSettleMs)
                {
                    Result = Outcome.NotUnlocked;
                    PluginLog.Information($"[Cosmic] The aetheryte menu lists Sinus Ardorum but not {Target.Name} [{entries.Print(" | ")}]: treating {Target.Name} as not unlocked yet, closing the menu and stopping (no fallback).");
                    ChatQueue.Orange($"[Lifestream] {"The aetheryte menu has no option for ??, so you have probably not unlocked it yet. Lifestream has stopped.".Loc(Target.PlanetName)}");
                    CloseLeftoverSelectString();
                    return true;
                }
            }
            return false;
        }
        MenuMismatchSince = 0;
        if(!Target.TrySelectAetheryteEntry()) return false;
        SelectedEntry = true;
        PluginLog.Information($"[Cosmic] Aetheryte menu: selected entry #{index} \"{entries[index]}\" for {Target.Name}.");
        return true;
    }

    /// <summary>
    /// 選完目的地後的視窗,一直處理到抵達:副本區清單選「隨意」、7.3 目的地選擇視窗(WKSPlanetSelect)、移動確認框。
    /// 只按文字認得出來的;交給使用者或判定未解鎖後就收手。
    /// </summary>
    private static bool? FollowUp()
    {
        if(!SelectedEntry || Result != Outcome.Pending) return true;
        if(IsArrived()) return true;
        if(!Player.Available || !IsScreenReady()) return false;
        if(SelectAnyInstance()) return false;
        if(HandlePlanetSelect()) return false;
        if(Result != Outcome.Pending) return true;
        ConfirmMove();
        return false;
    }

    private static bool IsArrived()
        => P.Territory == Target.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas];

    private static bool SelectAnyInstance()
    {
        if(!Utils.TrySelectSpecificEntry(Lang.AnyInstance, () => EzThrottler.Throttle("SelectString"))) return false;
        PluginLog.Information($"[Cosmic] Instance menu: selected \"{Lang.AnyInstance.FirstOrDefault()}\".");
        return true;
    }

    /// <summary>
    /// 7.3 的「選擇目的地」視窗:名稱(節點 17)是目標就按「移動到空閒副本區」(沒有就「移動」);不是就按切換鈕再看,
    /// 最多 <see cref="MaxSwitchPresses"/> 次。認不出名稱、找不到能按的鈕、或切換完都不是目標 ⇒ 交給使用者。
    /// </summary>
    /// <returns>這一幀按了東西。</returns>
    private static bool HandlePlanetSelect()
    {
        if(!TryGetAddonByName<AtkUnitBase>("WKSPlanetSelect", out var addon) || !IsAddonReady(addon)) return false;
        var now = Environment.TickCount64;
        if((nint)addon != PlanetAddon)
        {
            ResetPlanetSelect();
            PlanetAddon = (nint)addon;
            PlanetSeenAt = now;
            PluginLog.Information($"[Cosmic] Destination-select window (WKSPlanetSelect) is open, looking for \"{Target.PlanetName}\". Texts: {DescribeTexts(addon)}");
        }
        if(PlanetMovePressed) return false;

        if(!TryGetNodeText(FindNode(addon, PlanetLabelNodeId), out var label) || string.IsNullOrWhiteSpace(label))
        {
            if(now - PlanetSeenAt > PlanetGiveUpMs) HandOver($"could not read the destination name (node {PlanetLabelNodeId})");
            return false;
        }
        if(label != PlanetLastLabel)
        {
            PlanetLastLabel = label;
            PluginLog.Information($"[Cosmic] Destination-select window shows \"{label}\" (target \"{Target.PlanetName}\").");
        }
        if(now - PlanetLastSwitchAt < SwitchSettleMs) return false;

        if(IsTargetPlanet(label))
        {
            foreach(var (node, addonRow) in PlanetMoveButtons)
            {
                var button = addon->GetComponentButtonById(node);
                if(!IsUsable(button)) continue;
                if(!AddonPressGuard.TryPressOnce("WKSPlanetSelect", addon, "TaskCosmicShortcut.PlanetMove")) return false;
                button->ClickAddonButton(addon);
                PlanetMovePressed = true;
                PluginLog.Information($"[Cosmic] Destination-select window: \"{label}\" is the target, pressed button node {node} (\"{AddonText(addonRow)}\").");
                return true;
            }
            if(now - PlanetSeenAt > PlanetGiveUpMs) HandOver($"\"{label}\" is shown but none of its move buttons can be pressed (it may not be unlocked yet)");
            return false;
        }

        if(PlanetSwitchPresses >= MaxSwitchPresses)
        {
            HandOver($"still showing \"{label}\" after {PlanetSwitchPresses} presses of the switch button (it may not be unlocked yet)");
            return false;
        }
        foreach(var node in PlanetSwitchButtons)
        {
            var button = addon->GetComponentButtonById(node);
            if(!IsUsable(button)) continue;
            if(!AddonPressGuard.TryPressOnce("WKSPlanetSelect", addon, "TaskCosmicShortcut.PlanetSwitch", paramKey: $"switch{PlanetSwitchPresses}", escapeIsRoutine: true)) return false;
            button->ClickAddonButton(addon);
            PlanetSwitchPresses++;
            PlanetLastSwitchAt = now;
            PluginLog.Information($"[Cosmic] Destination-select window: \"{label}\" is not the target, pressed switch button node {node} ({PlanetSwitchPresses}/{MaxSwitchPresses}).");
            return true;
        }
        HandOver($"\"{label}\" is not the target and no switch button can be pressed");
        return false;
    }

    /// <summary>移動確認框:文字與 Addon 17015／17016 相同才按「是」;YesAlready 沒讓位就不按。</summary>
    private static void ConfirmMove()
    {
        if(!TryGetAddonMaster<AddonMaster.SelectYesno>(out var m) || !m.IsAddonReady) return;
        var text = m.Text;
        if(AddonPressGuard.IsTextUnstable("SelectYesno", text)) return;
        var flat = text.ReplaceLineEndings(" ");
        if(!Lang.WksConfirmMove.Matches(text, LegacyMatch.EqualsAny))
        {
            if(!LoggedForeignYesno)
            {
                LoggedForeignYesno = true;
                PluginLog.Information($"[Cosmic] A confirmation box is open that is not the destination move: \"{flat}\" - not pressing it. Expected [{Lang.WksConfirmMove.Describe()}].");
            }
            return;
        }
        if(YesAlreadyManager.IsEnabled())
        {
            if(!LoggedYesAlreadyActive)
            {
                LoggedYesAlreadyActive = true;
                PluginLog.Information("[Cosmic] YesAlready has not yielded yet, not pressing the move confirmation this frame.");
            }
            return;
        }
        if(!AddonPressGuard.TryPressOnce("SelectYesno", m, "TaskCosmicShortcut.ConfirmMove")) return;
        m.Yes();
        PluginLog.Information($"[Cosmic] Confirmed \"{flat}\".");
    }

    private static void HandOver(string reason)
    {
        Result = Outcome.HandedOver;
        PluginLog.Information($"[Cosmic] Destination-select window: {reason}. Lifestream stops here and leaves the window to you.");
        ChatQueue.Orange($"[Lifestream] {"Could not pick ?? in the destination window, it may not be unlocked yet. Choose it yourself or close the window; Lifestream has stopped.".Loc(Target.PlanetName)}");
    }

    private static void ResetPlanetSelect()
    {
        PlanetAddon = 0;
        PlanetLastLabel = null;
        PlanetSeenAt = 0;
        PlanetLastSwitchAt = 0;
        PlanetSwitchPresses = 0;
        PlanetMovePressed = false;
        LoggedForeignYesno = false;
        LoggedYesAlreadyActive = false;
    }

    private static bool IsTargetPlanet(string label)
    {
        var target = GameTextPattern.Normalize(Target.PlanetName);
        return target.Length > 0 && GameTextPattern.Normalize(label).Contains(target, StringComparison.Ordinal);
    }

    private static bool IsUsable(AtkComponentButton* button)
        => IsButtonEnabled(button) && button->AtkComponentBase.OwnerNode->AtkResNode.NodeFlags.HasFlag(NodeFlags.Visible);

    /// <summary>依節點 ID 在根節點清單裡找(純讀 NodeList,不呼叫原生查找)。</summary>
    private static AtkResNode* FindNode(AtkUnitBase* addon, uint nodeId)
    {
        for(var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = GetNodeSafe(&addon->UldManager, i);
            if(node != null && node->NodeId == nodeId) return node;
        }
        return null;
    }

    private static string DescribeTexts(AtkUnitBase* addon)
    {
        var parts = new List<string>();
        for(var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = GetNodeSafe(&addon->UldManager, i);
            if(node == null || node->Type != NodeType.Text) continue;
            if(TryGetNodeText(node, out var text) && !string.IsNullOrWhiteSpace(text)) parts.Add($"#{node->NodeId}=\"{text.ReplaceLineEndings(" ")}\"");
        }
        return parts.Count == 0 ? "(none)" : string.Join(" ", parts);
    }

    private static string AddonText(uint row)
    {
        try
        {
            return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(row)?.Text.ExtractText() ?? $"Addon#{row}";
        }
        catch(Exception e)
        {
            e.Log();
            return $"Addon#{row}";
        }
    }

    private static void LogMenuOnce(string kind, List<string> entries)
    {
        var line = $"{kind} shows [{entries.Print(" | ")}]";
        if(line == LastMenuLog) return;
        LastMenuLog = line;
        PluginLog.Information($"[Cosmic] {line}.");
    }

    /// <summary>快版的收尾:到了就結束;判定未解鎖或已交給使用者就停;其餘走入口物件路線。</summary>
    private static bool? VerifyArrivalOrFallBack()
    {
        if(P.Territory == Target.Territory)
        {
            PluginLog.Information($"[Cosmic] Arrived at {Target.Name} through the aetheryte menu.");
            return true;
        }
        if(Result != Outcome.Pending)
        {
            PluginLog.Information($"[Cosmic] Stopped without reaching {Target.Name} ({Result}); not falling back.");
            return true;
        }

        PluginLog.Information($"[Cosmic] The aetheryte-menu route did not get us to {Target.Name} ({DescribeFailure()}).");
        FallBackToEntranceObjectRoute();
        return true;
    }

    /// <summary>
    /// 入口物件路線:沿用 <see cref="StaticAlias.CosmicExploration"/> 走到入口物件並互動的那幾步,入口選單起改由這裡處理。
    /// 別名形狀對不上時,渴望灣退回整份別名(舊行為),法恩娜停下(那份別名的選單項是渴望灣專用)。
    /// </summary>
    private static void FallBackToEntranceObjectRoute()
    {
        PluginLog.Information($"[Cosmic] Falling back to the entrance-object route for {Target.Name}: teleport to the gateway aetheryte, mount up, ride to the entrance object and use its menu.");
        ChatQueue.Green($"[Lifestream] {(Target.IsSinusArdorum ? "The quick route to Sinus Ardorum did not work, falling back to the long one.".Loc() : "The quick route to ?? did not work, falling back to the long one.".Loc(Target.PlanetName))}");
        CloseLeftoverSelectString();

        var alias = StaticAlias.CosmicExploration;
        var menuStep = alias.Commands.FindIndex(c => c.Kind == CustomAliasKind.Select_List_Option);
        if(menuStep <= 0 || alias.Commands[menuStep - 1].Kind != CustomAliasKind.Interact || alias.Commands[menuStep - 1].DataID != EntranceObjectId)
        {
            PluginLog.Information($"[Cosmic] The built-in entrance route does not have the expected shape (menu step {menuStep}).");
            if(Target.IsSinusArdorum)
            {
                P.TaskManager.InsertStack(() => alias.Enqueue(true));
            }
            else
            {
                Result = Outcome.HandedOver;
                ChatQueue.Orange($"[Lifestream] {"Could not reach ??. Please continue manually.".Loc(Target.PlanetName)}");
            }
            return;
        }

        SelectedEntry = false;
        LastMenuLog = null;
        ResetPlanetSelect();
        P.TaskManager.InsertStack(() =>
        {
            alias.Enqueue(true, 0, menuStep);
            P.TaskManager.Enqueue(SelectEntranceMenu, "CosmicSelectEntranceMenu", Lenient(15000));
            P.TaskManager.Enqueue(FollowUp, "CosmicEntranceFollowUpAndWaitArrival", Lenient(70000));
            P.TaskManager.Enqueue(ReportEntranceRoute, "CosmicEntranceRouteResult");
        });
    }

    /// <summary>入口物件的選單:渴望灣先找舊的「前往渴望灣」,再找 7.3 的「移動到探索區域」;法恩娜只找後者。</summary>
    private static bool? SelectEntranceMenu()
    {
        if(!Player.Available) return false;
        if(!TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !IsAddonReady(&addon->AtkUnitBase))
        {
            if(EzThrottler.Throttle("CosmicEntranceMenuWaitLog", 5000)) PluginLog.Information("[Cosmic] Waiting for the entrance menu to open.");
            return false;
        }
        var entries = Utils.GetEntries(addon);
        if(entries.Count == 0 || AddonPressGuard.AnyTextUnstable("SelectString", entries)) return false;
        LogMenuOnce("Entrance menu", entries);

        GameTextMatcher matcher = null;
        var index = -1;
        if(Target.IsSinusArdorum)
        {
            index = Lang.WksEntranceTravelToSinusArdorum.FindIndex(entries, LegacyMatch.EqualsAny);
            if(index >= 0) matcher = Lang.WksEntranceTravelToSinusArdorum;
        }
        if(index < 0)
        {
            index = Lang.WksEntranceMoveToExplorationArea.FindIndex(entries, LegacyMatch.EqualsAny);
            if(index >= 0) matcher = Lang.WksEntranceMoveToExplorationArea;
        }
        if(matcher == null)
        {
            if(EzThrottler.Throttle("CosmicEntranceMenuMismatchLog", 5000))
            {
                var wanted = Target.IsSinusArdorum ? $"{Lang.WksEntranceTravelToSinusArdorum.Describe()} | {Lang.WksEntranceMoveToExplorationArea.Describe()}" : Lang.WksEntranceMoveToExplorationArea.Describe();
                PluginLog.Information($"[Cosmic] The entrance menu has none of [{wanted}], menu shows [{entries.Print(" | ")}].");
            }
            return false;
        }
        if(!Utils.TrySelectSpecificEntry(matcher, () => EzThrottler.Throttle("SelectString"))) return false;
        SelectedEntry = true;
        PluginLog.Information($"[Cosmic] Entrance menu: selected entry #{index} \"{entries[index]}\" for {Target.Name}.");
        return true;
    }

    private static bool? ReportEntranceRoute()
    {
        if(P.Territory == Target.Territory)
        {
            PluginLog.Information($"[Cosmic] Arrived at {Target.Name} through the entrance object.");
        }
        else if(Result != Outcome.Pending)
        {
            PluginLog.Information($"[Cosmic] Entrance-object route stopped without reaching {Target.Name} ({Result}).");
        }
        else
        {
            PluginLog.Information($"[Cosmic] The entrance-object route did not get us to {Target.Name} either ({DescribeFailure()}).");
            ChatQueue.Orange($"[Lifestream] {"Could not reach ??. Please continue manually.".Loc(Target.PlanetName)}");
        }
        return true;
    }

    private static string DescribeFailure()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            return $"still in territory {P.Territory}, selectedMenuEntry={SelectedEntry}, a SelectString is open showing [{Utils.GetEntries(addon).Print(" | ")}]";
        }
        var planetOpen = TryGetAddonByName<AtkUnitBase>("WKSPlanetSelect", out var planet) && IsAddonReady(planet);
        return $"still in territory {P.Territory}, selectedMenuEntry={SelectedEntry}, destinationWindowOpen={planetOpen}, lastDestinationShown=\"{PlanetLastLabel}\", ActiveAetheryte={(P.ActiveAetheryte == null ? "null" : $"{P.ActiveAetheryte.Value.Name}({P.ActiveAetheryte.Value.ID})")}";
    }

    /// <summary>選單開著時退路的騎馬/走位會被占用狀態擋住,先取消它。</summary>
    private static void CloseLeftoverSelectString()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            if(!AddonPressGuard.TryPressOnce("SelectString", &addon->AtkUnitBase, nameof(CloseLeftoverSelectString))) return;
            PluginLog.Information("[Cosmic] Cancelling the leftover menu.");
            Callback.Fire(&addon->AtkUnitBase, true, -1);
        }
    }
}
