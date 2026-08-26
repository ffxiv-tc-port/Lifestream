using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.GameHelpers;
using Lifestream.Schedulers;
using Lifestream.Systems.Legacy;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 「用乙太網前往某座城內乙太之光」的**唯一一份**排程邏輯。
///
/// 📌 這個檔案是把兩份幾乎一樣的序列合起來的結果,不是第三份:
/// <list type="bullet">
/// <item><c>TaskGotoDestination.EnqueueAethernetRoute</c> / <c>InsertMasterAetheryteFallback</c>
///   ——「先看身邊有沒有同網路節點」的版本,只有 <c>/li goto</c> 在用(含 v7.20.0.19
///   「走到了卻不選目標」的修正)。</item>
/// <item><c>TaskAetheryteAethernetTeleport.EnqueueInner</c> 的一般情形
///   ——「一律先傳送到主水晶」的版本,傳送面板 / 地圖點擊 / <c>/li &lt;地名&gt;</c> 都走它。</item>
/// </list>
/// 後者現在改呼叫這裡,所以「站在商業街旁邊點后翼」不會再先詠唱傳送回主水晶、吃兩次讀取畫面。
///
/// 🔴 <b>順序陷阱</b>:這裡的每一段都用 <c>InsertMulti</c>
/// 插到佇列**最前面**,不是 <c>Enqueue</c>。原因是這些步驟是在佇列「執行途中」才決定要不要排的,
/// 而呼叫端(<see cref="TaskGotoDestination"/> 的導航、<see cref="TaskTeleportPanelGo"/> 的落點)
/// 早就把後續步驟排在後面了 —— 用 <c>Enqueue</c> 會讓傳送排到「等讀取畫面」之後。
///
/// 🔴 <b>不碰</b> <c>TaskTryTpToAethernetDestination.Enqueue</c> 的 if/else 鏈:那是
/// <c>Lifestream.AethernetTeleport</c> IPC 的實作,契約是「必須already站在乙太之光範圍內」,
/// 艦隊有三個消費端,其中 Questionable 自己有 MovementController —— Lifestream 若自行走位會變成
/// 兩個移動控制器打架。這裡的改善是那條鏈**間接**經由本檔拿到的。
/// </summary>
internal static class TaskAethernetRoute
{
    /// <summary>
    /// 走去「身邊摸得到的同網路節點」的時限。<see cref="Utils.GetReachableAethernetNetworkNode"/> 的
    /// 50y 上限內,正常步行遠低於這個數字,逾時多半是卡地形。逾時只放棄捷徑、退回傳送。
    /// </summary>
    private const int ApproachTimeLimitMS = 20000;

    /// <summary>
    /// L2「乾脆用走的」的時限。門檻可以被使用者拉到上百碼,又可能繞路,所以比上面寬。
    /// 一樣是逾時就退回原本的乙太網流程,不是卡死。
    /// </summary>
    private const int WalkToDestinationTimeLimitMS = 45000;

    /// <summary>
    /// 這一輪排出來的路線「最後有沒有真的用到乙太網」——也就是後面到底會不會出現讀取畫面。
    ///
    /// 為什麼需要它:呼叫端(<see cref="TaskGotoDestination"/>、<see cref="TaskTeleportPanelGo"/>)
    /// 在路線後面排了一個「等 BetweenAreas」的等待。走乙太網時那是對的;但 L2 選擇用走的時**根本不會有
    /// 讀取畫面**,那個等待就會空轉滿 15 秒才放行。有了這個旗標,呼叫端可以直接跳過。
    ///
    /// ⚠️ 一次只會有一條佇列在跑(<c>P.TaskManager</c> 是單一佇列),所以這個靜態旗標不會交錯。
    /// 排入時一律先設回 true(預設「會有讀取畫面」),只有真的選了走路才在執行當下改成 false;
    /// 走路失敗退回傳送時再改回 true。**最保守的值是 true**——那只是讓呼叫端照舊等待,等同修改前。
    /// </summary>
    internal static bool RouteUsesAethernet { get; private set; } = true;

    /// <summary>把「後面會有讀取畫面」設回預設。任何不經過本檔的乙太網路線(天穹街/渴望灣玄關)排入前也要呼叫,
    /// 免得沿用到上一條路線留下的值。</summary>
    internal static void ExpectAethernetTransition() => RouteUsesAethernet = true;

