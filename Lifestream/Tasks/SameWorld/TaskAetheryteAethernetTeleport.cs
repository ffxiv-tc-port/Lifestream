using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Lifestream.Schedulers;
using Lifestream.Tasks.Utility;
using OtterGui;

namespace Lifestream.Tasks.SameWorld;

internal static class TaskAetheryteAethernetTeleport
{
    // Special values for the firmament.
    internal const uint FirmamentRootAetheryteId = 70;
    internal const uint FirmamentAethernetId = uint.MaxValue;
    private const uint FirmamentRootAetheryteTerritoryId = 418;
    private const string Firmament = "The Firmament";

    // Special values for Sinus Ardorum (渴望灣, Cosmic Exploration) — 比照蒼天街:
    // 嘆息海「最佳威兔洞」乙太之光(175)的選單有直達渴望灣的選項。
    internal const uint SinusArdorumRootAetheryteId = 175;
    internal const uint SinusArdorumAethernetId = uint.MaxValue - 1;
    internal const uint SinusArdorumRootAetheryteTerritoryId = 959;
    private const string SinusArdorum = "Sinus Ardorum";

    // 目的地區域本身沒有乙太之光,要靠「鄰近區域某座乙太之光的選單項」才進得去的區域。
    internal const uint FirmamentTerritoryId = 886;
    internal const uint SinusArdorumTerritoryId = 1237;

    /// <summary>
    /// 目的地區域 → 進得去它的「玄關乙太之光 + 專用選單項」。
    ///
    /// ⚠️ 這份對應在遊戲資料裡沒有現成來源 —— 2026-08-03 用台服 7.20 全量 EXD 逐一查證過:
    /// <list type="bullet">
    /// <item><c>TerritoryType.Aetheryte</c> 看起來像,但它是「該區域的所屬乙太之光」而不是「玄關」:
    ///   蒼天街(886) 剛好是 70(對的),渴望灣(1237) 卻指到 174(嘆息海的主水晶),
    ///   而真正有「前往渴望灣」那一項的是 175(最佳威兔洞)。用它會傳送到錯的地方。</item>
    /// <item><c>Warp</c> 表只有蒼天街那一筆(#131342, TerritoryType=886),指的還是另一個入口物件、
    ///   文字也不一樣(「前往蒼天街」vs 乙太之光選單的「傳送到蒼天街」);渴望灣整張表都沒有。</item>
    /// <item><c>Level</c> 表查不到這兩座乙太之光的 Type=12 記錄,<c>TerritoryAethernet</c> 表是空的。</item>
    /// </list>
    /// 所以只能沿用本檔既有的建模(選單「文字」本身仍然是執行期從表解析的,見 Lang,不是寫死的字串)。
    ///
    /// ⚠️ 涵蓋範圍只有上面兩個區域。其他沒有自己乙太之光的區域(優雷卡三區、南方博茲雅、扎杜諾爾、
    /// 新月島…)進去的方式是任務搜尋器而不是乙太之光選單,不在此列;那些區域的行為維持原樣
    /// ——<c>/li goto</c> 照舊回報「目標區域沒有已解鎖的乙太之光」後放棄,不會崩也不會走到錯的地圖。
    /// </summary>
    internal static bool TryGetGatewayRoute(uint destinationTerritory, out uint rootAetheryteId, out uint aethernetId)
    {
        if(TryGetGatewayRouteByTerritory(destinationTerritory, out var route))
        {
            rootAetheryteId = route.RootAetheryteId;
            aethernetId = route.AethernetId;
            return true;
        }
        rootAetheryteId = 0;
        aethernetId = 0;
        return false;
    }

    /// <summary>
    /// 一條玄關路線的完整資料。<see cref="AethernetId"/> 是 Lifestream 自己給這個目的地的偽 id
    /// (<c>uint.MaxValue</c> 往下數),它同時也是**我的最愛/備註/自訂落點的鍵** ——
    /// 傳送面板要列得出這個目的地,靠的就是它。
    /// </summary>
    /// <param name="DestinationTerritory">目的地區域(蒼天街 886 / 渴望灣 1237)。</param>
    /// <param name="RootAetheryteId">玄關乙太之光,也就是「歸屬」的那一座(伊修加爾德下層 70 / 最佳威兔洞 175)。</param>
    /// <param name="AethernetId">Lifestream 給這個目的地的偽 id,同時也是我的最愛的鍵。</param>
    /// <param name="IsEnabled">對應的設定開關。只在建立傳送面板索引時求值,不是每幀。</param>
    internal sealed record GatewayRoute(uint DestinationTerritory, uint RootAetheryteId, uint AethernetId, Func<bool> IsEnabled);

