using ECommons.Configuration;
using Lifestream.Enums;
using Lifestream.Tasks.Shortcuts;

namespace Lifestream.Data;

public class Config : IEzConfig
{
    public bool Enable = true;
    internal bool AllowClosingESC2 = false;
    public int ButtonWidth = 10;
    public int[] ButtonWidthArray = null;
    public int ButtonHeightAetheryte = 1;
    public int ButtonHeightWorld = 5;
    public bool FixedPosition = false;
    public Vector2 Offset = Vector2.Zero;
    public bool UseMapTeleport = true;
    public bool HideAddon = true;
    public HashSet<string> HideAddonList = [.. Utils.DefaultAddons];
    public BasePositionHorizontal PosHorizontal = BasePositionHorizontal.Middle;
    public BasePositionVertical PosVertical = BasePositionVertical.Middle;
    public bool ShowAethernet = true;
    public bool ShowWorldVisit = true;
    public HashSet<uint> Favorites = [];
    public HashSet<uint> Hidden = [];
    public Dictionary<uint, string> Renames = [];
    public WorldChangeAetheryte WorldChangeAetheryte = WorldChangeAetheryte.Uldah;
    public bool Firmament = true;
    public bool SinusArdorum = true;
    public bool WalkToAetheryte = true;
    public bool LeavePartyBeforeWorldChange = true;
    public bool AllowDcTransfer = true;
    public bool LeavePartyBeforeLogout = true;
    public bool TeleportToGatewayBeforeLogout = true;
    public bool NoProgressBar = false;
    public Dictionary<string, int> ServiceAccounts = [];
    public bool DCReturnToGateway = false;
    public bool WorldVisitTPToAethernet = false;
    public string WorldVisitTPTarget = "";
    public bool WorldVisitTPOnlyCmd = true;
    public bool UseAutoRetainerAccounts = true;
    public bool SlowTeleport = false;
    public int SlowTeleportThrottle = 0;
    public bool WaitForScreenReady = true;
    public bool ShowWards = true;
    internal bool ShowPlots = false;
    public List<AddressBookFolder> AddressBookFolders = [];
    public bool AddressNoPathing = false;
    public bool AddressApartmentNoEntry = false;
    public bool SingleBookMode = false;
    public List<MultiPath> MultiPathes = [];
    public string GameVersion = "";
    public Dictionary<uint, int> PublicInstances = [];
    public bool ShowInstanceSwitcher = true;
    public bool InstanceSwitcherRepeat = true;
    public bool InstanceTpToAetheryte = false;
    public bool InstanceRemount = false;
    public int InstanceButtonHeight = 10;
    public bool UseSprintPeloton = true;
    public bool UsePeloton = true;
    public bool EnableFlydownInstance = true;
    public bool DisplayChatTeleport = false;
    public bool DisplayPopupNotifications = true;
    public List<HousePathData> HousePathDatas = [];
    public List<HousePathData> CustomHousePathDatas = [];
    public bool EnterMyApartment = true;
    public HouseEnterMode HouseEnterMode = HouseEnterMode.None;
    public bool UseReturn = true;
    public uint PreferredInn = 0;
    public List<AutoPropertyData> PropertyPrio = [new(true, TaskPropertyShortcut.PropertyType.Home), new(true, TaskPropertyShortcut.PropertyType.FC), new(true, TaskPropertyShortcut.PropertyType.Apartment), new(true, TaskPropertyShortcut.PropertyType.Inn), new(false, TaskPropertyShortcut.PropertyType.Shared_Estate)];
    public bool EnableDvcRetry = true;
    public int MaxDcvRetries = 3000;
    public bool DcvUseAlternativeWorld = true;
    public int DcvRetryInterval = 30;
    public bool RetryWorldVisit = true;
    public int RetryWorldVisitInterval = 30;
    public int RetryWorldVisitIntervalDelta = 10;
    public List<CustomAlias> CustomAliases = [];
    public List<CustomDestination> CustomDestinations = [];
    public bool UseGuestWorldTravel = false;
    public bool AllowDCTravelFromCharaSelect = true;
    public List<TravelBanInfo> TravelBans = [];
    public bool TerminateSelfPartyFinder = false;
    public Dictionary<ulong, string> CharaMap = [];
    public bool UseMount = true;
    public int Mount = 0;
    // 預設關（上游預設開）：新增設定沿用現行行為；EzConfig 缺鍵時保留欄位初始值，既有設定檔拿到 false。
    public bool AutoDismount = false;
    public bool WotsitIntegrationEnabled = true;
    public WotsitIntegrationIncludedItems WotsitIntegrationIncludes = new();
    public bool EnableDtrBar = false;
    public Dictionary<ulong, (int Territory, int Ward, int Plot)> PreferredSharedEstates = [];
    public bool LeftAlignButtons = false;
    public int LeftAlignPadding = 0;
    public LiCommandBehavior LiCommandBehavior = LiCommandBehavior.Return_to_Home_World;
    public bool EnableNotifications = true;
    public bool ProgressOverlayToTop = false;
    public bool AllowCustomOverrides = false;
    public bool DisableMapClickOtherTerritory = false;
    public bool EnableAutoCompletion = false;
    public bool AutoCompletionFixedWindow = false;
    public bool AutoCompletionWindowBottom = false;
    public bool AutoCompletionWindowRight = false;
    public Vector2 AutoCompletionWindowOffset = Vector2.Zero;

