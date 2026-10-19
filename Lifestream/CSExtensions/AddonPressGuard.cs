using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using System.Threading;

namespace Lifestream.CSExtensions;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// 全外掛所有對 addon 的按法(<c>AddonMaster</c> 的 <c>Yes()</c>/<c>Select()</c>/<c>Click()</c>…、<c>Callback.Fire</c>、
/// <c>ClickAddonButton</c>、直送 <c>ReceiveEvent</c>、<c>Close(true)</c>)都要先問過 <see cref="TryPressOnce(string, nint, string, string, bool)"/>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類「按下即關」的窗被按下之後有「正在關閉中」的幾幀,
/// 這段期間 <c>GetAddonByName</c> 仍回得到實例、<c>IsAddonReady</c> 三關全過;此時再送 callback／輸入事件就是攔不到的存取違規
/// —— <b>唯一的防護是「不要送第二次」</b>。節流(<c>GenericThrottle</c>、<c>EzThrottler</c>)不是防護。
/// </para>
/// <para>
/// 粒度＝(視窗位址,參數組);位址<b>只做等值比較,永遠不解參</b>。不帶 <c>paramKey</c>＝「回答」(終結按法):它還熱著時,
/// 同一位址換任何參數組都不准。<see cref="SingleAnswerAddons"/> 裡的窗一律視為不帶參數。
/// 解除點:位址從同名清單消失(每幀輪詢)、同位址 <c>PostSetup</c>(這一幀才登記的不清)、常駐窗連續隱藏 N 幀、逃生口。
/// <c>PreFinalize</c> 是<b>上鎖點</b>,鎖到位址消失或同位址重建。刻意不把 <c>PostRefresh</c> 當解除點(關閉中也可能觸發)。
/// </para>
/// <para>
/// 被擋回 <see langword="false"/> ＝「這一輪沒按到,下一輪再來」;🔴 絕不回 <see langword="null"/>(NeoTaskManager 的 null 是 Abort)。
/// ⚠️ 只在主執行緒使用;任何不確定一律不放行。
/// </para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 終結按法的逃生口與熱窗長度(幀):已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>這不是節流,只是防死鎖的逃生口。走到這裡代表「按了卻沒關掉」,寫 <c>Information</c>。</remarks>
    internal const int RePressEscapeFrames = 60;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗(Talk、清單選列、換頁、換區、TelepotTown 的刻意雙送)的逃生口。
    /// </summary>
    /// <remarks>走逃生口是常態,寫 Debug。關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面。</remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>常駐視窗按下後,要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除;看到可見就歸零重數。</summary>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>同名實例最多掃到第幾個(遊戲同名實例上限);掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>超過這麼多幀沒再被按過、也沒上鎖的位址紀錄,定期回收(早就過了逃生口,不改變任何判定)。</summary>
    private const int SweepMinAgeFrames = 3600;

    private const int SweepIntervalFrames = 60;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key(呼叫端傳的 <c>paramKey</c> 會被忽略)。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只放回答一次就結束的窗。LobbyDKTWorldList、HousingSelectBlock、MansionSelectRoom、_CharaSelectListMenu
    /// 這種「窗一直開著、刻意連送不同 callback」的絕對不能放進來。
    /// SelectString／SelectIconString 若遇到巢狀選單重用同一實例,下一層要等 <see cref="RePressEscapeFrames"/>(慢,不會崩)。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectOk",
        "SelectString",
        "SelectIconString",
        "ContentsFinderConfirm",
        "LobbyDKTCheck",
        "LobbyDKTCheckExec",
        "LobbyWKTCheckHome",
        "_TitleMenu",
        "TitleDCWorldMap",
        "_CharaSelectReturn",
        "ContextMenu",
        "WorldTravelSelect",
        "Trade",
    };

    /// <summary>
    /// 常駐視窗:關閉只是隱藏,位址整場留在清單裡,<see cref="AddonEvent.PreFinalize"/>／<see cref="AddonEvent.PostSetup"/>
    /// 都不會再發生。這些窗改由「連續隱藏 <see cref="HiddenReleaseFrames"/> 幀」解除,而且送出前必須看得見(兩者互為反面)。
    /// </summary>
    /// <remarks>⚠️ 只收實機證實常駐的名字:把會被銷毀的窗誤收進來,等於在它拆除途中提早解除封鎖。</remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextIconMenu",
        "ContextMenu",
        "Talk",
    };

    /// <param name="Frame">按下時的守衛幀號。</param>
    /// <param name="Routine">以多次互動窗身分登記(短逃生口)。</param>
    private readonly record struct PressRecord(long Frame, bool Routine);

    private sealed class AddressRecords(string addonName)
    {
        public string AddonName { get; set; } = addonName;

        /// <summary>參數組 → 紀錄;空字串＝不帶參數的「回答」。</summary>
        public Dictionary<string, PressRecord> ByParam { get; } = new(StringComparer.Ordinal);
        public long LastFrame { get; set; }

        /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數;只有常駐窗會累加,看得見或重新按下就歸零。</summary>
        public int HiddenFrames { get; set; }

        /// <summary>登記時在同名清單裡找得到這個位址;只有它成立才准用「從清單消失」解除。</summary>
        public bool Listed { get; set; }

        /// <summary>看過 <see cref="AddonEvent.PreFinalize"/>:在位址消失或同位址重建之前,任何按法都不准,也沒有逃生口。</summary>
        public bool Finalizing { get; set; }
    }

    /// <summary>這一幀在同名清單裡對某個位址的觀察。零值必須是「不解除、也不放行」的答案。</summary>
    private enum AddonPresence
    {
        /// <summary>查詢不成立(名稱空、位址 0、同名實例超過掃描上限)。零值。</summary>
        Unknown = 0,

        /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
        NotListed = 1,

        Visible = 2,
        Hidden = 3,
    }

    /// <summary>位址 → 這個實例被按過的參數組。位址只當字典鍵,從不解參考。</summary>
    private static readonly Dictionary<nint, AddressRecords> Pressed = [];

    /// <summary>曾經守護過的窗名:這些名字的實例一進 <see cref="AddonEvent.PreFinalize"/> 就上鎖(即使還沒按過)。</summary>
    private static readonly HashSet<string> GuardedNames = new(StringComparer.Ordinal);

    private static readonly List<nint> ReleaseBuf = [];
    private static readonly List<string> StaleKeysBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>0＝還沒掛監聽器。用 <see cref="Interlocked"/>:重複訂閱會讓計數器一個 tick 前進 2,逃生口對半砍。</summary>
    private static int watching;

    /// <summary>守衛自己的幀計數器:每一個 framework tick +1。</summary>
    /// <remarks>
    /// 🔴 刻意不用 <c>UiBuilder.FrameCount</c>:它在隱藏 UI／過場動畫／GPose 期間完全停止前進,
    /// 傳送與跨服途中大量伴隨過場,逃生口會永遠不到期。
    /// </remarks>
    private static long frameCount;

    private static IAddonLifecycle.AddonEventDelegate lifecycleHandler;

    /// <summary>
    /// 從窗上讀出來的文字含 U+FFFD ＝ 這幾幀窗的記憶體正在變動(多半是關閉中),凡是靠文字做判定的按下點<b>這一幀不要碰</b>。
    /// </summary>
    /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做(走既有的「沒比中」路徑)。</returns>
    internal static bool IsTextUnstable(string addonName, string text)
    {
        if(string.IsNullOrEmpty(text) || text.IndexOf((char)0xFFFD) < 0) return false;
        if(EzThrottler.Throttle($"AddonPressGuard-Unstable-{addonName}", 1000))
            PluginLog.Information($"[AddonPressGuard] 「{addonName}」的文字讀到 U+FFFD(視窗記憶體正在變動,多半是關閉中),這一幀不碰它");
        return true;
    }

    /// <inheritdoc cref="IsTextUnstable(string, string)"/>
    internal static bool AnyTextUnstable(string addonName, IEnumerable<string> texts)
    {
        if(texts == null) return false;
        foreach(var text in texts)
        {
            if(IsTextUnstable(addonName, text)) return true;
        }
        return false;
    }

    /// <summary>掛上幀計數器與解除封鎖用的全域監聽器(重複呼叫是 no-op)。</summary>
    /// <remarks>
    /// 外掛初始化時先呼叫一次,讓時鐘排在本外掛其他 <c>Framework.Update</c> 處理常式前面:
    /// 同一外掛的處理常式共用一個 try/catch,排在前面的擲例外時後面那個 tick 全部不跑。
    /// </remarks>
    internal static void EnsureWatching()
    {
        if(Interlocked.CompareExchange(ref watching, 1, 0) != 0) return;
        Svc.Framework.Update += OnFrameworkUpdate;
        lifecycleHandler = OnAddonLifecycle;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, lifecycleHandler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, lifecycleHandler);
    }

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)並清掉全部紀錄。</summary>
    internal static void ForceTeardown()
    {
        if(Interlocked.Exchange(ref watching, 0) == 1)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            if(lifecycleHandler != null)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, lifecycleHandler);
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, lifecycleHandler);
            }
        }
        lifecycleHandler = null;
        Pressed.Clear();
        GuardedNames.Clear();
    }

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    internal static bool TryPressOnce(string addonName, void* addon, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, (nint)addon, label, paramKey, escapeIsRoutine);

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    /// <remarks>給不是 <c>unsafe</c> 的呼叫端用:位址在這裡從 AddonMaster 取,呼叫端不必碰指標。</remarks>
    internal static bool TryPressOnce(string addonName, ECommons.UIHelpers.AddonMasterImplementations.IAddonMasterBase master, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, master == null ? 0 : (nint)master.Base, label, paramKey, escapeIsRoutine);

    /// <summary>
    /// 問「這扇窗的這一個按法現在可以送嗎」,可以的話<b>順便記下</b>已經按過。呼叫端拿到 <see langword="true"/> 才去按。
    /// </summary>
    /// <param name="addonName">窗名(輪詢解除用的名字);空的一律不放行。</param>
    /// <param name="addon">要按的 addon 位址。<b>只做等值比較,這裡永遠不解參。</b></param>
    /// <param name="label">被擋/走逃生口時寫進 log 的站名。</param>
    /// <param name="paramKey">
    /// <see langword="null"/>(預設)＝「回答一次即終結」的按法;非空＝按下不會關窗的按法,同一扇窗對不同參數組各准按一次。
    /// </param>
    /// <param name="escapeIsRoutine"><see langword="true"/> ＝ 同一扇窗本來就會被按很多次,逃生口縮成 <see cref="RoutineRePressEscapeFrames"/>。</param>
    /// <remarks>呼叫點要放在<b>緊接著送出動作之前</b>(通常是 <c>&amp;&amp;</c> 鏈的最後一項)。</remarks>
    internal static bool TryPressOnce(string addonName, nint addon, string label, string paramKey = null, bool escapeIsRoutine = false)
    {
        // 🔴 放在所有 early return 之前:沒有掛上時鐘的話逃生口永遠不會到期。
        EnsureWatching();
        if(addon == 0 || string.IsNullOrEmpty(addonName)) return false;
        if(SingleAnswerAddons.Contains(addonName)) paramKey = null;
        var key = paramKey ?? string.Empty;
        var frame = frameCount;

        // 🔴 常駐窗的「送出前必須可見」與「連續隱藏才解除」互為反面,兩者一組才是防護;查不到也當不可見。
        if(PersistentAddons.Contains(addonName) && LookUp(addonName, addon, readVisibility: true) != AddonPresence.Visible)
        {
            if(EzThrottler.Throttle($"AddonPressGuard-PersistentHidden-{addonName}", 1000))
                PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」是常駐窗,0x{addon:X} 這一幀不可見(或不在同名清單裡),不送");
            return false;
        }

        GuardedNames.Add(addonName);
        Pressed.TryGetValue(addon, out var records);
        if(records != null)
        {
            if(records.Finalizing)
            {
                LogHold(addonName, addon, label, false, "已經看到它 PreFinalize、還沒看到它消失");
                return false;
            }

            // 這扇窗已經被「回答」過(我們自己按了關閉/取消/確認)。窗還在 ＝ 正在關閉中,任何參數組都不准再送。
            if(key.Length != 0
                && records.ByParam.TryGetValue(string.Empty, out var answeredRec)
                && frame - answeredRec.Frame < RePressEscapeFrames)
            {
                LogHold(addonName, addon, label, escapeIsRoutine, "回答過之後還沒觀察到它收掉");
                return false;
            }

            if(records.ByParam.TryGetValue(key, out var rec) && !PassesEscape(addonName, addon, label, rec, escapeIsRoutine, frame))
                return false;
        }
        else
        {
            records = new AddressRecords(addonName);
            Pressed[addon] = records;
        }

        if(!records.Listed && LookUp(addonName, addon, readVisibility: false) == AddonPresence.Visible)
        {
            records.AddonName = addonName;
            records.Listed = true;
        }

        records.ByParam[key] = new PressRecord(frame, escapeIsRoutine);
        records.LastFrame = frame;
        // 🔴 承重:隱藏解除是「從最後一次按下起算」連續隱藏幾幀;漏掉這一行,補按之後的保護期會被前一次累到一半的幀數吃掉。
        records.HiddenFrames = 0;

        // 跨外掛重按診斷:全艦隊逐字統一這一行;不節流、只印位址數值。
        PluginLog.Verbose($"[按窗診斷] plugin=Lifestream addon={addonName} addr=0x{addon:X} key={key}");
        return true;
    }

    /// <summary>同位址同參數按過:還在逃生口內回 <see langword="false"/>;到期就記一行並放行。</summary>
    private static bool PassesEscape(string addonName, nint addon, string label, PressRecord rec, bool escapeIsRoutine, long frame)
    {
        var escapeFrames = escapeIsRoutine ? RoutineRePressEscapeFrames : RePressEscapeFrames;
        var waited = frame - rec.Frame;
        if(waited < escapeFrames)
        {
            LogHold(addonName, addon, label, escapeIsRoutine, "按過之後還沒觀察到它收掉");
            return false;
        }
        if(escapeIsRoutine)
        {
            if(EzThrottler.Throttle($"AddonPressGuard-RoutineEscape-{addonName}", 5000))
                PluginLog.Debug($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次");
        }
        else if(EzThrottler.Throttle($"AddonPressGuard-Escape-{addonName}", 10000))
        {
            PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀既沒消失也沒重建,判定為「上一次沒生效」而不是「正在關閉」,放行補按一次");
        }
        return true;
    }

    /// <summary>被擋那一幀的診斷。終結窗寫 Information、每扇窗 1 秒節流;多次互動窗被擋是常態,不記。</summary>
    private static void LogHold(string addonName, nint addon, string label, bool escapeIsRoutine, string reason)
    {
        if(escapeIsRoutine) return;
        if(EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) {reason},這一幀不再碰它 —— 對關閉中的視窗送事件是攔不到的存取違規");
    }

    /// <summary>🔴 遞增必須是第一行、前面不可以有任何條件:一旦時鐘會停,逃生口就跟著失準。</summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;
        PollRecords();
        if(frameCount % SweepIntervalFrames == 0) SweepColdRecords();
    }

    /// <summary>
    /// 每幀的解除路徑:①登記時找得到、現在從同名清單消失 ⇒ 解除(含 PreFinalize 鎖);
    /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
    /// </summary>
    /// <remarks>判準刻意不用「還可不可見」(常駐窗例外):拆除途中那幾幀三關全過。只有「找到了而且看不見」才累加隱藏幀。</remarks>
    private static void PollRecords()
    {
        if(Pressed.Count == 0) return;
        ReleaseBuf.Clear();
        foreach(var (address, records) in Pressed)
        {
            var persistent = PersistentAddons.Contains(records.AddonName);
            var presence = LookUp(records.AddonName, address, readVisibility: persistent);
            var present = presence != AddonPresence.NotListed;
            if(!present)
            {
                if(records.Listed) ReleaseBuf.Add(address);
                else records.HiddenFrames = 0;
                continue;
            }
            if(!persistent || records.Finalizing) continue;
            if(presence != AddonPresence.Hidden)
            {
                records.HiddenFrames = 0;
                continue;
            }
            if(++records.HiddenFrames < HiddenReleaseFrames) continue;
            ReleaseBuf.Add(address);
            if(HiddenReleaseReported.Add(records.AddonName))
                PluginLog.Information($"[AddonPressGuard] 「{records.AddonName}」:偵測到這是常駐窗(隱藏而不銷毀),按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個窗名每次遊戲只寫一次。");
        }
        foreach(var address in ReleaseBuf) Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    /// <summary>在同名的所有實例裡找 <paramref name="address"/>。</summary>
    /// <remarks>
    /// 位址只做等值比較;解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標,而且只在 <paramref name="readVisibility"/> 時才讀。
    /// 不讀可見性時,找到一律回 <see cref="AddonPresence.Visible"/>。
    /// </remarks>
    private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
    {
        if(address == 0 || string.IsNullOrEmpty(addonName)) return AddonPresence.Unknown;
        for(var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if(unit.Address == 0) return AddonPresence.NotListed;
            if(unit.Address != address) continue;
            if(!readVisibility) return AddonPresence.Visible;
            return unit.IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
        }
        return AddonPresence.Unknown;
    }

    /// <summary>回收「已經不可能再擋住任何人」的槽位(不改變任何一次「按／不按」的判定)。</summary>
    private static void SweepColdRecords()
    {
        if(Pressed.Count == 0) return;
        ReleaseBuf.Clear();
        foreach(var (address, records) in Pressed)
        {
            if(records.Finalizing || frameCount - records.LastFrame < SweepMinAgeFrames) continue;
            ReleaseBuf.Add(address);
        }
        foreach(var address in ReleaseBuf) Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    /// <summary>該位址進入銷毀(上鎖)或重新開始(解除)生命週期。</summary>
    /// <remarks>
    /// 🔴 按位址比對,不是按名稱清空:同名的兩扇窗可以並存(第一扇正在關閉、第二扇剛建好)。
    /// <see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄;<see cref="AddonEvent.PreFinalize"/> 把位址鎖到它從清單消失。
    /// </remarks>
    private static void OnAddonLifecycle(AddonEvent type, AddonArgs args)
    {
        var address = (nint)args.Addon.Address;
        if(address == 0) return;
        if(type == AddonEvent.PreFinalize)
        {
            LockFinalizing(address, args);
            return;
        }
        if(!Pressed.TryGetValue(address, out var records)) return;
        records.Finalizing = false;
        StaleKeysBuf.Clear();
        foreach(var (paramKey, record) in records.ByParam)
        {
            if(record.Frame == frameCount) continue;
            StaleKeysBuf.Add(paramKey);
        }
        foreach(var paramKey in StaleKeysBuf) records.ByParam.Remove(paramKey);
        StaleKeysBuf.Clear();
        if(records.ByParam.Count == 0) Pressed.Remove(address);
    }

    private static void LockFinalizing(nint address, AddonArgs args)
    {
        if(Pressed.TryGetValue(address, out var records))
        {
            records.Finalizing = true;
            return;
        }
        if(GuardedNames.Count == 0) return;
        var name = args.AddonName;
        if(!GuardedNames.Contains(name)) return;
        Pressed[address] = new AddressRecords(name) { Listed = true, Finalizing = true, LastFrame = frameCount };
    }
}