    /// <summary>
    /// 全部的玄關路線。⚠️ <see cref="TryGetGatewayRoute"/> **刻意不看 <see cref="GatewayRoute.IsEnabled"/>** ——
    /// 那兩個開關的語意一直是「要不要把這個地點列進乙太之光的清單」,而不是「准不准前往」。
    /// <c>/li goto</c>、地圖點擊、別名這些**指名道姓要去那裡**的路徑在開關關閉時仍然照舊可用,
    /// 這是修改前就有的行為,不要因為新增了資料表就順手改掉。
    /// </summary>
    internal static readonly GatewayRoute[] GatewayRoutes =
    [
        new(FirmamentTerritoryId, FirmamentRootAetheryteId, FirmamentAethernetId, () => C.Firmament),
        new(SinusArdorumTerritoryId, SinusArdorumRootAetheryteId, SinusArdorumAethernetId, () => C.SinusArdorum),
    ];

    internal static bool TryGetGatewayRouteByTerritory(uint destinationTerritory, out GatewayRoute route)
    {
        foreach(var x in GatewayRoutes)
        {
            if(x.DestinationTerritory == destinationTerritory)
            {
                route = x;
                return true;
            }
        }
        route = null;
        return false;
    }

    /// <summary>
    /// 等「讀取畫面結束」的組態。比照 <see cref="Shortcuts.TaskCosmicShortcut"/> 既有的做法:
    /// 60 秒、<c>abortOnTimeout: false</c>。
    ///
    /// 讀取畫面一旦開始,傳送就已經成立,剩下的只是這台機器載入要多久;預設的 30 秒逾時會把整條佇列
    /// 清掉,結果是「傳送到了玄關乙太之光,但接下來的乙太網傳送整段靜默消失」。載入慢不是失敗訊號,
    /// 所以逾時只丟掉這一步,後面每一步本來就各有自己的等待條件。
    ///
    /// ⚠️ 前一步的 <see cref="Utils.WaitForScreenFalse"/> 刻意維持「逾時即中止」:它等的是讀取畫面
    /// 「開始」,逾時代表詠唱被打斷、傳送根本沒發生。那時候放行會讓後面幾步在錯誤的區域執行。
    /// </summary>
    internal static TaskManagerConfiguration WaitForLoadingScreen => new(timeLimitMS: 60000, abortOnTimeout: false);

