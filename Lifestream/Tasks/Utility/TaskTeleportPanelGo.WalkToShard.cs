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
    /// <summary>
    /// 落地點離所有乙太之光都太遠時，用 vnavmesh 走到最近的那一座。
    ///
    /// 🔴 移動只走 vnavmesh IPC，Lifestream 這邊不自己接管走位 —— 路徑點交給既有的
    /// <see cref="Movement.FollowPath"/>(它自己會偵測 vnavmesh 同時在動並讓路)。
    /// 🔴 vnavmesh 不可用(沒裝／網格沒建好／找不到路)時一律**明確報錯並中止整條佇列**，
    /// 絕不留使用者在原地罰站等逾時。
    /// </summary>
    private static void InsertWalkToNearestShard(TeleportPanelEntry entry)
    {
        if(!TryGetNearestShard(entry.Territory, out var shard, out var shardPos))
        {
            PluginLog.Error($"[TeleportPanel] Landed in territory {entry.Territory} out of interaction range of every aethernet shard, and none of them has a resolvable world position - cannot walk anywhere.");
            DuoLog.Error("Landed too far from every aethernet shard, and Lifestream could not work out where the nearest one is.".Loc());
            P.TaskManager.Abort();
            return;
        }

        var distance = DistanceXZ(shardPos, Player.Position);

        if(!Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded))
        {
            // 標點是額外的協助，不是替代方案 —— 佇列照樣中止，聊天欄也照樣有錯誤訊息。
            FlagOnMap(entry.Territory, shardPos);
            PluginLog.Error($"[TeleportPanel] Landed {distance:F0}y from the nearest aethernet shard \"{shard.Name}\" but vnavmesh is not installed - aborting.");
            DuoLog.Error($"{"Landed too far from the aethernet shards and vnavmesh is not available - please walk to this one yourself:".Loc()} {shard.Name}");
            P.TaskManager.Abort();
            return;
        }

        PluginLog.Information($"[TeleportPanel] Landed {distance:F0}y from the nearest aethernet shard \"{shard.Name}\" (interaction range here is {GetMaxInteractionDistance(entry.Territory):F1}y) - walking there with vnavmesh before taking the aethernet to \"{entry.Name}\".");

        P.TaskManager.InsertMulti(
            // 網格還在建就等它。逾時不中止：下一步會再問一次 IsReady，由它統一報錯，
            // 免得這裡靜默清掉整條佇列。
            // ⚠️ 刻意直接傳 IsReady 這個方法群組，不要包成 `IsReady() == true`：它在 IPC 整個叫不動時**回 null**（＝NeoTaskManager 的「中止」），
            // 包成 == true 會把那個 null 變成 false，於是這一步空轉滿 120 秒，而 IsReady 每一幀都再印一次聊天欄錯誤。
            new(S.Ipc.VnavmeshIPC.IsReady, "TeleportPanelGatewayZoneWaitNav",
                new(timeLimitMS: 120000, abortOnTimeout: false)),
            new(() => StartWalkToShard(shard, shardPos), "TeleportPanelGatewayZonePathfind"),
            new(() => CheckWalkArrival(entry, shard), "TeleportPanelGatewayZoneWalkCheckArrival")
            );
    }

    /// <summary>
    /// 送出尋路要求並把「等路徑 → 衝刺 → 走 → 等走完」四步插到抵達檢查前面。
    /// 回 <c>null</c> = 中止整條佇列(NeoTaskManager 的語意)，只有在已經印出錯誤之後才用。
    /// </summary>
    private static bool? StartWalkToShard(CustomAetheryte shard, Vector3 shardPos)
    {
        if(S.Ipc.VnavmeshIPC.IsReady() != true)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh is not ready (build progress {S.Ipc.VnavmeshIPC.BuildProgress():F2}) - cannot walk to \"{shard.Name}\".");
            DuoLog.Error($"{"The vnavmesh navigation mesh is not ready - please walk to this aethernet shard yourself:".Loc()} {shard.Name}");
            return null;
        }

        // ⚠️ 這個 IPC 掛在 SafeWrapper.AnyException 底下：vnavmesh 那頭擲例外時它是**回 null**，
        // 不是把例外傳上來。不檢查就會在下一步吃 NullReferenceException。
        var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, shardPos, false);
        if(task == null)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh Nav.Pathfind returned no task for \"{shard.Name}\".");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        P.TaskManager.InsertMulti(
            new(() => task.IsCompleted, "TeleportPanelGatewayZoneWaitPath",
                new(timeLimitMS: 60000, abortOnTimeout: false)),
            // 衝刺失敗(卡動畫、技能還在冷卻)只是走得慢一點，不能因此中止整條佇列。
            new(() => TaskMoveToHouse.UseSprint(false), "TeleportPanelGatewayZoneSprint",
                new(timeLimitMS: 20000, abortOnTimeout: false)),
            new(() => BeginFollowPath(task, shard), "TeleportPanelGatewayZoneStartWalk"),
            // 進得了互動範圍就不必走完剩下的路徑點。
            new(() => IsAetheryteInReach() || P.FollowPath.Waypoints.Count == 0,
                "TeleportPanelGatewayZoneWaitWalk",
                new(timeLimitMS: GatewayZoneWalkTimeLimitMS, abortOnTimeout: false))
            );
        return true;
    }

    private static bool? BeginFollowPath(Task<List<Vector3>> task, CustomAetheryte shard)
    {
        if(!task.IsCompleted || task.IsFaulted || task.IsCanceled)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh pathfinding to \"{shard.Name}\" did not produce a path: completed={task.IsCompleted}, faulted={task.IsFaulted}, cancelled={task.IsCanceled}, error={task.Exception?.InnerException?.Message ?? task.Exception?.Message}");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        var path = task.Result;
        if(path == null || path.Count == 0)
        {
            // 🔴 空路徑**不是**「距離 0」。vnavmesh 找不到路、或目標點根本不在網格上時就是回空的，
            // 不擲例外。照舊往下走的話，下一步的「路徑點數 == 0」會被誤讀成「已經走到了」，
            // 然後互動步驟在原地空轉。
            PluginLog.Error($"[TeleportPanel] vnavmesh returned an empty path to \"{shard.Name}\" - the destination is probably off the navmesh.");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        P.FollowPath.Stop();
        P.FollowPath.Move([.. path], true);
        return true;
    }

    /// <summary>
    /// 走完(或走不動了)之後的收尾。三種結果各自明確，沒有「什麼都不做」這一格。
    /// </summary>
    private static void CheckWalkArrival(TeleportPanelEntry entry, CustomAetheryte shard)
    {
        // 提早抵達時佇列裡還留著沒走完的路徑點，收乾淨再往下。
        P.FollowPath.Stop();

        var reached = Player.Available ? Utils.GetValidAetheryte() : null;
        if(reached == null)
        {
            PluginLog.Error($"[TeleportPanel] Walked toward \"{shard.Name}\" but there is still no aetheryte within interaction range.");
            DuoLog.Error($"{"Could not reach this aethernet shard in time:".Loc()} {shard.Name}");
            P.TaskManager.Abort();
            return;
        }

        // 走到的如果就是目的地本身，拿它開選單再選自己會被遊戲以 LogMessage 1478
        // 「此處為目前所在地。」拒絕 —— 與本檔開頭那個前置檢查同一個理由。
        // ⚠️ 用實際站到的那一座來判斷，不是用「原本打算走去哪一座」：兩者在極端情形下會不同。
        var actual = S.Data.CustomAethernet?.GetFromIGameObject(reached);
        if(actual != null && actual.Value.ID == entry.Id)
        {
            PluginLog.Information($"[TeleportPanel] Walked to \"{entry.Name}\" itself - no aethernet hop needed.");
            return;
        }

        PluginLog.Information($"[TeleportPanel] Arrived at \"{actual?.Name ?? shard.Name}\" - taking the aethernet to \"{entry.Name}\".");
        InsertAethernetHop(entry);
    }

    /// <summary>
    /// 現在身邊有沒有摸得到的乙太之光。<see cref="Utils.GetValidAetheryte"/> 會解參考
    /// <c>Svc.ClientState.LocalPlayer</c>，所以一定要先過 <see cref="Player.Available"/>。
    /// </summary>
    private static bool IsAetheryteInReach() => Player.Available && Utils.GetValidAetheryte() != null;

    /// <summary>診斷用：這個區域的互動半徑。蒼天街只有 4.56 碼，log 裡看得到才解釋得了
    /// 「明明已經在蒼天街了為什麼還說摸不到」。</summary>
    private static float GetMaxInteractionDistance(uint territory)
    {
        if(S.Data.CustomAethernet?.ZoneInfo != null
            && S.Data.CustomAethernet.ZoneInfo.TryGetValue(territory, out var zone))
        {
            return zone.MaxInteractionDistance;
        }
        return Data.ZoneDetail.DefaultMaxInteractionDistance;
    }

    /// <summary>
    /// 這個區域裡離玩家最近、而且**座標解得出來**的那一座城內乙太之光。
    ///
    /// 🔑 「解得出來」是硬條件不是加分項：<see cref="CustomAetheryte.Position"/> 只有 XZ，
    /// 而蒼天街那八座的 Y 從 -50 到 +10 差了 60 碼。拿玩家的 Y 硬湊餵給 vnavmesh，
    /// 它會找不到對應的 navmesh 多邊形 —— 而**它的失敗形式是回空路徑不是報錯**。
    /// </summary>
    private static bool TryGetNearestShard(uint territory, out CustomAetheryte shard, out Vector3 position)
    {
        shard = default;
        position = default;
        if(!Player.Available) return false;
        if(S.Data.CustomAethernet?.ZoneInfo == null) return false;
        if(!S.Data.CustomAethernet.ZoneInfo.TryGetValue(territory, out var zone)) return false;

        var found = false;
        var best = float.MaxValue;
        foreach(var candidate in zone.Aetherytes)
        {
            if(candidate.TerritoryType != territory) continue;
            var pos = ResolveShardWorldPosition(candidate);
            if(pos == null)
            {
                PluginLog.Information($"[TeleportPanel] Could not resolve a world position for aethernet shard \"{candidate.Name}\" ({candidate.ID}) - skipping it as a walk target.");
                continue;
            }
            var d = DistanceXZ(pos.Value, Player.Position);
            if(d < best)
            {
                best = d;
                shard = candidate;
                position = pos.Value;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// 一座城內乙太之光的**完整**世界座標(含 Y)。兩個來源：<list type="number">
    /// <item>場上的實體 —— 最準。只在**當下這一幀**讀它的 <c>Position</c>，不保存 <c>IGameObject</c>、不保存任何原生指標；
    ///   比對方式與 <see cref="CustomAethernet.GetFromIGameObject"/> 相同(2D、10 碼)。</item>
    /// <item><c>Level</c> 表 <c>Type=45</c> 的列 —— 物件還沒載入時的退路。⚠️ 新月島(1252) 整個區域一列 <c>Level</c> 都沒有，
    ///   所以「查不到」是**正常結果不是錯誤**，呼叫端必須接受 null(它會跳過那一座)。</item>
    /// </list>
    /// </summary>
    private static Vector3? ResolveShardWorldPosition(CustomAetheryte shard)
    {
        if(P.Territory != shard.TerritoryType) return null;

        if(Player.Available)
        {
            foreach(var obj in Svc.Objects)
            {
                if(!obj.IsAetheryte()) continue;
                var d = new Vector2(obj.Position.X - shard.Position.X, obj.Position.Z - shard.Position.Y).Length();
                if(d < 10f) return obj.Position;
            }
        }

        var best = float.MaxValue;
        Vector3? result = null;
        foreach(var pos in GetLevelShardPositions(shard.TerritoryType))
        {
            var d = new Vector2(pos.X - shard.Position.X, pos.Z - shard.Position.Y).Length();
            if(d < best)
            {
                best = d;
                result = pos;
            }
        }
        // 對得上的話誤差是 0.1 碼等級的。差到 5 碼以上就代表比對的根本不是同一座，寧可回 null。
        return best <= 5f ? result : null;
    }

    private static List<Vector3> GetLevelShardPositions(uint territory)
    {
        if(LevelShardPositions.TryGetValue(territory, out var cached)) return cached;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<Vector3> list = [];
        var sheet = Svc.Data.GetExcelSheet<Level>();
        if(sheet != null)
        {
            foreach(var row in sheet)
            {
                if(row.Type != AethernetShardLevelType) continue;
                if(row.Territory.RowId != territory) continue;
                list.Add(new Vector3(row.X, row.Y, row.Z));
            }
        }
        LevelShardPositions[territory] = list;
        // 這是整段流程裡唯一會掃全表的地方(約 5.8 萬列)，而且一個區域只掃一次。
        // 把耗時一起寫進 log：真的造成掉影格時看得出來是這裡，不必用猜的。
        PluginLog.Information($"[TeleportPanel] Level sheet lists {list.Count} aethernet shard rows (type {AethernetShardLevelType}) in territory {territory}; scan took {sw.Elapsed.TotalMilliseconds:F1}ms (cached from now on).");
        return list;
    }
}
