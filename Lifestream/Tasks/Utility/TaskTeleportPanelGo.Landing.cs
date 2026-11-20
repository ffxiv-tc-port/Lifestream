using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

public static unsafe partial class TaskTeleportPanelGo
{
    /// <summary>與 <see cref="EnqueueLanding"/> 開頭兩道閘門同義：true＝它會排出落點步驟。</summary>
    private static bool HasActiveLanding(TeleportPanelEntry entry)
        => C.EnableAetheryteLanding && C.AetheryteLandings.ContainsKey(entry.Id);

    /// <summary>
    /// 若該乙太之光設了自訂落點而且功能已啟用，抵達後再前往落點。
    /// 沒設、或功能關著，就什麼都不做 —— 傳送本身完全不受影響。
    /// </summary>
    private static void EnqueueLanding(TeleportPanelEntry entry)
    {
        if(!C.EnableAetheryteLanding) return;
        if(!C.AetheryteLandings.TryGetValue(entry.Id, out var landing)) return;

        // 到了對的區域、能動了，才開始處理落點。逾時中止整條佇列：
        // 沒到對的地圖就開始導航(或更糟，直接寫座標)會在錯的地方亂跑。
        P.TaskManager.Enqueue(
            () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
            "TeleportPanelWaitArrival", new(timeLimitMS: 120000));
        P.TaskManager.Enqueue(Utils.WaitForScreen);

        P.TaskManager.Enqueue(() =>
        {
            if(C.AetheryteLandingDirectWrite)
            {
                if(TryDirectWrite(landing)) return true;
                // 直接寫失敗(例如在戰鬥中/副本裡)就退回安全路徑，不要什麼都不做。
                ChatQueue.Red($"[Lifestream] {"Direct position write refused - falling back to walking.".Loc()}");
            }
            // 落點離某座城內乙太之光比離剛落地的主水晶近很多時，先搭一段都市傳送網再走。
            // 不成立(或功能關著)就從落地點一路走過去。
            if(TryEnqueueLandingAethernetRelay(entry, landing)) return true;
            EnqueueWalkTo(entry, landing);
            return true;
        }, "TeleportPanelLanding");
    }

    /// <summary>
    /// 自訂落點的「城內乙太之光中繼」。
    /// </summary>
    /// <remarks>傳送到主水晶時，從落地點 vnavmesh 一路走到落點可能橫跨整座城(九號解決方案)，
    /// 而同一個都市傳送網裡往往就有一座乙太之光近得多。搭乘那一段沿用 <see cref="TaskAethernetRoute.Enqueue"/>
    /// (含它全部的逾時退路)，走路那一段仍是 <see cref="EnqueueWalkTo"/>，這裡只有「要不要中繼」的判斷。
    /// <para>
    /// 🔴 判斷刻意保守，每一條不成立就直接回 false(＝不中繼)：只處理「傳送到主水晶」(<see cref="TeleportPanelEntry.IsAetheryte"/>，
    /// 目的地本身就是城內乙太之光時人已落在它旁邊，再挑一座別的等於推翻使用者指名的目的地)；
    /// 房屋類(<see cref="TeleportPanelEntry.SubIndex"/> &gt; 0)不處理；
    /// 沒裝 vnavmesh 不處理(那條路只是在地圖上插旗，多吃一次讀取畫面沒有意義)；
    /// 收益低於 <see cref="Data.Config.AetheryteLandingRelayGain"/> 不處理(0 = 整個功能關掉)。
    /// </para>
    /// </remarks>
    private static bool TryEnqueueLandingAethernetRelay(TeleportPanelEntry entry, Vector3 landing)
    {
        if(!entry.IsAetheryte) return false;
        if(entry.SubIndex != 0) return false;
        var gain = C.AetheryteLandingRelayGain;
        if(gain <= 0f) return false;
        // ⚠️ TaskAethernetRoute.Enqueue 開頭就有 `if(!Player.Available) return;` —— 它靜默什麼都不排，
        // 而我們後面那幾步照樣會排進去，結果是白等 15 秒轉場才走路。這裡先擋掉那條路。
        if(!Player.Available) return false;
        if(!Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded)) return false;
        if(S.Data.DataStore?.Aetherytes == null) return false;

        // 這一列對應的主水晶。DataStore 的鍵只有「有 AethernetGroup 的主水晶」，
        // 野外傳送點與房屋傳送點查不到 —— 查不到就代表它根本沒有都市傳送網可搭。
        TinyAetheryte root = default;
        var haveRoot = false;
        foreach(var master in S.Data.DataStore.Aetherytes.Keys)
        {
            if(master.ID != entry.Id) continue;
            root = master;
            haveRoot = true;
            break;
        }
        if(!haveRoot) return false;
        // 同一個都市傳送網可以橫跨區域(蒼天街/住宅區)，只在落點所在的那一區裡比。
        if(root.TerritoryType != entry.Territory) return false;
        if(!TryGetAetheryteGroundPosition(root, out var rootPos))
        {
            PluginLog.Information($"[TeleportPanel] Cannot work out where aetheryte \"{entry.Name}\" ({entry.Id}) actually is, so the custom landing cannot be compared against the aethernet - walking the whole way.");
            return false;
        }