    /// <summary>
    /// 排入「前往 <paramref name="shard"/>」的完整流程。決策順序:
    /// <list type="number">
    /// <item>L2(預設關):目的地同區且比 <see cref="Data.Config.SkipAethernetIfCloserThan"/> 近 → 直接走過去。</item>
    /// <item>身邊(50y 內)摸得到同一個乙太網的節點 → 走過去互動,用乙太網跳過去(省一次讀取畫面)。</item>
    /// <item>都不成立 → <see cref="InsertMasterAetheryteFallback"/>,也就是修改前的行為。</item>
    /// </list>
    /// 每一層失敗都往下一層退,最差等同完全沒做這個最佳化。
    /// </summary>
    internal static void Enqueue(TinyAetheryte root, TinyAetheryte shard)
    {
        ExpectAethernetTransition();
        if(!Player.Available) return;
        TaskRemoveAfkStatus.Enqueue();
        P.TaskManager.Enqueue(() =>
        {
            if(TryInsertDirectWalk(root, shard)) return;

            // 排除目的地本身:拿目的地開選單再選自己會被遊戲拒絕(LogMessage 1478)。
            var node = Utils.GetReachableAethernetNetworkNode(root, excludeId: shard.ID);
            if(node == null)
            {
                PluginLog.Information($"[Aethernet] No usable node of the \"{root.Name}\" aethernet within reach - teleporting to the root aetheryte first to get to \"{shard.Name}\".");
                InsertMasterAetheryteFallback(root, shard);
                return;
            }

            var nodeName = Utils.TryGetTinyAetheryteFromIGameObjectLenient(node, out var nodeAe) ? $"{nodeAe.Value.Name}({nodeAe.Value.ID})" : $"BaseId {node.BaseId}";
            PluginLog.Information($"[Aethernet] Walking to nearby aethernet node {nodeName}, {DistanceXZ(node.Position, Player.Position):F0}y away, instead of teleporting to \"{root.Name}\" first. Destination: \"{shard.Name}\".");

            // 🔴 「接近節點」這一段的每一步都刻意 abortOnTimeout:false。
            // 為什麼:鎖定與自動移動在目標物件突然消失/變得不可鎖定時會一直回 false,用預設組態
            // (30 秒逾時即中止)會**把整條佇列清掉**——使用者按了傳送卻什麼都沒發生。
            // 全部設成不中止之後,最後那一步 ApproachCheckArrival 一定跑得到,也就一定會退回
            // InsertMasterAetheryteFallback(＝修改前的行為)。這是「最差等同今天」的實作保證。
            P.TaskManager.InsertMulti(
                new(() => WorldChange.TargetReachableAethernetNetworkNode(root, shard.ID), "ApproachTargetNetworkNode",
                    new(timeLimitMS: 5000, abortOnTimeout: false)),
                new(() =>
                {
                    if(!Utils.IsActiveAetheryteInNetwork(root, shard.ID))
                    {
                        P.TaskManager.InsertMulti(
                            new(WorldChange.LockOn, nameof(WorldChange.LockOn), new(timeLimitMS: 5000, abortOnTimeout: false)),
                            new(WorldChange.EnableAutomove, nameof(WorldChange.EnableAutomove), new(timeLimitMS: 5000, abortOnTimeout: false)),
                            new(() => Utils.IsActiveAetheryteInNetwork(root, shard.ID), "WaitArriveAtNetworkNode",
                                new(timeLimitMS: ApproachTimeLimitMS, abortOnTimeout: false)),
                            new(WorldChange.DisableAutomove, nameof(WorldChange.DisableAutomove), new(timeLimitMS: 5000, abortOnTimeout: false)),
                            new FrameDelayTask(10)
                            );
                    }
                }, "ApproachConditionalLockon"),
                new(() =>
                {
                    if(Utils.IsActiveAetheryteInNetwork(root, shard.ID))
                    {
                        P.TaskManager.InsertMulti(
                            new(WorldChange.InteractWithTargetedAetheryte),
                            // 走到的可能是主水晶,也可能是城內乙太之光(子節點)——兩者互動後開的視窗不一樣,
                            // 子節點沒有「乙太網」那一層選單。直接排 SelectAethernet 正是 v7.20.0.19
                            // 「走到了、互動了、卻不選目標」的根因,所以用會看實際視窗決定的版本。
                            new(WorldChange.SelectAethernetIfNeeded),
                            new DelayTask(C.SlowTeleport ? C.SlowTeleportThrottle : 0),
                            new(() => WorldChange.TeleportToAethernetDestination(shard.Name), nameof(WorldChange.TeleportToAethernetDestination))
                            );
                    }
                    else
                    {
                        PluginLog.Information($"[Aethernet] Could not reach the nearby node of \"{root.Name}\" in time - falling back to teleporting to the root aetheryte.");
                        InsertMasterAetheryteFallback(root, shard);
                    }
                }, "ApproachCheckArrival")
                );
        }, "ApproachAethernetNetworkNode");
    }

