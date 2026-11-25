using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using Callback = ECommons.Automation.Callback;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.Automation.UIInput;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.IPC;
using Lifestream.Schedulers;
using Lifestream.Services;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Utility;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

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
/// <para>⚠️ 快版**不是** <see cref="TaskSinusArdorumTeleport.Enqueue"/>(只會選「前往渴望灣」、沒有退路,已無路線使用)。
/// 所有渴望灣入口都走這裡;要接著做事的呼叫端經 <see cref="TaskAetheryteAethernetTeleport"/> 的玄關路線加抵達檢查。
/// 這裡每一步都**逾時不中止**,好讓失敗時佇列還能走到退路(預設的 <c>AbortOnTimeout=true</c> 會連退路一起清掉)。</para>
///
/// <para>退路觸發點:沒站到玄關乙太之光;要去渴望灣、選單卻只列法恩娜(解鎖後只列最新星球);走完快版人不在目的地。
/// 例外(停下、不走退路):選單有渴望灣卻沒有法恩娜＝尚未解鎖;還沒替使用者選任何項目就開始轉場＝使用者自己選了。</para>
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

    private enum Outcome { Pending, NotUnlocked, HandedOver, UserTookOver }

    private const uint EntranceObjectId = 1052581;

    /// <summary>乙太之光選單「有另一顆星球、沒有目標」的狀態要持續這麼久才下結論(選單剛開那幾幀可能還沒填完)。</summary>
    private const long NotUnlockedSettleMs = 1500;

    /// <summary>退路出發前等「選單／對話關掉、人能動」的上限;逾時就停下交給使用者。</summary>
    private const long ReadyWaitMs = 15000;

    /// <summary>退路的傳送被遊戲拒絕時,重試到這麼久為止。</summary>
    private const long TeleportRetryMs = 20000;

    /// <summary>內建座標騎完後，入口物件要連續這麼久都不在物件表裡才停下（剛停下那幾幀可能還沒列出來）。</summary>
    private const int EntranceMissingSettleMs = 3000;

    /// <summary>改用 vnavmesh 走到入口物件時，走路這一步的上限；逾時就照原本的方式試著互動。</summary>
    private const int EntranceNavWalkMs = 60000;

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
    /// <summary>乙太之光選單只列另一顆星球:不是失敗,是直接改走入口物件路線的理由。</summary>
    private static bool GatewayListsOtherPlanetOnly;
    private static uint MenuStageTerritory;
    /// <summary>要關掉的那扇選單的位址,只做等值比較、不解參。</summary>
    private static nint MenuToClose;
    /// <summary>自帶期限的等待步驟從何時開始等;0＝還沒開始。</summary>
    private static long StepSince;
    private static int EntranceAliasStart;
    private static int EntranceMenuStep;
    /// <summary>vnavmesh 沒給出路徑；收尾時不再重複記「還是摸不到」。</summary>
    private static bool EntranceNavNoPath;

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
        GatewayListsOtherPlanetOnly = false;
        MenuStageTerritory = 0;
        MenuToClose = 0;
        StepSince = 0;
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

        MenuStageTerritory = P.Territory;
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
    /// 沒有目標那一項的處置見 <see cref="CosmicRouteRules.ClassifyMissingEntry"/>,要持續 <see cref="NotUnlockedSettleMs"/> 才算數。
    /// </summary>
    private static bool? SelectTravelTo()
    {
        if(Result == Outcome.UserTookOver) return TransitionSettled();
        if(UserChoseDestination("Aetheryte menu", MenuStageTerritory)) return false;
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
            var verdict = CosmicRouteRules.ClassifyMissingEntry(Target.IsSinusArdorum,
                menuHasSinusArdorum: entries.Any(x => x.EqualsAny(Lang.TravelToSinusArdorum)),
                menuHasPhaenna: Lang.TravelToPhaenna.FindIndex(entries, LegacyMatch.EqualsAny) >= 0);
            if(verdict == CosmicRouteRules.MissingEntry.KeepWaiting) return false;
            var now = Environment.TickCount64;
            if(MenuMismatchSince == 0) MenuMismatchSince = now;
            if(now - MenuMismatchSince < NotUnlockedSettleMs) return false;
            if(verdict == CosmicRouteRules.MissingEntry.NotUnlocked)
            {
                Result = Outcome.NotUnlocked;
                PluginLog.Information($"[Cosmic] The aetheryte menu lists Sinus Ardorum but not {Target.Name} [{entries.Print(" | ")}]: treating {Target.Name} as not unlocked yet, closing the menu and stopping (no fallback).");
                ChatQueue.Orange($"[Lifestream] {"The aetheryte menu has no option for ??, so you have probably not unlocked it yet. Lifestream has stopped.".Loc(Target.PlanetName)}");
            }
            else
            {
                GatewayListsOtherPlanetOnly = true;
                PluginLog.Information($"[Cosmic] The aetheryte menu lists Phaenna but not {Target.Name} [{entries.Print(" | ")}]: once Phaenna is unlocked the gateway aetheryte only offers the newest planet. Closing the menu and taking the entrance-object route now instead of waiting for the timeout.");
            }
            CloseLeftoverSelectString();
            return true;
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

        if(!GatewayListsOtherPlanetOnly)
        {
            PluginLog.Information($"[Cosmic] The aetheryte-menu route did not get us to {Target.Name} ({DescribeFailure()}).");
        }
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
        if(GatewayListsOtherPlanetOnly)
        {
            ChatQueue.Green($"[Lifestream] {"The aetheryte menu no longer lists Sinus Ardorum, going through the entrance object instead.".Loc()}");
        }
        else
        {
            ChatQueue.Green($"[Lifestream] {(Target.IsSinusArdorum ? "The quick route to Sinus Ardorum did not work, falling back to the long one.".Loc() : "The quick route to ?? did not work, falling back to the long one.".Loc(Target.PlanetName))}");
        }
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
        StepSince = 0;
        ResetPlanetSelect();
        // 別名的第一步是「傳送到 175」就由這裡的可重試版本取代;別名那一步被拒約 3 秒就中止整條鏈。
        var first = alias.Commands[0];
        EntranceAliasStart = first.Kind == CustomAliasKind.Teleport_to_Aetheryte && first.Aetheryte == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId ? 1 : 0;
        EntranceMenuStep = menuStep;
        P.TaskManager.InsertStack(() =>
        {
            P.TaskManager.Enqueue(WaitReadyForEntranceRoute, "CosmicEntranceWaitReady", Lenient((int)ReadyWaitMs + 10000));
            P.TaskManager.Enqueue(TeleportToGatewayForEntranceRoute, "CosmicEntranceTeleport", Lenient((int)TeleportRetryMs + 10000));
            P.TaskManager.Enqueue(StartEntranceRide, "CosmicEntranceStart");
        });
    }

    /// <summary>退路出發前:等選單、確認框、對話都關掉、人能動;上一步留下的乙太之光選單由這裡關。逾時就停下交給使用者。</summary>
    private static bool? WaitReadyForEntranceRoute()
    {
        if(Result == Outcome.UserTookOver) return TransitionSettled();
        if(Result != Outcome.Pending) return true;
        if(UserChoseDestination("Before the entrance-object route", 0)) return false;
        var now = Environment.TickCount64;
        if(StepSince == 0) StepSince = now;
        var blocker = DescribeBlocker();
        if(blocker == null)
        {
            StepSince = 0;
            return true;
        }
        if(now - StepSince < ReadyWaitMs)
        {
            if(EzThrottler.Throttle("CosmicEntranceReadyLog", 5000)) PluginLog.Information($"[Cosmic] Entrance-object route: waiting before setting off ({blocker}).");
            return false;
        }
        StepSince = 0;
        Result = Outcome.HandedOver;
        PluginLog.Information($"[Cosmic] Entrance-object route: still not free to set off after {ReadyWaitMs / 1000} s ({blocker}); stopping here.");
        ChatQueue.Orange($"[Lifestream] {"A window or conversation is still open, so Lifestream cannot continue. Please continue manually.".Loc()}");
        return true;
    }

    /// <returns>擋住退路出發的原因;null＝可以出發。</returns>
    private static string DescribeBlocker()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var menu) && IsAddonReady(&menu->AtkUnitBase))
        {
            if((nint)menu == MenuToClose) CloseLeftoverSelectString();
            return "a menu is open";
        }
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && IsAddonReady(yesno)) return "a confirmation box is open";
        if(!Player.Interactable || !IsScreenReady()) return "the character cannot act yet";
        if(IsOccupied()) return "the character is occupied (conversation, event, zone change, ...)";
        return null;
    }

    /// <summary>退路的傳送:人已在 175 旁邊就跳過;遊戲暫時拒絕就重試到 <see cref="TeleportRetryMs"/>,不整條中止。</summary>
    private static bool? TeleportToGatewayForEntranceRoute()
    {
        if(Result == Outcome.UserTookOver) return TransitionSettled();
        if(Result != Outcome.Pending) return true;
        var aetheryte = TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId;
        if(IsAtGatewayAetheryte())
        {
            StepSince = 0;
            PluginLog.Information($"[Cosmic] Entrance-object route: already next to aetheryte {aetheryte}, no teleport needed.");
            return true;
        }
        if(UserChoseDestination("Before the entrance-object route", 0)) return false;
        var now = Environment.TickCount64;
        if(StepSince == 0) StepSince = now;
        if(TeleportService.CanTeleport(out var error) && S.TeleportService.TeleportToAetheryte(aetheryte, abortChainWhenRefused: false))
        {
            StepSince = 0;
            PluginLog.Information($"[Cosmic] Entrance-object route: teleporting to aetheryte {aetheryte} from territory {P.Territory}.");
            P.TaskManager.InsertMulti(
                new(Utils.WaitForScreenFalse, "CosmicEntranceWaitTeleportStart", Lenient(30000)),
                new(Utils.WaitForScreen, "CosmicEntranceWaitTeleportEnd", Lenient(60000)),
                new(() => Player.Interactable && IsAtGatewayAetheryte(), "CosmicEntranceWaitAtGateway", Lenient(10000))
                );
            return true;
        }
        if(now - StepSince < TeleportRetryMs)
        {
            if(EzThrottler.Throttle("CosmicEntranceTeleportLog", 5000)) PluginLog.Information($"[Cosmic] Entrance-object route: cannot teleport to aetheryte {aetheryte} yet ({error ?? "aetheryte not in the teleport list"}), retrying.");
            return false;
        }
        StepSince = 0;
        Result = Outcome.HandedOver;
        PluginLog.Information($"[Cosmic] Entrance-object route: teleport to aetheryte {aetheryte} still refused after {TeleportRetryMs / 1000} s ({error ?? "aetheryte not in the teleport list"}); stopping here.");
        ChatQueue.Orange($"[Lifestream] {"Could not teleport to the gateway aetheryte. Please continue manually.".Loc()}");
        return true;
    }

    /// <summary>傳送完確認人在嘆息海才騎過去;不在就停下,不在錯的區域繞圈。</summary>
    private static bool? StartEntranceRide()
    {
        if(Result != Outcome.Pending)
        {
            PluginLog.Information($"[Cosmic] Entrance-object route not started ({Result}).");
            return true;
        }
        if(P.Territory != TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteTerritoryId)
        {
            Result = Outcome.HandedOver;
            PluginLog.Information($"[Cosmic] Entrance-object route: not in territory {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteTerritoryId} after the teleport (currently {P.Territory}); stopping here.");
            ChatQueue.Orange($"[Lifestream] {"Could not reach ??. Please continue manually.".Loc(Target.PlanetName)}");
            return true;
        }
        var alias = StaticAlias.CosmicExploration;
        StepSince = 0;
        // 別名的移動步驟與互動步驟分開排，中間插一道「真的到入口物件旁了嗎」的檢查；兩段合起來與原本整段相同。
        P.TaskManager.InsertStack(() =>
        {
            alias.Enqueue(true, EntranceAliasStart, EntranceMenuStep - 1);
            P.TaskManager.Enqueue(CheckEntranceReach, "CosmicEntranceCheckReach", Lenient(EntranceMissingSettleMs + 10000));
        });
        return true;
    }

    /// <summary>入口物件互動、入口選單、後續視窗與結果回報；走到入口物件旁之後才排。</summary>
    private static void EnqueueEntranceInteractAndMenu()
    {
        StaticAlias.CosmicExploration.Enqueue(true, EntranceMenuStep - 1, EntranceMenuStep);
        P.TaskManager.Enqueue(SelectEntranceMenu, "CosmicSelectEntranceMenu", Lenient(15000));
        P.TaskManager.Enqueue(FollowUp, "CosmicEntranceFollowUpAndWaitArrival", Lenient(70000));
        P.TaskManager.Enqueue(ReportEntranceRoute, "CosmicEntranceRouteResult");
    }

    /// <summary>
    /// 內建座標騎完之後：已在入口物件的互動範圍（或問不到遊戲）就照原本直接互動；
    /// 騎到逾時、或停下的位置摸不到入口物件，就改用 vnavmesh 走到入口物件<b>現在</b>的位置（每次重查物件表，不寫死新座標）。
    /// 找不到入口物件＝停下交給使用者。
    /// </summary>
    private static bool? CheckEntranceReach()
    {
        if(Result != Outcome.Pending) return true;
        if(!Player.Available) return false;
        var walkFailed = P.followPath?.LastMovementFailed == true;
        var entrance = FindEntranceObject();
        var reach = entrance == null ? null : IsWithinInteractRange(entrance);
        var now = Environment.TickCount64;
        if(entrance == null && StepSince == 0) StepSince = now;
        var verdict = CosmicRouteRules.ClassifyEntranceReach(entrance != null, reach, walkFailed, TaskGotoDestination.IsVnavmeshLoaded(),
            entrance == null ? now - StepSince : 0, EntranceMissingSettleMs);
        if(verdict == CosmicRouteRules.EntranceReach.Wait) return false;
        StepSince = 0;
        if(verdict == CosmicRouteRules.EntranceReach.HandOver)
        {
            Result = Outcome.HandedOver;
            PluginLog.Information($"[Cosmic] Entrance-object route: after the built-in ride (movement failed={walkFailed}) there is no targetable entrance object {EntranceObjectId} nearby at {Player.Position:F1} in territory {P.Territory}; stopping here.");
            ChatQueue.Orange($"[Lifestream] {"Could not reach ??. Please continue manually.".Loc(Target.PlanetName)}");
            return true;
        }
        if(verdict == CosmicRouteRules.EntranceReach.Interact)
        {
            P.TaskManager.InsertStack(EnqueueEntranceInteractAndMenu);
            return true;
        }
        var target = entrance.Position;
        var why = $"the built-in ride ended {Player.DistanceTo(entrance):F1}y from entrance object {EntranceObjectId} at {Player.Position:F1} (movement failed={walkFailed}, in interaction range={reach?.ToString() ?? "unknown"})";
        if(verdict == CosmicRouteRules.EntranceReach.InteractWithoutNavmesh)
        {
            PluginLog.Information($"[Cosmic] Entrance-object route: {why}; vnavmesh is not loaded, so trying to interact from here as before.");
            P.TaskManager.InsertStack(EnqueueEntranceInteractAndMenu);
            return true;
        }
        PluginLog.Information($"[Cosmic] Entrance-object route: {why}; walking to the entrance object's current position {target:F1} with vnavmesh instead of the built-in coordinates.");
        P.followPath?.Stop();
        P.TaskManager.InsertMulti(
            // null（IPC 叫不動）也放行：下一步自己再問一次，叫不動就照原本的方式互動，不中止整條佇列。
            new(() => S.Ipc.VnavmeshIPC.IsReady() != false, "CosmicEntranceNavWaitReady", Lenient(120000)),
            new(() => StartNavToEntrance(target), "CosmicEntranceNavStart")
            );
        return true;
    }

    /// <returns>這一段一律接到入口互動（失敗也照原本的方式試一次），只差多記一行原因。</returns>
    private static bool? StartNavToEntrance(Vector3 target)
    {
        if(Result != Outcome.Pending) return true;
        EntranceNavNoPath = false;
        var task = S.Ipc.VnavmeshIPC.IsReady() == true ? S.Ipc.VnavmeshIPC.Pathfind(Player.Position, target, false) : null;
        if(task == null)
        {
            PluginLog.Information("[Cosmic] Entrance-object route: vnavmesh is not ready or returned no pathfinding task; trying to interact from here as before.");
            P.TaskManager.InsertStack(EnqueueEntranceInteractAndMenu);
            return true;
        }
        P.TaskManager.InsertMulti(
            new(() => task.IsCompleted, "CosmicEntranceNavWaitPath", Lenient(60000)),
            new(() => BeginNavToEntrance(task), "CosmicEntranceNavBeginWalk"),
            new(() => IsEntranceInReach() || P.FollowPath.Waypoints.Count == 0, "CosmicEntranceNavWalk", Lenient(EntranceNavWalkMs)),
            new(AfterNavToEntrance, "CosmicEntranceNavDone")
            );
        return true;
    }

    private static bool? BeginNavToEntrance(Task<List<Vector3>> task)
    {
        // 空路徑不是「距離 0」：vnavmesh 找不到路時回空清單、不擲例外。
        if(!task.IsCompletedSuccessfully || task.Result == null || task.Result.Count == 0)
        {
            var detail = task.IsCompleted ? task.Exception?.InnerException?.Message ?? "empty path" : "pathfinding still running";
            EntranceNavNoPath = true;
            PluginLog.Information($"[Cosmic] Entrance-object route: vnavmesh found no path to the entrance object ({detail}); trying to interact from here as before.");
            return true;
        }
        P.FollowPath.Stop();
        P.FollowPath.Move([.. task.Result], true);
        return true;
    }

    private static bool? AfterNavToEntrance()
    {
        P.FollowPath.Stop();
        if(Result != Outcome.Pending) return true;
        if(!EntranceNavNoPath && !IsEntranceInReach())
        {
            var entrance = FindEntranceObject();
            PluginLog.Information($"[Cosmic] Entrance-object route: after walking with vnavmesh the entrance object is still not in interaction range ({(entrance == null ? "not found" : $"{Player.DistanceTo(entrance):F1}y away")}); trying to interact anyway.");
        }
        P.TaskManager.InsertStack(EnqueueEntranceInteractAndMenu);
        return true;
    }

    /// <summary>每次重查物件表，回傳值只在這一幀用、不保存。</summary>
    private static IGameObject FindEntranceObject()
        => Player.Available ? Svc.Objects.Where(x => x.IsTargetable && x.BaseId == EntranceObjectId).OrderBy(Player.DistanceTo).FirstOrDefault() : null;

    private static bool IsEntranceInReach()
    {
        var entrance = FindEntranceObject();
        return entrance != null && IsWithinInteractRange(entrance) == true;
    }

    /// <returns>遊戲的答案；null＝問不到（函式沒解析到或單例還沒建立）。</returns>
    private static bool? IsWithinInteractRange(IGameObject target)
    {
        if(EventFramework.MemberFunctionPointers.CheckInteractRange == null || !Player.Available) return null;
        try
        {
            var framework = EventFramework.Instance();
            if(framework == null) return null;
            return framework->CheckInteractRange((GameObjectStruct*)Player.Object.Address, (GameObjectStruct*)target.Address, 1, false);
        }
        catch(InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 還沒替使用者選任何項目,卻開始轉場、區域變了、或出現目的地視窗＝使用者自己選了目的地。
    /// 標記後不再按任何東西、不走退路;記一行 Information 並在聊天欄提示。
    /// </summary>
    /// <param name="stage">log 用的階段名。</param>
    /// <param name="stageTerritory">這一段開始時的區域;0＝不比區域。</param>
    private static bool UserChoseDestination(string stage, uint stageTerritory)
    {
        if(SelectedEntry || Result != Outcome.Pending) return false;
        var signal = CosmicRouteRules.UserChoiceSignal(
            betweenAreas: Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
            stageTerritory, P.Territory,
            planetWindowOpen: TryGetAddonByName<AtkUnitBase>("WKSPlanetSelect", out var planet) && IsAddonReady(planet));
        if(signal == null) return false;
        Result = Outcome.UserTookOver;
        PluginLog.Information($"[Cosmic] {stage}: {signal} although Lifestream had not chosen anything, so the destination was picked by hand. Lifestream stops here (no fallback, nothing else is pressed).");
        ChatQueue.Orange($"[Lifestream] {"You chose a destination yourself, so Lifestream has stopped.".Loc()}");
        return true;
    }

    /// <summary>使用者自己選了之後,等轉場結束再交棒,讓後面的到達判定看到真正的區域。</summary>
    private static bool? TransitionSettled()
        => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51] && IsScreenReady() && Player.Interactable;

    /// <summary>入口物件的選單:渴望灣先找舊的「前往渴望灣」,再找 7.3 的「移動到探索區域」;法恩娜只找後者。</summary>
    private static bool? SelectEntranceMenu()
    {
        if(Result == Outcome.UserTookOver) return TransitionSettled();
        if(UserChoseDestination("Entrance menu", TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteTerritoryId)) return false;
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
            MenuToClose = (nint)addon;
            if(!AddonPressGuard.TryPressOnce("SelectString", &addon->AtkUnitBase, nameof(CloseLeftoverSelectString))) return;
            PluginLog.Information("[Cosmic] Cancelling the leftover menu.");
            Callback.Fire(&addon->AtkUnitBase, true, -1);
        }
    }
}