    // ── 傳送面板(移植自 DailyRoutines BetterTeleport) ─────────────────────────────
    public bool TeleportPanelShowMap = true;
    public bool TeleportPanelHideAethernetInParty = false;
    public float TeleportPanelMapZoom = 1f;

    /// <summary>
    /// 每座乙太之光的自訂落點，鍵是 Aetheryte 表的 RowId ——
    /// 與 DailyRoutines BetterTeleport 的 <c>Positions</c> 同一個鍵空間，可直接匯入。
    /// </summary>
    public Dictionary<uint, Vector3> AetheryteLandings = [];

    /// <summary>傳送後自動前往自訂落點。預設關。開啟後走 vnavmesh(與 <c>/li goto</c> 同一套)。</summary>
    public bool EnableAetheryteLanding = false;

    /// <summary>🔴 改用「直接寫記憶體座標瞬移」抵達落點。預設關，需先開 <see cref="EnableAetheryteLanding"/>。</summary>
    public bool AetheryteLandingDirectWrite = false;

    /// <summary>
    /// 前往自訂落點時，「先搭都市傳送網到離落點最近的城內乙太之光，再走完最後一段」所需的最小直線距離收益(碼)。
    /// 落點離「傳送過去的那座主水晶」X 碼、離同一個都市傳送網裡最近的城內乙太之光 Y 碼，
    /// 只有 <c>X - Y &gt;=</c> 這個值時才中繼；否則從主水晶一路走過去。
    /// 🔴 <b>0 = 關</b>。預設 40：多繞一次都市傳送網要付一次互動＋選單＋讀取畫面(十幾秒)，省下的路程要明顯多於那個成本才划算。
    /// 📌 只比直線距離，不用 vnavmesh 算路徑長度，理由見 <see cref="Tasks.Utility.TaskAethernetRoute"/>。
    /// ⚠️ 只在 <see cref="EnableAetheryteLanding"/> 開著、該乙太之光**有**自訂落點、而且裝了 vnavmesh 時才有作用。
    /// </summary>
    public float AetheryteLandingRelayGain = 40f;

    /// <summary>🔴 「允許傳送到目前所在的乙太之光」記憶體修補。預設關。</summary>
    public bool SameAethernetTeleport = false;

    // ── 我的最愛：自訂排序與分類 ────────────────────────────────────────────────
    // ⚠️ Favorites 是 HashSet(無序)，而且浮動視窗/指令都在讀它 —— **完全不動它**。
    // 排序與分類另存在下面這幾個欄位裡：順序表與分類表是空的時落回字母序與「未分類」，不需要設定檔遷移。

    /// <summary>
    /// 我的最愛的自訂顯示順序(Aetheryte RowId)。**只是一份排名**，不是成員名單 ——
    /// 沒列在這裡的我的最愛不會消失，只是排在有排名的項目之後、彼此照字母序。
    /// </summary>
    public List<uint> FavoriteOrder = [];

    /// <summary>我的最愛的自訂分類定義。清單順序就是顯示順序。</summary>
    public List<FavoriteCategory> FavoriteCategories = [];

    /// <summary>我的最愛 → 分類 Id。查不到、或指向已被刪除的分類，一律落到「未分類」。</summary>
    public Dictionary<uint, uint> FavoriteCategoryAssignment = [];

    // ── 野外導航 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 野外導航(自訂落點 / <c>/li goto</c>)時，路徑夠長就先上坐騎再走。
    /// 📌 預設開 = 刻意不沿用舊行為(這條路徑原本是寫死不上坐騎的)。只影響移動方式，
    /// 上坐騎本身用的是既有的 <see cref="Tasks.Utility.TaskMount"/>，遵守 <see cref="Mount"/> 偏好；
    /// 城內/室內/戰鬥中/副本裡遊戲會直接拒絕，那些情況自動退回步行。
    /// ⚠️ 既有使用者的設定檔已經寫過這個鍵時，反序列化會蓋掉這個預設值 —— 只有沒有該鍵的設定檔才會吃到開。
    /// </summary>
    public bool GotoUseMount = true;

    /// <summary>路徑總長短於這個距離就不上坐騎 —— 上下馬的時間比省下來的還多。</summary>
    public float GotoMountMinDistance = 60f;

    /// <summary>
    /// 野外導航(自訂落點 / <c>/li goto</c>)在可飛的區域、距離夠遠時改走飛行路線(上坐騎→起飛→vnavmesh 飛行)。
    /// 刻意獨立於 <see cref="GotoUseMount"/>:那個只管地面要不要上坐騎,關掉它不代表不想飛。
    /// 起飛失敗或沒有飛行路線時退回地面導航。
    /// </summary>
    public bool GotoAutoFly = true;

