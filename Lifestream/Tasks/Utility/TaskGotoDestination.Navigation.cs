using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Data;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.Legacy;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

public static unsafe partial class TaskGotoDestination
{
    /// <summary>
    /// 轉場後等 navmesh 真的換成**這個區域**的網格。詳見 <see cref="NavStaleObserveMs"/> 的說明。
    /// </summary>
    /// <returns>null = 中止整條佇列(vnavmesh 整個叫不動,它自己已經印過一次聊天欄錯誤)。</returns>
    private static bool? WaitNavmeshFresh(MapPointRun run)
    {
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || !Player.Interactable)
        {
            // 又進了一次轉場(也可能是使用者自己動的),前面的觀察全部作廢重來。
            run.Zoned = true;
            run.SawNavNotReady = false;
            run.FreshGateStart = 0;
            return false;
        }
        if(P.Territory != run.Dest.Territory) return false;

        // ⚠️ 刻意不寫成 `IsReady() == true`:它在 IPC 整個叫不動時回 null(同時自己印一次錯誤),
        //    而 null 在 NeoTaskManager 的語意是「中止」。包成 == true 會讓這一步空轉到逾時,
        //    而 IsReady 每一幀都再印一次聊天欄錯誤 —— 一次故障洗出上千行。
        var ready = S.Ipc.VnavmeshIPC.IsReady();
        if(ready == null) return null;
        if(ready == false)
        {
            run.SawNavNotReady = true;
            return false;
        }
        if(!run.Zoned || run.SawNavNotReady)
        {
            PluginLog.Information($"[Goto] {run.Dest.Name}: 目標區域的 vnavmesh 網格已就緒（{(run.Zoned ? "轉場後有觀察到重建" : "沒有經過讀取畫面")}）。");
            return true;
        }