    internal static void Enqueue(uint rootAetheryteId, uint aethernetId)
    {
        // 天穹街/渴望灣這兩條玄關路線不經過 TaskAethernetRoute,但呼叫端的「等讀取畫面」共用同一個旗標,
        // 所以這裡要主動設回「後面會有讀取畫面」,免得沿用上一條路線留下的值。
        TaskAethernetRoute.ExpectAethernetTransition();

        if(aethernetId == FirmamentAethernetId)
        {
            if(rootAetheryteId != FirmamentRootAetheryteId)
            {
                throw new Exception($"Special firmament aethernet {FirmamentAethernetId} must be teleported from root aetheryte {FirmamentRootAetheryteId}");
            }
            EnqueueInner(FirmamentRootAetheryteId, FirmamentRootAetheryteTerritoryId, Firmament);
            return;
        }

        if(aethernetId == SinusArdorumAethernetId)
        {
            if(rootAetheryteId != SinusArdorumRootAetheryteId)
            {
                throw new Exception($"Special Sinus Ardorum aethernet {SinusArdorumAethernetId} must be teleported from root aetheryte {SinusArdorumRootAetheryteId}");
            }
            EnqueueInner(SinusArdorumRootAetheryteId, SinusArdorumRootAetheryteTerritoryId, SinusArdorum);
            return;
        }

        if(!S.Data.DataStore.Aetherytes.Keys.TryGetFirst(a => a.ID == rootAetheryteId, out var rootAetheryte))
        {
            throw new Exception($"Root aetheryte {rootAetheryteId} not found");
        }
        if(S.Data.DataStore.Aetherytes[rootAetheryte].TryGetFirst(a => a.ID == aethernetId, out var aethernet))
        {
            // 🔴 一般情形改走共用的 TaskAethernetRoute:身邊摸得到同一個乙太網的節點(主水晶或城內乙太之光)
            // 就走過去用它,摸不到才傳送到主水晶。修改前這裡一律先傳送到主水晶,結果是「站在商業街旁邊
            // 點后翼」也要吃兩次讀取畫面。
            // 📌 不是抄第三份走位碼 —— 那一套是 /li goto 已經上線過的版本(含 v7.20.0.19 的修正),
            // 現在兩邊共用同一份。
            TaskAethernetRoute.Enqueue(rootAetheryte, aethernet);
        }
        else
        {
            if(Svc.ClientState.TerritoryType != rootAetheryte.TerritoryType || Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.HasValue && ae.Value.ID == rootAetheryteId) == null)
            {
                P.TaskManager.InsertMulti(
                    new(() => S.TeleportService.TeleportToAetheryte(rootAetheryteId), "TeleportToRootAetheryte"),
                    new(Utils.WaitForScreenFalse),
                    new(Utils.WaitForScreen, nameof(Utils.WaitForScreen), WaitForLoadingScreen)
                    );
            }
            else
            {
                throw new Exception($"Could not find aetheryte {aethernetId} under root aetheryte {rootAetheryteId}");
            }
        }
    }

    /// <summary>
    /// 「傳送到指定的主水晶 → 互動 → 選單」的原始序列。
    ///
    /// 📌 自從一般情形改走 <see cref="Tasks.Utility.TaskAethernetRoute"/> 之後,實際只剩天穹街與渴望灣
    /// 這兩條**玄關**路線會進來 —— 它們的選單項只掛在那一座特定的乙太之光上,不能改成「走去同網路的
    /// 任一節點」,所以維持原樣。
    ///
    /// ⚠️ 底下一般情形的尾段(開乙太網選單 → 選目的地)刻意保留、沒有刪:它是這兩條特例的對照組,
    /// 也是這個方法本身仍然自洽的證明。要改行為請改 <see cref="Tasks.Utility.TaskAethernetRoute"/>。
    /// </summary>
    private static void EnqueueInner(uint rootAetheryteId, uint territoryId, string aethernetName)
    {
        if(!Player.Available)
        {
            return;
        }

        //DuoLog.Information($"Teleporting to {aethernetName}");
        TaskRemoveAfkStatus.Enqueue();

        // Teleport to the root aetheryte unless we're already close to it.
        P.TaskManager.Enqueue(() =>
        {
            if(Svc.ClientState.TerritoryType != territoryId || Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.HasValue && ae.Value.ID == rootAetheryteId) == null)
            {
                P.TaskManager.InsertMulti(
                    new(() => S.TeleportService.TeleportToAetheryte(rootAetheryteId), "TeleportToRootAetheryte"),
                    new(Utils.WaitForScreenFalse),
                    new(Utils.WaitForScreen, nameof(Utils.WaitForScreen), WaitForLoadingScreen)
                    );
            }
        }, "ConditionalTeleportToRootAetheryte");

        // Target and ensure we're in range to interact.
        P.TaskManager.EnqueueDelay(10, true);
        P.TaskManager.Enqueue(WorldChange.TargetReachableMasterAetheryte);
        P.TaskManager.Enqueue(() =>
        {
            if(P.ActiveAetheryte == null)
            {
                P.TaskManager.InsertMulti(
                    new(WorldChange.LockOn),
                    new(WorldChange.EnableAutomove),
                    new(WorldChange.WaitUntilMasterAetheryteExists),
                    new(WorldChange.DisableAutomove),
                    new FrameDelayTask(10)
                    );
            }
        }, "ConditionalLockonTask");
        P.TaskManager.Enqueue(WorldChange.InteractWithTargetedAetheryte);

        // If we're going to the firmament, select the firmament option.
        if(aethernetName == Firmament)
        {
            P.TaskManager.Enqueue(() => Utils.TrySelectSpecificEntry(Lang.TravelToFirmament, () => EzThrottler.Throttle("SelectString")),
                "SelectTravelToFirmament");
            return;
        }

        // If we're going to Sinus Ardorum, select its menu option (+ optional instance pick).
        if(aethernetName == SinusArdorum)
        {
            P.TaskManager.Enqueue(() => Utils.TrySelectSpecificEntry(Lang.TravelToSinusArdorum, () => EzThrottler.Throttle("SelectString")),
                "SelectTravelToSinusArdorum");
            TaskSinusArdorumTeleport.EnqueueSelectAnyInstance();
            return;
        }

        // Otherwise, open the aethernet menu and select the destination.
        P.TaskManager.Enqueue(WorldChange.SelectAethernet);
        P.TaskManager.EnqueueDelay(C.SlowTeleport ? C.SlowTeleportThrottle : 0);
        P.TaskManager.Enqueue(() => WorldChange.TeleportToAethernetDestination(aethernetName),
            nameof(WorldChange.TeleportToAethernetDestination));
    }
}