    /// <summary>水平直線距離短於這個值就走地面;實際門檻不低於 <see cref="Tasks.Utility.FieldFlightDecision.DistanceFloor"/>。</summary>
    public float GotoAutoFlyMinDistance = 20f;

    /// <summary>
    /// 自動移動時偵測「推不動」並嘗試脫困(跳一下 / 從現在的位置重算路徑)。
    /// </summary>
    /// <remarks>
    /// 🔴 vnavmesh 給的是折線路徑,而 Lifestream 是直線走向下一個航點:中間若擦到欄杆、小石頭、地形接縫這種
    /// 網格沒模到的小障礙物,角色會頂著障礙物原地推到那個航點的 30 秒逾時為止,然後整趟作廢。
    /// 📌 <b>預設開 = 刻意不沿用舊行為。</b>回復動作**只在已經卡住時才會執行**,判斷失準最差也只是白跳一次或白算一次路徑;
    /// 次數用完(見 <see cref="MovementStuckMaxRecoveries"/>)之後交還給原本的 30 秒逾時。
    /// ⚠️ 既有使用者的設定檔已經寫過這個鍵時,反序列化會蓋掉這個預設值 ——
    /// 只有沒有該鍵的設定檔才會吃到開(EzConfig 連 false 都會寫進 JSON)。
    /// </remarks>
    public bool MovementStuckRecovery = true;

    /// <summary>同一段路徑最多嘗試脫困幾次;用完就交還給原本的 30 秒逾時。</summary>
    /// <remarks>
    /// 奇數次跳躍、偶數次重算路徑,所以 6 = 3 次跳 + 3 次重算。
    /// ⚠️ 上限存在的理由是**不能無限重試**:真的過不去的地形要讓它照原本的方式失敗、
    /// 把控制權還給使用者,而不是永遠在原地跳。
    /// </remarks>
    public int MovementStuckMaxRecoveries = 6;

    // ── 抵達提醒 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一整條 <c>/li</c> 傳送／導航鏈真的抵達目的地時，請「塔塔露誇獎」(TataruPraise) 念一句短通知。
    /// </summary>
    /// <remarks>
    /// 📌 <b>預設開</b>：沒安裝 TataruPraise 的人完全不受影響(IPC 呼叫是安靜的 no-op)，
    /// 而 TataruPraise 自己的總開關預設是關的，所以「預設開」不會讓任何人突然聽到聲音。
    /// <para>
    /// ⚠️ 這個預設值只在設定檔裡<b>沒有</b>這個鍵時生效；存過一次之後鍵就在檔裡(EzConfig 連 false 也寫)，
    /// 之後一律以檔內的值為準 —— 再改這裡的預設值對既有使用者無效。
    /// </para>
    /// <para>
    /// ⚠️ 關掉時 <see cref="Tasks.Utility.TaskAnnounceArrival"/> 的哨兵任務根本不會排進佇列。
    /// </para>
    /// </remarks>
    public bool TataruPraiseOnArrival = true;

    /// <summary>
    /// 城內快捷傳送時,若目的地乙太之光**與玩家同區**且直線距離小於這個值,就乾脆走過去、完全不用乙太網
    /// (省下整整一次讀取畫面)。
    /// 🔴 <b>0 = 關,而且是預設值</b>。這會改變語意(點了傳送面板卻用走的),所以必須由使用者自己開。
    /// 建議 60~80。
    /// 📌 只比直線距離,不用 vnavmesh 算路徑長度 —— 理由見 <see cref="Tasks.Utility.TaskAethernetRoute"/>。
    /// </summary>
    public float SkipAethernetIfCloserThan = 0f;

    /// <summary>
    /// 目前「去不了」的世界(World RowId)。列在這裡的世界永遠不會被當成傳送目的地。
    /// </summary>
    /// <remarks>
    /// 出廠值 4034 是台服的「拉姆」—— 該世界已停止營運，切過去只會失敗。
    /// <para>
    /// 這份清單只影響「可以去哪裡」，不影響「有哪些世界」：地址簿既有條目、世界名稱的
    /// 比對與顯示、大廳世界清單的列數都照舊用完整的世界表。判斷入口只有一個 ——
    /// <see cref="PublicWorlds.IsUnavailable(uint)"/>。
    /// </para>
    /// <para>
    /// 設定走 ECommons EzConfig，反序列化是 <c>ObjectCreationHandling.Replace</c>：
    /// 既有設定檔沒有這個鍵時吃得到這個出廠值；使用者在 UI 把它移掉、存檔寫成空集合之後，
    /// Replace 會讓它保持空的，不會被出廠值塞回來。
    /// </para>
    /// </remarks>
    public HashSet<uint> UnavailableWorlds = [4034];
}