        if(run.FreshGateStart == 0) run.FreshGateStart = Environment.TickCount64;
        var waited = Environment.TickCount64 - run.FreshGateStart;
        if(waited < NavStaleObserveMs + NavStaleFallbackMs) return false;
        PluginLog.Information($"[Goto] 轉場後 {waited}ms 內沒觀察到 vnavmesh 重建網格(可能是同一張網格可重用),改用固定等待後繼續。");
        return true;
    }

    /// <summary>
    /// 抵達目的區域後,把「地圖上的 XZ」解成一個真的站得住的三維落點,然後排最後一段移動。
    /// </summary>
    /// <remarks>
    /// 🔴 一定要解 Y。vnavmesh 的 <c>FindNearestMeshPoly</c> 預設 halfExtentY=5,
    ///    直接拿 Y=0 的地圖座標去尋路,在多數野外地圖會得到**空路徑而且零訊息**。
    /// ⚠️ <c>Query.Mesh.PointOnFloor</c> 的第 2 個參數(allowUnlandable)這裡傳 false。我方 vnavmesh fork 的 <c>IPCProvider.cs</c> 目前刻意**忽略**
    ///    這個參數,並在該處列了「傳 false 的消費端」清單;那份清單還沒有 Lifestream ——
    ///    若日後 vnavmesh 真的把這個參數接上去,這裡會開始拿到 null 並靜默拒絕出發,
    ///    屆時要連同那份清單一起處理。
    /// </remarks>
    private static bool ResolveFloorAndGo(MapPointRun run)
    {
        var dest = run.Dest;
        if(P.Territory != dest.Territory)
        {
            PluginLog.Information($"[Goto] 地圖點中止：人在區域 {P.Territory}，不是目標區域 {dest.Territory}。");
            TravelDiag.Record($"地圖點中止：沒到目標區域 {dest.Territory}");
            ChatQueue.Red($"[Lifestream] {"Could not reach the target zone.".Loc()} ({ExcelTerritoryHelper.GetName(dest.Territory)})");
            P.TaskManager.Abort();
            return true;
        }
        var ready = S.Ipc.VnavmeshIPC.IsReady();
        if(ready != true)
        {
            PluginLog.Information($"[Goto] 地圖點中止：vnavmesh 網格還沒就緒（IsReady={ready?.ToString() ?? "null，IPC 叫不動"}、建置進度 {S.Ipc.VnavmeshIPC.BuildProgress():F2}）。");
            TravelDiag.Record("地圖點中止：vnavmesh 網格沒就緒");
            ChatQueue.Red($"[Lifestream] {"vnavmesh has not finished building the navigation mesh of this zone yet.".Loc()}");
            P.TaskManager.Abort();
            return true;
        }

        var probe = new Vector3(dest.WorldX, FloorProbeAltitude, dest.WorldZ);
        // PointOnFloor 是 SafeWrapper.AnyException 包過的:vnavmesh 中途消失也只是回 null,不會擲例外。
        var floor = S.Ipc.VnavmeshIPC.PointOnFloor(probe, false, FloorProbeHalfExtentXZ);
        if(floor == null)
        {
            // 地圖點擊本來就有幾碼誤差,放寬一次再試 —— 這比直接放棄有用得多。
            floor = S.Ipc.VnavmeshIPC.PointOnFloor(probe, false, FloorProbeHalfExtentXZWide);
            if(floor != null) PluginLog.Information($"[Goto] 地圖點 XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) 在 {FloorProbeHalfExtentXZ} 米內沒有地板,放寬到 {FloorProbeHalfExtentXZWide} 米後找到。");
        }
        if(floor == null)
        {
            PluginLog.Information($"[Goto] 地圖點中止：XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) 在 {FloorProbeHalfExtentXZWide} 米內找不到可站的地板（PointOnFloor 兩次都回 null）。");
            TravelDiag.Record("地圖點中止：找不到地板");
            ChatQueue.Red($"[Lifestream] {"There is no ground to stand on below that spot.".Loc()}");
            P.TaskManager.Abort();
            return true;
        }

        PluginLog.Information($"[Goto] 地圖點落點解析完成:XZ({dest.WorldX:F1}, {dest.WorldZ:F1}) ⇒ {floor.Value:F1}。");
        var territory = P.Territory;
        var flyable = IsFlyingUnlockedIn(territory);
        var distance = Player.Available ? DistanceXZ(Player.Position, floor.Value) : float.NaN;
        var heightDiff = Player.Available ? floor.Value.Y - Player.Position.Y : float.NaN;
        var fly = FieldFlightDecision.ShouldFlyMapPoint(run.RequestedFly, flyable, Svc.Condition[ConditionFlag.Diving],
            Svc.Condition[ConditionFlag.InCombat], Svc.Condition[ConditionFlag.InFlight], distance, heightDiff, C.GotoAutoFlyMinDistance, out var reason);
        PluginLog.Information($"[Goto] 地圖點: 最後一段用{(fly ? "飛行" : "地面")}——{reason}"
            + $"（區域 {territory} 可飛={flyable}、水平距離 {distance:F1} 米、高低差 {heightDiff:F1} 米、門檻 {FieldFlightDecision.EffectiveThreshold(C.GotoAutoFlyMinDistance):F0} 米、要求飛行={run.RequestedFly}）。");
        // 📌 此刻佇列是空的(本任務是 EnqueueToMapPoint 排的最後一個),所以 Enqueue 等同 Insert。
        EnqueueNavTo(floor.Value, fly);
        return true;
    }

    /// <summary>
    /// 使用者自己的野外目的地(自訂落點、<c>/li goto</c>)的最後一段:依 <see cref="FieldFlightDecision"/> 選飛或走。
    /// 必須在角色已抵達目標區域的那一刻呼叫(區域與距離都取當下值)。
    /// </summary>
    internal static void EnqueueFieldNavTo(Vector3 point, string source)
    {
        var territory = P.Territory;
        var flyable = IsFlyingUnlockedIn(territory);
        var distance = Player.Available ? DistanceXZ(Player.Position, point) : float.NaN;
        var fly = FieldFlightDecision.ShouldFly(C.GotoAutoFly, flyable, Svc.Condition[ConditionFlag.Diving],
            Svc.Condition[ConditionFlag.InCombat], Svc.Condition[ConditionFlag.InFlight], distance, C.GotoAutoFlyMinDistance, out var reason);
        PluginLog.Information($"[Goto] {source}: 最後一段用{(fly ? "飛行" : "地面")}——{reason}"
            + $"（區域 {territory} 可飛={flyable}、水平距離 {distance:F1} 米、門檻 {FieldFlightDecision.EffectiveThreshold(C.GotoAutoFlyMinDistance):F0} 米、開關={C.GotoAutoFly}）。");
        EnqueueNavTo(point, fly);
    }

    /// <summary>最後一段移動。<paramref name="fly"/>=true 時走飛行編排,起飛失敗會自動退回地面。</summary>
    internal static void EnqueueNavTo(Vector3 point, bool fly = false)
    {
        TravelDiag.Record($"排入最後一段移動 ⇒ {point:F1}（{(fly ? "飛行" : "地面")}）");
        P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady, "GotoWaitNavReady", new(timeLimitMS: 120000));
        if(fly)
        {
            EnqueueFlyNavTo(point);
        }
        else
        {
            EnqueueGroundNavTo(point);
        }
    }

    /// <summary>地面路線:自己算路徑、自己跟(Lifestream 的 FollowPath)。</summary>
    private static void EnqueueGroundNavTo(Vector3 point)
    {
        P.TaskManager.Enqueue(() =>
        {
            var from = Player.Position;
            var task = S.Ipc.VnavmeshIPC.Pathfind(from, point, false);
            PluginLog.Information(task == null
                ? "[Goto] 地面導航：vnavmesh Nav.Pathfind 沒有回傳工作（IPC 呼叫失敗），下一步會以例外中止。"
                : $"[Goto] 地面導航：向 vnavmesh 要路徑 {from:F1} ⇒ {point:F1}（直線 {Vector3.Distance(from, point):F1} 米）。");
            P.TaskManager.Enqueue(() =>
            {
                if(!task.IsCompleted) return false;
                LogGroundPath(task, point);
                var path = task.Result;
                var mount = ShouldMountForPath(path);
                // ⚠️ 上坐騎這一步刻意 abortOnTimeout:false —— 召喚不出來(卡動畫、區域其實不准騎)
                // 時只放棄坐騎、照樣用走的，不能因此把整條落點/導航佇列中止掉。
                P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(mount), "GotoUseSprint",
                    new(timeLimitMS: 20000, abortOnTimeout: false));
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. path], true));
                return true;
            }, "GotoBuildPath");
            return true;
        }, "GotoNavmeshMaster");
    }

    /// <summary>只寫 log，不改變流程：尋路失敗時後面的 task.Result 照樣擲例外中止。</summary>
    private static void LogGroundPath(Task<List<Vector3>> task, Vector3 target)
    {
        if(task.IsFaulted || task.IsCanceled)
        {
            PluginLog.Information($"[Goto] 地面導航：vnavmesh 尋路失敗（faulted={task.IsFaulted}、canceled={task.IsCanceled}：{task.Exception?.InnerException?.Message ?? task.Exception?.Message}），這一步會以例外中止整條佇列。");
            return;
        }
        var path = task.Result;
        if(path == null)
        {
            PluginLog.Information("[Goto] 地面導航：vnavmesh 回傳 null 路徑，下一步會以例外中止。");
            return;
        }
        if(path.Count == 0)
        {
            PluginLog.Information($"[Goto] 地面導航：vnavmesh 回傳空路徑（目標 {target:F1} 可能不在網格上），角色不會移動。");
            return;
        }
        var length = 0f;
        var prev = Player.Position;
        foreach(var p in path)
        {
            length += Vector3.Distance(prev, p);
            prev = p;
        }
        PluginLog.Information($"[Goto] 地面導航：路徑 {path.Count} 個航點、全長 {length:F0} 米，終點 {path[^1]:F1}（離目標 {Vector3.Distance(path[^1], target):F1} 米）。");
    }

    /// <summary>
    /// 飛行路線:上坐騎 → 起飛 → 交給 vnavmesh 的 <c>SimpleMove.PathfindAndMoveTo(point, true)</c>。
    /// 🔴 起飛失敗一定要退回地面重算,這不是保險而是必要:vnavmesh 的 FollowPath 在
    ///    「路徑要求飛行、角色卻沒上坐騎」時是 <c>_movement.Enabled = false; return;</c> ——
    ///    角色會**站在原地不動、沒有任何訊息**,使用者只看得到「按了沒反應」。
    /// 📌 這條路用的是 vnavmesh 自己的 FollowPath(不是 Lifestream 的),所以要排一個
    ///    「等 Path.IsRunning 變 false」的任務,<c>Lifestream.IsBusy()</c> 才會誠實。
    /// </summary>
    private static void EnqueueFlyNavTo(Vector3 point, FlightYieldGate gate = null)
    {
        gate ??= new();
        // ⚠️ abortOnTimeout:false —— 召喚不出坐騎不代表要中止整趟,下面的分流會改用地面路線。
        P.TaskManager.Enqueue(TaskMount.MountIfCan, "GotoFlyMount", new(timeLimitMS: 20000, abortOnTimeout: false));
        // 🔴 FlyIfCan 在不可飛區域回 null,而 NeoTaskManager 的 null = 中止整條佇列。
        //    一定要用包過的版本,否則「勾了飛行卻在不能飛的地方」會整條靜默斷掉。
        P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp, "GotoFlyTakeoff", new(timeLimitMS: 15000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            if(!Svc.Condition[ConditionFlag.InFlight])
            {
                PluginLog.Information("[Goto] 起飛沒有成功(不可飛/沒上到坐騎/被打斷),改用地面路線。");
                ChatQueue.Green($"[Lifestream] {"Could not take off - walking to the destination instead.".Loc()}");
                EnqueueGroundNavTo(point);
                return true;
            }
            PluginLog.Information($"[Goto] 已在空中,交給 vnavmesh 飛往 {point:F1}。");
            P.TaskManager.Enqueue(() => Movement.VnavMoveOwner.PathfindAndMoveTo(point, true), "GotoFlyStartMove",
                new(timeLimitMS: 30000, abortOnTimeout: false));
            P.TaskManager.Enqueue(() => WaitFlyPathfindThenFollow(point, gate), "GotoFlyWaitPathfind",
                new(timeLimitMS: 120000, abortOnTimeout: false));
            return true;
        }, "GotoFlyDecide");
    }

    /// <summary>
    /// 等 vnavmesh 把飛行路線算完,再決定是「跟著它走完」還是「退回地面重算」。
    /// </summary>
    /// <remarks>
    /// 📌 <c>AsyncMoveRequest.Update</c> 是在同一次呼叫裡把 TaskInProgress 清掉並呼叫 FollowPath.Move 的,
    ///    所以「TaskInProgress 剛變 false」的那一幀,IsRunning 已經反映了結果,兩者之間沒有空窗。
    /// ⚠️ 目標很近時路徑可能只有一兩個點、一瞬間就走完,那也會看到 IsRunning=false ——
    ///    所以只有在**人還離目標很遠**時才判定成失敗,否則會白跑一次地面路線。
    /// </remarks>
    private static bool WaitFlyPathfindThenFollow(Vector3 point, FlightYieldGate gate)
    {
        if(S.Ipc.VnavmeshIPC.PathfindInProgress()) return false;
        if(S.Ipc.VnavmeshIPC.IsRunning())
        {
            P.TaskManager.Enqueue(() => WaitFlyArrival(point, gate), "GotoFlyWaitArrival",
                new(timeLimitMS: 600000, abortOnTimeout: false));
            return true;
        }
        Movement.VnavMoveOwner.Release();
        if(Player.Available)
        {
            var remaining = DistanceXZ(Player.Position, point);
            if(remaining > FlyPathFailureDistance)
            {
                PluginLog.Information($"[Goto] vnavmesh 沒有給出飛行路線(離目標還有 {remaining:F0} 米),改用地面路線。");
                ChatQueue.Green($"[Lifestream] {"No flight path was found - walking to the destination instead.".Loc()}");
                EnqueueGroundNavTo(point);
            }
            else
            {
                PluginLog.Information($"[Goto] vnavmesh 已不在移動，離目標 {remaining:F1} 米（{FlyPathFailureDistance} 米內，視為已抵達）。");
                InsertLand();
            }
        }
        else
        {
            MarkNotArrived();
        }
        return true;
    }

    /// <summary>
    /// 跟著 vnavmesh 飛到停下為止。停在半路而且使用者剛按過移動鍵＝手動中斷：讓位，停手後從目前位置接續
    /// （上限見 <see cref="FlightYieldGate"/>）；抵達就降落。沒抵達的那一趟不念抵達播報。
    /// </summary>
    private static bool WaitFlyArrival(Vector3 point, FlightYieldGate gate)
    {
        var navigating = S.Ipc.VnavmeshIPC.IsRunning() || S.Ipc.VnavmeshIPC.PathfindInProgress();
        if(!Player.Available)
        {
            if(navigating) return false;
            Movement.VnavMoveOwner.Release();
            PluginLog.Information("[Goto] vnavmesh 停止飛行移動（角色目前不可用，量不到距離）。");
            MarkNotArrived();
            return true;
        }
        var remaining = DistanceXZ(Player.Position, point);
        var ev = gate.Observe(Environment.TickCount64, navigating, Svc.Condition[ConditionFlag.Mounted],
            MoveInputProbe.Pressed(), Player.Position, remaining, FlyPathFailureDistance);
        // 讓位中 vnavmesh 可能還在跑我們的路線，旗標留著；其餘結果＝這一趟已結束或交給別人（接續會重新掛上）。
        if(ev is not (FlightWatchEvent.None or FlightWatchEvent.YieldStarted)) Movement.VnavMoveOwner.Release();
        switch(ev)
        {
            case FlightWatchEvent.None:
                return false;
            case FlightWatchEvent.Arrived:
                PluginLog.Information($"[Goto] vnavmesh 停止飛行移動：離目標 {remaining:F1} 米（{FlyPathFailureDistance} 米內，視為已抵達）。");
                InsertLand();
                return true;
            case FlightWatchEvent.StoppedShort:
                PluginLog.Information($"[Goto] vnavmesh 停止飛行移動：離目標還有 {remaining:F1} 米（水平），沒有偵測到手動操作，不接續。");
                MarkNotArrived();
                return true;
            case FlightWatchEvent.YieldStarted:
                PluginLog.Information($"[Goto] 飛行被手動操作中斷（離目標 {remaining:F1} 米）：讓位，停手 {FlightYieldGate.ResumeQuietMs / 1000.0:F0} 秒後從目前位置接續（已接續 {gate.Resumes}/{FlightYieldGate.MaxResumes} 次）。");
                return false;
            case FlightWatchEvent.Resume:
                PluginLog.Information($"[Goto] 使用者已停手：從目前位置接續飛往 {point:F1}（離目標 {remaining:F1} 米，第 {gate.Resumes}/{FlightYieldGate.MaxResumes} 次接續）。");
                P.TaskManager.InsertStack(() => EnqueueFlyNavTo(point, gate));
                return true;
            case FlightWatchEvent.GaveUp:
                PluginLog.Information($"[Goto] 放棄接續飛行：{gate.GiveUpReason}（離目標 {remaining:F1} 米），交給使用者。");
                MarkNotArrived();
                return true;
            default:
                PluginLog.Information($"[Goto] 讓位期間 vnavmesh 又開始移動（別的插件或使用者送了新路線）：這一趟交給它、不再接續（離目標 {remaining:F1} 米）。");
                MarkNotArrived();
                return true;
        }
    }

    /// <summary>這一趟沒走到：讓收尾的抵達播報閉嘴（它看的就是這個旗標）。</summary>
    private static void MarkNotArrived()
    {
        if(P.followPath != null) P.followPath.LastMovementFailed = true;
    }

    /// <summary>
    /// 抵達後降落，不下坐騎（野外導航沒有「抵達後下坐騎」的設定；房區那個只管房區）。
    /// 在空中且動作可用時送一般動作 23（跳下），落地（InFlight 消失）即完成；重送與否見 <see cref="LandRetryGate"/>
    /// （跳下在地面騎乘時＝下坐騎，所以降落途中不重送）。
    /// </summary>
    private static void InsertLand()
    {
        if(!Svc.Condition[ConditionFlag.InFlight]) return;
        var gate = new LandRetryGate();
        P.TaskManager.Insert(() =>
        {
            if(!Svc.Condition[ConditionFlag.InFlight])
            {
                PluginLog.Information($"[Goto] 已降落（送出降落 {gate.Sends} 次）。");
                return true;
            }
            if(!Player.Available) return false;
            var now = Environment.TickCount64;
            var y = Player.Position.Y;
            if(!gate.ShouldSend(now, y)) return false;
            var am = ActionManager.Instance();
            if(am == null || am->GetActionStatus(ActionType.GeneralAction, 23) != 0)
            {
                gate.OnNotReady(now, y);
                return false;
            }
            var accepted = am->UseAction(ActionType.GeneralAction, 23);
            gate.OnSent(now, y);
            if(gate.Sends == 1) PluginLog.Information($"[Goto] 抵達目標上空：送出降落（遊戲回傳 {accepted}）。");
            else PluginLog.Information($"[Goto] 仍在空中、高度 {LandRetryGate.StallWindowMs / 1000.0:F1} 秒內沒在下降：再送一次降落（第 {gate.Sends}/{LandRetryGate.MaxSends} 次，遊戲回傳 {accepted}）。");
            return false;
        }, "GotoFlyLand", new(timeLimitMS: 10000, abortOnTimeout: false));
    }

    /// <summary>
    /// 這一段路值不值得上坐騎。
    /// 📌 「能不能騎」不自己判斷 —— <see cref="TaskMount.MountIfCan"/> 問的是
    /// <c>ActionManager.GetActionStatus(GeneralAction, 9)</c>，那就是遊戲自己對「現在能不能召喚
    /// 坐騎」的答案(區域不准、戰鬥中、室內、沒解鎖…全都涵蓋)，回非 0 時它會直接回 true 放行、不會卡住佇列。
    /// 這裡只補遊戲答不出來的那一半：**值不值得**。
    /// ⚠️ 距離用的是**路徑總長**不是直線距離：要繞路時直線距離會嚴重低估，而那正是最該上坐騎的情況。
    /// </summary>
    private static bool ShouldMountForPath(List<Vector3> path)
    {
        if(!C.GotoUseMount) return false;
        if(path == null || path.Count == 0) return false;
        // 已經在坐騎上就不必再判斷距離：MountIfCan 會立刻回 true，不會有任何額外動作。
        if(Svc.Condition[ConditionFlag.Mounted]) return true;
        // 潛水中上坐騎會先浮上來，等於把使用者拉離原本的路徑
        if(Svc.Condition[ConditionFlag.Diving]) return false;
        if(Svc.Condition[ConditionFlag.InCombat]) return false;

        var length = 0f;
        var prev = Player.Position;
        foreach(var p in path)
        {
            length += DistanceXZ(prev, p);
            prev = p;
        }
        if(length < C.GotoMountMinDistance)
        {
            // 要使用者回報的診斷:「為什麼沒上坐騎」正是他會來問的事。
            PluginLog.Information($"[Goto] Path is {length:F0}y, shorter than the {C.GotoMountMinDistance:F0}y mount threshold - walking.");
            return false;
        }
        PluginLog.Information($"[Goto] Path is {length:F0}y, mounting up before moving.");
        return true;
    }

    internal static void SetFlag(uint territory, Vector3 position, string name)
    {
        // AgentMap 取得器合法回 null;拿不到就不插旗也不開地圖(fail-closed)。
        var agent = AgentMap.Instance();
        if(agent == null) return;
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.RowId ?? 0;
        if(mapId == 0) return;
        agent->SetFlagMapMarker(territory, mapId, position);
        agent->OpenMap(mapId, territory);
        ChatQueue.Green($"[Lifestream] {LocText.VnavmeshNotInstalledFlagged.Loc()} {name}");
    }
}