    /// <summary>
    /// L2:目的地乙太之光就在同一區、而且比使用者設定的門檻近時,乾脆用走的,連乙太網都不用
    /// (省下的是整整一次讀取畫面)。
    ///
    /// 🔴 預設關(<see cref="Data.Config.SkipAethernetIfCloserThan"/> = 0)。這會改變語意
    /// ——「點了傳送面板卻用走的」——所以必須由使用者自己開,而且門檻可調。
    ///
    /// 📌 判準刻意只用**直線距離**,不用 vnavmesh 算路徑長度:
    /// navmesh 只有目前區域那一張、查詢會排隊、partial path 的長度含穿牆直線,而且「空 List」會被誤讀成
    /// 「距離 0 = 走路免費」讓指令靜默無作為。艦隊裡跑最多區域的成熟實作(Questionable 的
    /// <c>AetheryteShortcut</c>)也是純直線 + 常數門檻。
    ///
    /// 走位本身用的是既有的鎖定 + 自動移動(跟上面那條捷徑同一套),沒有引入新的移動機制。
    /// 逾時或走不到就退回 <see cref="InsertMasterAetheryteFallback"/>。
    /// </summary>
    private static bool TryInsertDirectWalk(TinyAetheryte root, TinyAetheryte shard)
    {
        var threshold = C.SkipAethernetIfCloserThan;
        if(threshold <= 0f) return false;
        if(!Player.Available) return false;
        if(shard.TerritoryType != P.Territory) return false;

        var obj = Utils.GetAethernetNodeObject(shard);
        if(obj == null || !obj.IsTargetable)
        {
            PluginLog.Information($"[Aethernet] \"Walk instead of aethernet\" is on, but \"{shard.Name}\" is not loaded/targetable in the object table right now - using the aethernet.");
            return false;
        }
        var dist = DistanceXZ(obj.Position, Player.Position);
        if(dist >= threshold)
        {
            PluginLog.Information($"[Aethernet] \"{shard.Name}\" is {dist:F0}y away, past the {threshold:F0}y walk threshold - using the aethernet.");
            return false;
        }

        PluginLog.Information($"[Aethernet] \"{shard.Name}\" is only {dist:F0}y away (walk threshold {threshold:F0}y) - walking there instead of using the aethernet.");
        RouteUsesAethernet = false;
        // 🔴 這條路徑上的**每一步**都是有時限而且 abortOnTimeout:false 的,故意的:
        // 這樣不管哪一步卡住(目標物件突然消失、鎖定送不出去、走不到),最後的 WalkCheckArrival 一定會被
        // 執行到,也就一定會退回原本的乙太網流程。若讓任何一步逾時即中止,整條佇列會直接消失 ——
        // 那才是比修改前更糟的結果。
        P.TaskManager.InsertMulti(
            new(() => WorldChange.TargetReachableAetheryte(_ => Utils.GetAethernetNodeObject(shard)), "WalkTargetDestinationShard",
                new(timeLimitMS: 5000, abortOnTimeout: false)),
            new(() =>
            {
                if(!IsStandingAt(shard))
                {
                    P.TaskManager.InsertMulti(
                        new(WorldChange.LockOn, nameof(WorldChange.LockOn), new(timeLimitMS: 5000, abortOnTimeout: false)),
                        new(WorldChange.EnableAutomove, nameof(WorldChange.EnableAutomove), new(timeLimitMS: 5000, abortOnTimeout: false)),
                        new(() => IsStandingAt(shard), "WalkWaitArriveAtDestination",
                            new(timeLimitMS: WalkToDestinationTimeLimitMS, abortOnTimeout: false)),
                        new(WorldChange.DisableAutomove, nameof(WorldChange.DisableAutomove), new(timeLimitMS: 5000, abortOnTimeout: false)),
                        new FrameDelayTask(10)
                        );
                }
            }, "WalkConditionalLockon"),
            new(() =>
            {
                if(IsStandingAt(shard))
                {
                    PluginLog.Information($"[Aethernet] Arrived at \"{shard.Name}\" on foot - no aethernet teleport needed.");
                }
                else
                {
                    // 走不到(卡地形、距離其實超出自動移動可靠範圍…)——退回原本可靠的乙太網流程,
                    // 並把「後面會有讀取畫面」設回去,免得呼叫端提早放行。
                    PluginLog.Information($"[Aethernet] Could not walk to \"{shard.Name}\" in time - falling back to the aethernet route.");
                    ExpectAethernetTransition();
                    InsertMasterAetheryteFallback(root, shard);
                }
            }, "WalkCheckArrival")
            );
        return true;
    }

