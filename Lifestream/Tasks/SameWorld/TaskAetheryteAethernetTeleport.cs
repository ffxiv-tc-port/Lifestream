using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Lifestream.Schedulers;
using OtterGui;

namespace Lifestream.Tasks.SameWorld;

internal static class TaskAetheryteAethernetTeleport
{
    // Special values for the firmament.
    private const uint FirmamentRootAetheryteId = 70;
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
    private const uint FirmamentTerritoryId = 886;
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
        if(destinationTerritory == FirmamentTerritoryId)
        {
            rootAetheryteId = FirmamentRootAetheryteId;
            aethernetId = FirmamentAethernetId;
            return true;
        }
        if(destinationTerritory == SinusArdorumTerritoryId)
        {
            rootAetheryteId = SinusArdorumRootAetheryteId;
            aethernetId = SinusArdorumAethernetId;
            return true;
        }
        rootAetheryteId = 0;
        aethernetId = 0;
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
    private static TaskManagerConfiguration WaitForLoadingScreen => new(timeLimitMS: 60000, abortOnTimeout: false);

    internal static void Enqueue(uint rootAetheryteId, uint aethernetId)
    {
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
            EnqueueInner(rootAetheryte.ID, rootAetheryte.TerritoryType, aethernet.Name);
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