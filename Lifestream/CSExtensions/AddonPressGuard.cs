using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;

namespace Lifestream.CSExtensions;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// 全外掛所有對 addon 的按法(<c>AddonMaster</c> 的 <c>Yes()</c>/<c>Select()</c>/<c>Click()</c>/<c>Start()</c>…、
/// <c>Callback.Fire</c>、<c>ClickAddonButton</c>、直送 <c>ReceiveEvent</c>、合成 <c>ListItemClick</c>、
/// <c>Close(true)</c>)都要先問過 <see cref="TryPressOnce(string, nint, string, string, bool)"/>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類「按下即關」的窗被按下之後
/// 有<b>「正在關閉中」的幾幀</b>,這段期間 <c>GetAddonByName</c> 仍然回得到實例、
/// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立 ——
/// 也就是說 <c>IsAddonReady</c> <b>三關全過、擋不住這個窗口</b>。
/// 此時再對它送 callback/輸入事件就是原生 AccessViolation(C0000005)。
/// AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到,
/// 遊戲當場關閉 —— <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
/// 旁證:崩潰前讀窗上文字會出現 U+FFFD(窗記憶體變動中),見 <see cref="IsTextUnstable"/>。
/// </para>
/// <para>
/// 🔴 <b>節流不是防護</b>:節流記的是「上一次動作在哪一幀/哪個時刻」,不是「這扇窗已經按過」。
/// 本外掛的 <c>Utils.GenericThrottle</c>/<c>DCChange.DCThrottle</c> 是 <c>FrameThrottler</c> 10 幀
/// (數的是繪製幀,高 FPS 下比預期短),各站的 <c>EzThrottler</c> key 全域持久且<b>首次必放行</b>;
/// 而 <c>AddonMaster.SelectYesno.Yes()</c> 遇停用鈕會翻 <c>NodeFlags</c> 強制啟用再點,
/// 「按過的按鈕會被遊戲停用所以不會重按」也不成立。
/// </para>
/// <para>
/// 🔑 <b>做法</b>:按下之前先登記「這個名字底下的哪一個實例位址、被送過哪一組參數」,
/// 在觀察到那扇窗真的走完生命週期之前不准再送同一組。
/// 🔴 全程只做<b>位址等值比較,永遠不解參</b> —— 被記下的那個位址隨時可能已經失效。
/// </para>
/// <para>
/// 🔑 <b>粒度=(窗,位址,參數組)</b>:
/// <list type="bullet">
/// <item>「回答一次即終結」的窗(SelectYesno 族、確認鈕按下即關、取消/關閉)<b>不帶</b> <c>paramKey</c>,
/// 整扇窗一把 key —— 不管按的是「是」「否」還是取消,按過任何一個之後窗就在關閉中,別的都不准再送;
/// <see cref="SingleAnswerAddons"/> 裡的窗名一律強制併 key。</item>
/// <item>按下不會關的窗(清單選列、換頁、換區、Talk 翻頁、TelepotTown 的刻意雙送)帶 <c>paramKey</c>,
/// 同一扇窗對不同參數組可以各按一次(保住「同幀對同窗連送不同參數」的正常流程);
/// 但只要這扇窗<b>不帶參數的</b> key 已經記下這個位址(＝我們自己把它關了),任何參數組都不准再送。</item>
/// <item><c>SelectString</c>/<c>SelectIconString</c> 刻意<b>不</b>併 key(用索引當參數組):巢狀選單常常
/// 重用同一個實例只換內容,併 key 會讓下一層的選擇被擋到逃生口。</item>
/// </list>
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>(兩條都只會讓封鎖<b>提早</b>解除,不會延後):
/// <list type="number">
/// <item><b>輪詢</b>:被記下的位址已經不在該名稱的 addon 清單裡(掃全索引 1..99,掃到第一個空的停,
/// 與 <c>Utils.GetSpecificYesno</c> 同一種走法)⇒ 那扇窗真的收乾淨了。本外掛所有按下點都是
/// <c>Framework.Update</c>(含跑在它上面的 NeoTaskManager)驅動,每個 tick 都會再進來一次,所以輪詢可行。</item>
/// <item><b><see cref="IAddonLifecycle"/> 事件</b>:<see cref="AddonEvent.PreFinalize"/>(這一扇正在被銷毀)
/// 與 <see cref="AddonEvent.PostSetup"/>(有新的一扇在這個位址被建立起來),只清<b>同位址</b>的紀錄。
/// 同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>,只靠輪詢的話重開的那扇會被誤認成
/// 「按過的那扇還沒收掉」而白白被擋到逃生口。⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 當解除點:
/// 它有可能在「關閉中」那幾幀觸發,那會把封鎖提早解除,正好把這道防線變成沒有。</item>
/// </list>
/// ⚠️ 判準刻意<b>不</b>用「文字還對不對」或「還可不可見」:窗在拆除途中可能有幾幀讀不到文字、或已被設成不可見,
/// 拿那些當「窗不見了」會<b>正好在最危險的那幾幀</b>把封鎖解除掉。
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>:萬一某扇窗既不 finalize 也不重新 setup(上一次的 callback 根本沒生效、窗就是還開著),
/// 沒有逃生口的話呼叫端會<b>永遠</b>按不下去,等於把崩潰換成靜默失效;而 NeoTaskManager 預設 <c>abortOnTimeout</c>
/// 會清掉<b>整條</b>佇列。單答終結窗 <see cref="RePressEscapeFrames"/>(60 幀),多次互動窗
/// <see cref="RoutineRePressEscapeFrames"/>(15 幀,2026-09-02 艦隊 Talk 政策)。用幀數不用毫秒:
/// 危險窗口的長度本來就是以幀計的,遊戲卡頓時兩者一起拉長。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>:第一次看到某扇窗的某個按法一律當場按下去;被擋回 <see langword="false"/>
/// 對呼叫端的意義一律是「這一輪沒按到,下一輪再來」,與「addon 還沒出現」「節流還沒放行」走同一條既有路徑。
/// 🔴 絕不回 <see langword="null"/>:NeoTaskManager 的 <c>bool?</c> 三態裡 <see langword="null"/> 是 Abort。
/// </para>
/// <para>⚠️ 只在主執行緒使用(與呼叫端的 <c>EzThrottler</c> 同一個前提)。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次(單答終結窗)。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」,這個值只是防死鎖的逃生口。
    /// 60 幀(60fps 下約 1 秒)遠遠大於「關閉中的那幾幀」,補按永遠不會落在危險窗口內。
    /// 走到這個逃生口代表「按了卻沒關掉」,寫 <c>Information</c>(使用者跑 LogLevel 2,Debug 收不到)。
    /// </remarks>
    internal const int RePressEscapeFrames = 60;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗(Talk 是代表;同形狀還有清單選列、換頁、換區、
    /// TelepotTown 的刻意雙送)專用的逃生口:<c>escapeIsRoutine</c> 為 <see langword="true"/> 時用它。
    /// </summary>
    /// <remarks>
    /// 這類窗走逃生口是<b>常態</b>(那才是翻到下一頁/再送下一次的方式),所以逃生口的長度直接決定節奏。
    /// 關閉中的危險窗口實測 &lt;10 幀,15 幀不落在裡面;每頁多等 0.25 秒幾乎無感。走逃生口寫 <c>Debug</c> 不洗版。
    /// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據:關閉中的窗文字會讀壞(U+FFFD)。(2026-09-02 艦隊政策:Talk 類一律 15 幀。)
    /// </remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>輪詢解除時最多掃到第幾個同名實例;掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 99;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key(呼叫端傳的 <c>paramKey</c> 會被忽略)。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只放<b>回答一次就結束</b>的窗。像 LobbyDKTWorldList(清單選列＋取消/確認鈕)、HousingSelectBlock(換頁＋確認)、
    /// MansionSelectRoom(換區＋選房)、_CharaSelectListMenu(選角不關窗)這種「窗一直開著、刻意連送不同 callback」的
    /// <b>絕對不能</b>放進來 —— 那會把正常流程一起擋掉;那些窗由呼叫端決定哪一發帶參數組、哪一發是「回答」。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectOk",
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

    /// <summary>一把 key(窗名＋參數組)底下「已經按過的位址 → 按下當時的幀」。同名窗可能同時開好幾扇(SelectYesno 就會),所以是集合。</summary>
    private sealed class Slot
    {
        public string AddonName;
        public readonly Dictionary<nint, long> Pressed = [];
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);

    // 輪詢用的可重用緩衝:沒有窗被記著時整支 ReleaseVanished 是一個整數比較就回來,不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<nint> RemoveBuf = [];
    private static readonly List<string> EmptyKeysBuf = [];

    private static long CurrentFrame => (long)Svc.PluginInterface.UiBuilder.FrameCount;

    /// <summary>
    /// 從窗上讀出來的文字含 U+FFFD(替換字元)＝ 這幾幀窗的記憶體正在變動(多半是關閉中),
    /// 凡是靠文字做判定的按下點<b>這一幀不要碰</b>。這是崩潰前 log 裡實測看到的旁證,不是防護本體。
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

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    internal static bool TryPressOnce(string addonName, void* addon, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, (nint)addon, label, paramKey, escapeIsRoutine);

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    /// <remarks>給不是 <c>unsafe</c> 的呼叫端用(CustomAliasCommand、TaskChangeDatacenter):位址在這裡從 AddonMaster 取,呼叫端不必碰指標。</remarks>
    internal static bool TryPressOnce(string addonName, ECommons.UIHelpers.AddonMasterImplementations.IAddonMasterBase master, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, master == null ? 0 : (nint)master.Base, label, paramKey, escapeIsRoutine);

    /// <summary>
    /// 問「這扇窗的這一個按法現在可以送嗎」,可以的話<b>順便記下</b>已經按過。呼叫端拿到
    /// <see langword="true"/> 才去按;按法留給呼叫端自己決定。
    /// </summary>
    /// <param name="addonName">窗名。是輪詢/生命週期監聽解除封鎖時用的名字,也是 key 的前半。</param>
    /// <param name="addon">要按的 addon 位址。<b>只做等值比較,這裡永遠不解參。</b></param>
    /// <param name="label">被擋/走逃生口時寫進 log 的站名。</param>
    /// <param name="paramKey">
    /// <see langword="null"/>(預設)＝「回答一次即終結」的按法,整扇窗一把 key;
    /// 非空＝按下不會關窗的按法,同一扇窗對不同參數組各准按一次。<see cref="SingleAnswerAddons"/> 裡的窗一律視為 null。
    /// </param>
    /// <param name="escapeIsRoutine">
    /// <see langword="true"/> ＝ 這個按下點「同一扇窗本來就會被按很多次」,逃生口縮成
    /// <see cref="RoutineRePressEscapeFrames"/>,走逃生口是常態(寫 Debug、被擋不記);
    /// <see langword="false"/>(預設)＝ 走逃生口代表「按了卻沒關掉」這種該被回報的異常,寫 <c>Information</c>。
    /// </param>
    /// <returns><see langword="true"/> ＝ 可以按(而且已經記下);<see langword="false"/> ＝ 這一輪不要按。</returns>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>(通常是 <c>&amp;&amp;</c> 鏈的最後一項)—— 這支一回 <see langword="true"/>
    /// 就已經把「按過了」記下去,登記完卻不按的話會白白封鎖到逃生口為止。
    /// </remarks>
    internal static bool TryPressOnce(string addonName, nint addon, string label, string paramKey = null, bool escapeIsRoutine = false)
    {
        if(addon == 0 || string.IsNullOrEmpty(addonName)) return false;
        ReleaseVanished();
        EnsureWatching(addonName);
        if(SingleAnswerAddons.Contains(addonName)) paramKey = null;
        var frame = CurrentFrame;
        if(paramKey != null
            && Slots.TryGetValue(addonName, out var answered)
            && answered.Pressed.TryGetValue(addon, out var answeredAt)
            && frame - answeredAt < RePressEscapeFrames)
        {
            // 這扇窗已經被「回答」過(我們自己按了關閉/取消/確認)。窗還在 ＝ 正在關閉中,任何參數組都不准再送。
            LogHold(addonName, addon, label, escapeIsRoutine);
            return false;
        }
        var key = paramKey == null ? addonName : addonName + "|" + paramKey;
        if(!Slots.TryGetValue(key, out var slot))
        {
            slot = new() { AddonName = addonName };
            Slots[key] = slot;
        }
        if(slot.Pressed.TryGetValue(addon, out var pressedAt))
        {
            // 這一扇的這一個按法已經送過。窗還在 ＝ 可能正在關閉中,此時再送就是上面說的 AVE。
            var escapeFrames = escapeIsRoutine ? RoutineRePressEscapeFrames : RePressEscapeFrames;
            var waited = frame - pressedAt;
            if(waited < escapeFrames)
            {
                LogHold(addonName, addon, label, escapeIsRoutine);
                return false;
            }
            // 逃生口:等了遠超過關閉所需的時間,窗仍在。視為那次沒生效(或這是另一扇重用了同一塊記憶體的新窗),放行補按一次。
            if(escapeIsRoutine)
            {
                if(EzThrottler.Throttle($"AddonPressGuard-RoutineEscape-{addonName}", 5000))
                    PluginLog.Debug($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次");
            }
            else if(EzThrottler.Throttle($"AddonPressGuard-Escape-{addonName}", 10000))
            {
                PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀既沒消失也沒重建,判定為「上一次沒生效」而不是「正在關閉」,放行補按一次");
            }
        }
        slot.Pressed[addon] = frame;
        return true;
    }

    /// <summary>每幀從 <c>Framework_Update</c> 最前面無條件呼叫:被記下的位址已從清單消失時解除封鎖。沒有紀錄時等於免費。</summary>
    internal static void Tick() => ReleaseVanished();

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)並清掉全部紀錄。</summary>
    internal static void ForceTeardown()
    {
        foreach(var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }
        Watchers.Clear();
        Slots.Clear();
    }

    /// <summary>被擋那一幀的診斷。單答終結窗寫 Information(使用者跑 LogLevel 2)、每扇窗 1 秒節流;多次互動窗被擋是常態,不記。</summary>
    private static void LogHold(string addonName, nint addon, string label, bool escapeIsRoutine)
    {
        if(escapeIsRoutine) return;
        if(EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按過之後還沒觀察到它收掉,這一幀不再碰它 —— 對關閉中的視窗送事件是攔不到的存取違規");
    }

    /// <summary>
    /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄。同一個窗名底下不管有幾把 key 都只掃一次清單。
    /// </summary>
    /// <remarks>🔴 只做位址等值比較,永遠不解參(<c>GetAddonByName(name, i).Address</c> 只是讀清單裡的指標值)。</remarks>
    private static void ReleaseVanished()
    {
        if(Slots.Count == 0) return;
        NamesBuf.Clear();
        EmptyKeysBuf.Clear();
        foreach(var (key, slot) in Slots)
        {
            if(slot.Pressed.Count == 0)
            {
                EmptyKeysBuf.Add(key);
                continue;
            }
            if(!NamesBuf.Contains(slot.AddonName)) NamesBuf.Add(slot.AddonName);
        }
        foreach(var name in NamesBuf)
        {
            PresentBuf.Clear();
            for(var i = 1; i <= MaxAddonIndex; i++)
            {
                var present = Svc.GameGui.GetAddonByName(name, i).Address;
                if(present == 0) break;
                PresentBuf.Add(present);
            }
            foreach(var (key, slot) in Slots)
            {
                if(slot.AddonName != name || slot.Pressed.Count == 0) continue;
                RemoveBuf.Clear();
                foreach(var addr in slot.Pressed.Keys)
                {
                    if(!PresentBuf.Contains(addr)) RemoveBuf.Add(addr);
                }
                foreach(var addr in RemoveBuf) slot.Pressed.Remove(addr);
                if(slot.Pressed.Count == 0) EmptyKeysBuf.Add(key);
            }
        }
        // 空掉的 key 順手收掉,帶動態參數組的 key(TelepotTown 的 callback、清單索引)才不會無限累積。
        foreach(var key in EmptyKeysBuf) Slots.Remove(key);
    }

    /// <summary>同名窗的某個位址被銷毀(PreFinalize)或在該位址重新建立(PostSetup)時,只清那個位址的紀錄。</summary>
    private static void ReleaseAddress(string addonName, nint address)
    {
        if(address == 0 || Slots.Count == 0) return;
        foreach(var slot in Slots.Values)
        {
            if(slot.AddonName == addonName) slot.Pressed.Remove(address);
        }
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。掛上去之後就不再拆(只在 <see cref="ForceTeardown"/> 拆):
    /// 這兩條監聽器只做一次字典移除,成本可忽略,而動態掛/拆比較容易留下懸空的監聽器。
    /// </summary>
    private static void EnsureWatching(string addonName)
    {
        if(Watchers.ContainsKey(addonName)) return;
        IAddonLifecycle.AddonEventDelegate handler = (_, args) => ReleaseAddress(addonName, args.Addon.Address);
        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