    private static bool IsStandingAt(TinyAetheryte node) => P.ActiveAetheryte != null && P.ActiveAetheryte.Value.ID == node.ID;

    /// <summary>
    /// 退路:傳送到主水晶再走乙太網 —— **這就是修改前的行為**,所以任何一層捷徑失敗時退到這裡都不會比
    /// 原本更糟。
    ///
    /// 🔴 用 <c>InsertMulti</c> 不是 <c>Enqueue</c>:它是在佇列執行途中才被呼叫的,呼叫端早就把
    /// 「等讀取畫面 → 導航/落點」排在後面了,用 Enqueue 會讓傳送排到那些之後。
    ///
    /// 等讀取畫面用的是 <see cref="TaskAetheryteAethernetTeleport.WaitForLoadingScreen"/>(60 秒、
    /// 逾時不中止)。讀取畫面一旦開始傳送就已經成立,剩下的只是這台機器載入要多久;預設 30 秒逾時會把
    /// 整條佇列清掉,結果是「傳送到了主水晶,但接下來的乙太網傳送整段靜默消失」。
    /// </summary>
    internal static void InsertMasterAetheryteFallback(TinyAetheryte root, TinyAetheryte shard)
    {
        P.TaskManager.InsertMulti(
            new(() =>
            {
                if(Svc.ClientState.TerritoryType != root.TerritoryType
                    || Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.HasValue && ae.Value.ID == root.ID) == null)
                {
                    P.TaskManager.InsertMulti(
                        new(() => S.TeleportService.TeleportToAetheryte(root.ID), "TeleportToRootAetheryte"),
                        new(Utils.WaitForScreenFalse),
                        new(Utils.WaitForScreen, nameof(Utils.WaitForScreen), TaskAetheryteAethernetTeleport.WaitForLoadingScreen)
                        );
                }
            }, "FallbackConditionalTeleportToRootAetheryte"),
            new FrameDelayTask(10),
            new(WorldChange.TargetReachableMasterAetheryte),
            new(() =>
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
            }, "FallbackConditionalLockonTask"),
            new(WorldChange.InteractWithTargetedAetheryte),
            // 這條退路鎖定的是主水晶(TargetReachableMasterAetheryte 只認 IsAetheryte=true),照理一定有
            // 「乙太網」選單;仍然用會看實際視窗的版本,是因為它在主水晶情境下行為完全相同,
            // 而萬一鎖到的不是主水晶就不會靜默卡死,還會把當下的選單內容寫進 log。
            new(WorldChange.SelectAethernetIfNeeded),
            new DelayTask(C.SlowTeleport ? C.SlowTeleportThrottle : 0),
            new(() => WorldChange.TeleportToAethernetDestination(shard.Name), nameof(WorldChange.TeleportToAethernetDestination))
            );
    }

    /// <summary>
    /// 只比水平距離。<see cref="TinyAetheryte.Position"/> 本身就是 <see cref="Vector2"/>(沒有 Y),
    /// 而乙太之光座標的 Y 在缺 Level 資料時是從地圖標記換算的、恆為 0 —— 把 Y 算進去會讓不同來源的
    /// 座標無法公平比較。
    /// </summary>
    private static float DistanceXZ(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