        var arrivalDistance = DistanceXZ(rootPos, landing);

        TinyAetheryte best = default;
        var bestDistance = float.MaxValue;
        foreach(var child in S.Data.DataStore.Aetherytes[root])
        {
            // 選單上不會出現的隱藏節點(飛空艇著陸場之類)不能當中繼點。
            if(child.Invisible) continue;
            if(child.TerritoryType != entry.Territory) continue;
            if(!TaskGotoDestination.IsAetheryteUnlocked(child.ID)) continue;
            if(!TryGetAetheryteGroundPosition(child, out var childPos)) continue;
            var d = DistanceXZ(childPos, landing);
            if(d < bestDistance)
            {
                bestDistance = d;
                best = child;
            }
        }

        if(bestDistance == float.MaxValue)
        {
            PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, no unlocked aethernet shard with a usable position in territory {entry.Territory} - decision = walk the whole way.");
            return false;
        }

        var saved = arrivalDistance - bestDistance;
        if(saved < gain)
        {
            PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, nearest aethernet shard \"{best.Name}\" {bestDistance:F0}y - only {saved:F0}y saved, below the {gain:F0}y threshold, decision = walk the whole way.");
            return false;
        }

        PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, nearest aethernet shard \"{best.Name}\" {bestDistance:F0}y - {saved:F0}y saved (threshold {gain:F0}y), decision = take the aethernet there first, then walk.");

        // 🔴 這裡用 Enqueue 不是 InsertMulti，而且那是對的：本方法是從佇列**最後一步**
        // (TeleportPanelLanding)裡呼叫的，此刻佇列後面什麼都沒有，走路那段也是接在這些之後才排進去。
        // (TaskAethernetRoute 內部自己會用 InsertMulti 把實際步驟插到這幾步前面，那是它的職責。)
        TaskAethernetRoute.Enqueue(root, best);
        // ⚠️ 路線若決定「用走的」(RouteUsesAethernet=false)就根本不會有讀取畫面，直接放行，
        // 否則這裡會空轉滿 15 秒才往下走。與本檔上面那條城內乙太之光路線同一個判斷。
        P.TaskManager.Enqueue(
            () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
            "TeleportPanelLandingRelayWaitTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
        P.TaskManager.Enqueue(Utils.WaitForScreen);
        // 🔴 這一步刻意 abortOnTimeout:false。中繼是最佳化，不是必要條件：不管乙太之光那一段
        // 有沒有真的成功(選單沒開、被打斷、逾時)，人都還在同一個區域裡，接下來照樣走得到落點 ——
        // 逾時即中止會把走路那段一起清掉。
        P.TaskManager.Enqueue(
            () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
            "TeleportPanelLandingRelayWaitArrival", new(timeLimitMS: 120000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            PluginLog.Information($"[TeleportPanel] Aethernet relay finished at territory {P.Territory}, {(Player.Available ? $"{DistanceXZ(Player.Position, landing):F0}y" : "unknown distance")} from the custom landing - walking the rest.");
            EnqueueWalkTo(entry, landing);
            return true;
        }, "TeleportPanelLandingRelayWalk");
        return true;
    }

    /// <summary>
    /// 一座乙太之光在世界中的水平位置(Y 一律填 0，呼叫端只比 XZ)。
    /// </summary>
    /// <remarks>🔑 <b>優先用 <see cref="TinyAetheryte.Position"/>，不要用
    /// <see cref="ECommons.GameHelpers.Map.AetherytePosition(uint)"/></b>：後者查不到 <c>Level</c> 資料時
    /// 退回地圖標記換算，而那個換算**沒有把 <c>Map.OffsetX</c>/<c>Map.OffsetY</c> 減掉**
    /// (九號解決方案差 (0, 90)、圖萊尤拉 (50, -70)、多瑪飛地 (23, 34))；<c>StaticData.json</c> 的 <c>CustomPositions</c> 就是補這個缺項，
    /// 而 <see cref="Systems.Legacy.DataStore.GetTinyAetheryte"/> 是**先查它**再退回地圖標記的。
    /// <para>
    /// 退路仍留著 <see cref="Systems.TeleportPanel.TeleportPanelIndex.GetPosition"/>(它會先查
    /// <c>Level</c> 表)，只在 DataStore 兩個來源都空的時候才用得到。</para>
    /// <para>
    /// ⚠️ <b>精度只有幾十碼</b>(地圖標記畫在圖示中心而不是物件上，水平差可達上百碼)：
    /// 這些座標只夠用來回答「哪一座明顯比較近」，**不能**拿來當導航目標或判斷「是不是已經到了」；
    /// 呼叫端的門檻(<see cref="Data.Config.AetheryteLandingRelayGain"/>)本來就要蓋過這個誤差。
    /// 📌 也**不能**反過來把 <c>Level</c> 表 <c>Type=45</c> 當乙太之光清單 —— 該區域的列數比乙太之光多，多出來的會冒充成更近的候選。
    /// </para>
    /// </remarks>
    private static bool TryGetAetheryteGroundPosition(TinyAetheryte a, out Vector3 position)
    {
        // (0,0) 是 DataStore「兩個來源都查不到」的哨兵值，不是真的世界原點。
        if(a.Position != Vector2.Zero)
        {
            position = new Vector3(a.Position.X, 0f, a.Position.Y);
            return true;
        }
        var fallback = TeleportPanelIndex.GetPosition(a.ID);
        if(fallback == null || fallback.Value.X == 0f && fallback.Value.Z == 0f)
        {
            position = default;
            return false;
        }
        position = fallback.Value;
        return true;
    }

    /// <summary>從目前所在位置走去落點。</summary>
    /// <remarks>
    /// 📌 這裡的診斷刻意寫 <c>Information</c>:「人被丟在奇怪的地方 / 卡住了」這類回報,
    /// 沒有<b>起點、終點、走不走得成</b>三件事就只能用猜的(要使用者回報的診斷)。
    /// ⚠️ 起點要在**這一刻**取:落地後角色可能還在滑動,事後回推會對不上。
    /// </remarks>
    private static void EnqueueWalkTo(TeleportPanelEntry entry, Vector3 landing)
    {
        var hasVnavmesh = Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded);
        var from = Player.Available ? Player.Position : default;
        var straight = Player.Available ? Vector3.Distance(from, landing) : -1f;
        PluginLog.Information($"[TeleportPanel] 自訂落點 ⇒ 用走的:\"{entry.Name}\"(乙太之光 {entry.Id}、區域 {entry.Territory}),"
            + $"起點 {from:F2},落點 {landing:F2},直線 {straight:F1} 米,vnavmesh={hasVnavmesh}。");
        if(hasVnavmesh)
        {
            TaskGotoDestination.EnqueueFieldNavTo(landing, $"自訂落點「{entry.Name}」");
        }
        else
        {
            var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(entry.Territory)?.Map.RowId ?? 0;
            // AgentMap 取得器合法回 null;拿不到就只印訊息、不插旗(同檔 FlagOnMap 的寫法)。
            var agent = AgentMap.Instance();
            if(mapId != 0 && agent != null)
            {
                agent->SetFlagMapMarker(entry.Territory, mapId, landing);
                agent->OpenMap(mapId, entry.Territory);
            }
            ChatQueue.Green($"[Lifestream] {LocText.VnavmeshNotInstalledFlagged.Loc()} {entry.DisplayName}");
        }
    }

    /// <summary>
    /// 🔴 直接把玩家座標寫進遊戲記憶體(瞬移)。使用者自行承擔風險的功能，預設關閉。
    /// 安全性設計(針對「崩潰」而非「被偵測」)：指標是**當下這一幀重新取得**的(<see cref="Player.GameObject"/> 每次都從
    /// <c>Svc.ClientState.LocalPlayer.Address</c> 重解析)，絕不跨幀保存，也不存 IGameObject；
    /// 呼叫的是 FFXIVClientStructs 的 <c>GameObject.SetPosition</c>(把 X/Y/Z 寫進物件並更新旗標)；
    /// 沒有 hook、沒有每幀常駐、沒有封包。
    /// 前置條件擋掉最容易出事的情境(副本、戰鬥、轉場、坐騎飛行中)。
    /// </summary>
    private static bool TryDirectWrite(Vector3 target)
    {
        var refusal = DirectWriteRefusal();
        if(refusal != null)
        {
            PluginLog.Information($"[TeleportPanel] 不直接寫入座標（{refusal}），改走安全路徑。");
            return false;
        }

        var go = Player.GameObject;
        if(go == null)
        {
            PluginLog.Information("[TeleportPanel] 不直接寫入座標（取不到角色物件），改走安全路徑。");
            return false;
        }

        go->SetPosition(target.X, target.Y, target.Z);
        PluginLog.Information($"[TeleportPanel] Direct position write to {target.X:F1}, {target.Y:F1}, {target.Z:F1}");
        TravelDiag.RecordDirectWrite($"直接寫入座標 {target:F1}（區域 {P.Territory}）");
        return true;
    }

    /// <summary>不准直接寫入的原因；null＝可以寫。</summary>
    private static string DirectWriteRefusal()
    {
        if(!Player.Interactable) return "角色不可互動";
        if(Player.IsInDuty) return "在副本中";
        if(Svc.Condition[ConditionFlag.InCombat]) return "戰鬥中";
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return "讀取畫面中";
        if(Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Occupied]) return "詠唱中或忙碌中";
        return null;
    }
}
