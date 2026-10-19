using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace Lifestream.Data;
[Serializable]
public class CustomAliasCommand
{
    private static readonly CustomAliasCommand Default = new();

    internal string ID = Guid.NewGuid().ToString();
    // 只給編輯器上色用（TabCustomAlias.AssignAllChainGroups 每幀重算），不序列化。
    internal int ChainGroup = 0;
    public CustomAliasKind Kind;
    public Vector3 Point;
    public List<Vector3> ExtraPoints = [];
    public uint Aetheryte;
    public int World;
    public Vector2 CenterPoint;
    public Vector3 CircularExitPoint;
    public (float Min, float Max)? Clamp = null;
    public float Precision = 20f;
    public int Tolerance = 1;
    public bool WalkToExit = true;
    public float SkipTeleport = 15f;
    public uint DataID = 0;
    public bool UseTA = false;
    public List<string> SelectOption = [];
    public bool StopOnScreenFade = false;
    public bool NoDisableYesAlready = false;
    public bool UseFlight = false;
    public float Scatter = 0f;
    public bool RequireTerritoryChange = false;
    // 0＝不限區域。只有移動類（移動／導航／繞圈）會照它等待與停步。
    public uint Territory = 0;
    // null（或 ≤0）＝不先靠近，直接互動（舊版行為）。
    public float? InteractDistance = null;

    public bool ShouldSerializeInteractDistance() => Kind.EqualsAny(CustomAliasKind.Interact) && InteractDistance != Default.InteractDistance;
    public bool ShouldSerializeExtraPoints() => ExtraPoints?.Count > 0;
    public bool ShouldSerializeTerritory() => Territory != 0;
    public bool ShouldSerializeRequireTerritoryChange() => Kind.EqualsAny(CustomAliasKind.Wait_for_Transition);
    public bool ShouldSerializeScatter() => Kind.EqualsAny(CustomAliasKind.Move_to_point) && Scatter > 0f;
    public bool ShouldSerializeUseFlight() => Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point) && UseFlight != Default.UseFlight;
    public bool ShouldSerializePoint() => Point != Default.Point;
    public bool ShouldSerializeAetheryte() => Aetheryte != Default.Aetheryte;
    public bool ShouldSerializeWorld() => World != Default.World;
    public bool ShouldSerializeCenterPoint() => CenterPoint != Default.CenterPoint;
    public bool ShouldSerializeCircularExitPoint() => CircularExitPoint != Default.CircularExitPoint;
    public bool ShouldSerializeClamp() => Clamp != Default.Clamp;
    public bool ShouldSerializePrecision() => Precision != Default.Precision;
    public bool ShouldSerializeTolerance() => Tolerance != Default.Tolerance;
    public bool ShouldSerializeWalkToExit() => WalkToExit != Default.WalkToExit;
    public bool ShouldSerializeSkipTeleport() => SkipTeleport != Default.SkipTeleport;
    public bool ShouldSerializeDataID() => DataID != Default.DataID;
    public bool ShouldSerializeUseTA() => UseTA != Default.UseTA;
    public bool ShouldSerializeSelectOption() => SelectOption.Count > 0;
    public bool ShouldSerializeStopOnScreenFade() => StopOnScreenFade != Default.StopOnScreenFade;
    public bool ShouldSerializeNoDisableYesAlready() => NoDisableYesAlready != Default.NoDisableYesAlready;

    /// <param name="aliasName">只用在診斷 log;null=不知道。</param>
    public void Enqueue(List<Vector3> appendMovement, string aliasName = null)
    {
        if(Kind == CustomAliasKind.Change_world)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            if(World != Player.Object.CurrentWorld.RowId)
            {
                var world = ExcelWorldHelper.GetName(World);
                if(S.Ipc.IPCProvider.CanVisitCrossDC(world))
                {
                    P.TPAndChangeWorld(world, true, skipChecks: true);
                }
                else if(S.Ipc.IPCProvider.CanVisitSameDC(world))
                {
                    P.TPAndChangeWorld(world, false, skipChecks: true);
                }
            }
        }
        else if(Kind == CustomAliasKind.Move_to_point)
        {
            var selectedPoint = PickPoint();
            P.TaskManager.Enqueue(() => Territory == 0 || Territory == Player.Territory, $"{Kind}: Wait for selected territory");
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            // 🔴 不能裸排 FlyIfCan:它在不可飛的區域回 null,而 NeoTaskManager 的 null＝中止整條佇列,
            //    於是「勾了使用飛行」的別名一到不能飛的地方就整條靜默斷掉(連移動都不會做)。
            //    包過的版本把「不能飛」降級成「用走的」。
            if(UseFlight) P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp, $"{Kind}: Fly if can");
            P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false), $"{Kind}: use sprint");
            P.TaskManager.Enqueue(() => P.FollowPath.Move([selectedPoint.Scatter(Scatter), .. appendMovement], true), $"{Kind}: Enqueue move");
            P.TaskManager.Enqueue(WaitForMoveEndOrOccupied, $"{Kind}: Wait until move ends/occupied", new(timeLimitMS: 5.Minutes()));
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
        }
        else if(Kind == CustomAliasKind.Navmesh_to_point)
        {
            P.TaskManager.Enqueue(() => Territory == 0 || Territory == Player.Territory, $"{Kind}: Wait for selected territory");
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            // ⚠️ 刻意直接傳 IsReady 這個方法群組，不要包成 `IsReady() == true`：
            // 它在 IPC 整個叫不動時是**回 null**（同時自己印一次錯誤），而 null 在 NeoTaskManager
            // 的語意是「中止」。包成 == true 會把那個 null 變成 false，於是這一步空轉滿預設的
            // 30 秒逾時，而 IsReady 每一幀都再印一次聊天欄錯誤 —— 一次故障洗出上千行。
            // 導航網格還在建（回 false）時最多等 5 分鐘。
            P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady, "CustomAliasNavmeshWaitNavReady", new(timeLimitMS: 5.Minutes()));
            if(IsUsingTAForMovement())
            {
                P.TaskManager.Enqueue(() =>
                {
                    S.Ipc.TextAdvanceIPC.EnqueueMoveTo2DPoint(new()
                    {
                        Position = Point,
                        NoInteract = true,
                    }, 5f);
                }, $"{Kind}: Move to 2d point via TA");
                P.TaskManager.Enqueue(S.Ipc.TextAdvanceIPC.IsBusy, $"{Kind}: wait until movement starts", new(abortOnTimeout: false, timeLimitMS: 5000));
                P.TaskManager.Enqueue(() => !S.Ipc.TextAdvanceIPC.IsBusy(), $"{Kind}: wait until movement ends", new(timeLimitMS: 1000 * 60 * 5));
                // Utils.IsChainedWithNext 不讓 TA 模式吸收後續移動點，別名執行時這裡的 appendMovement 恆為空；
                // 後面的移動到座標會各自排成獨立指令。
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. appendMovement], true), $"{Kind}: Move appended points");
                P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
                P.TaskManager.Enqueue(() => P.FollowPath.Waypoints.Count == 0, $"{Kind}: Wait until appended points reached");
            }
            else
            {
                // 🔴 裸排 FlyIfCan 在不可飛區域會回 null 而把整條佇列中止掉,所以用包過的版本。
                if(UseFlight) P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp, $"{Kind}: Fly if can");
                P.TaskManager.Enqueue(() =>
                {
                    // 🔴 起飛沒成功就不能再要求飛行路線:vnavmesh 的 FollowPath 對「路徑要求飛行、
                    //    角色卻沒上坐騎」是 `_movement.Enabled = false; return;` —— 角色**站著不動**而且零訊息。
                    var fly = UseFlight && Svc.Condition[ConditionFlag.InFlight];
                    if(UseFlight && !fly) PluginLog.Information("[CustomAlias] 沒有飛起來,改用地面路線尋路。");
                    var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, Point, fly);
                    P.TaskManager.InsertMulti(
                        new(() => task.IsCompleted, new(timeLimitMS: 5.Minutes())),
                        new(() => TaskMoveToHouse.UseSprint(false)),
                        new(() => P.FollowPath.Move([.. task.Result, .. appendMovement], true)),
                        new(() => P.FollowPath.Waypoints.Count == 0, new(timeLimitMS: 5.Minutes()))
                        );
                }, $"{Kind}: Pathfind and move");
                P.TaskManager.Enqueue(WaitForMoveEndOrOccupied, $"{Kind}: Wait until move ends/occupied");
                P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            }
        }
        else if(Kind == CustomAliasKind.Teleport_to_Aetheryte)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            P.TaskManager.Enqueue(() =>
            {
                // 別名裡的以太之光 ID 是設定檔回讀的，可能跨版本殘留或匯入自其他服務版本。
                // 裸 GetRow 查無此列時 Lumina 會擲例外，整條別名任務鏈會在沒有任何訊息的
                // 情況下斷掉；改成查不到就記一行使用者看得見的錯誤並跳過這一步。
                if(!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(Aetheryte, out var aetheryte))
                {
                    DuoLog.Error($"此別名的乙太之光（ID {Aetheryte}）已不存在，略過這一步。");
                    return;
                }
                var nearestAetheryte = Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.IsTargetable && x.IsAetheryte() && Utils.IsAetheryteEligibleForCustomAlias(x));
                if(nearestAetheryte == null || P.Territory != aetheryte.Territory.RowId || Player.DistanceTo(nearestAetheryte) > SkipTeleport)
                {
                    P.TaskManager.InsertMulti(
                        new((Action)(() => S.TeleportService.TeleportToAetheryte(Aetheryte))),
                        new(() => !IsScreenReady()),
                        new(() => IsScreenReady())
                        );
                }
            }, $"{Kind}: Teleport to aetheryte {Aetheryte}");
        }
        else if(Kind == CustomAliasKind.Use_Aethernet)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            P.TaskManager.Enqueue(() =>
            {
                P.TaskManager.InsertStack(() =>
                {
                    var aethernetPoint = Utils.GetAethernetNameWithOverrides(Aetheryte);
                    TaskTryTpToAethernetDestination.Enqueue(aethernetPoint);
                });
            }, $"{Kind}: Teleport to aethernet destination");
            P.TaskManager.Enqueue(() => !IsScreenReady(), $"{Kind}: Wait until screen is not ready");
            P.TaskManager.Enqueue(IsScreenReady, $"{Kind}: Wait until screen is ready");
        }
        else if(Kind == CustomAliasKind.Circular_movement)
        {
            P.TaskManager.Enqueue(() => Territory == 0 || Territory == Player.Territory, $"{Kind}: Wait for selected territory");
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false));
            P.TaskManager.Enqueue(() => P.FollowPath.Move([.. MathHelper.CalculateCircularMovement(CenterPoint, Player.Position.ToVector2(), CircularExitPoint.ToVector2(), out _, Precision, Tolerance, Clamp).Select(x => x.ToVector3(Player.Position.Y)).ToList(), .. (Vector3[])(WalkToExit ? [CircularExitPoint] : []), .. appendMovement], true));
            P.TaskManager.Enqueue(() => P.FollowPath.Waypoints.Count == 0, new(timeLimitMS: 5.Minutes()));
        }
        else if(Kind == CustomAliasKind.Interact)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, $"{Kind}: Wait for screen and player interactable");
            // 每次呼叫都重查物件表、不跨幀持有；NeoTasks 建任務時就會先呼叫一次（組任務名稱），角色不在時回 null。
            IGameObject selector() => Player.Available ? Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.IsTargetable && x.BaseId == DataID) : null;
            if(InteractDistance is > 0f)
            {
                P.TaskManager.EnqueueTask(NeoTasks.ApproachObjectViaAutomove(selector, InteractDistance.Value));
            }
            P.TaskManager.EnqueueTask(NeoTasks.InteractWithObject(selector));
        }
        else if(Kind == CustomAliasKind.Mount_Up)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.Enqueue(TaskMount.MountIfCan);
        }
        else if(Kind == CustomAliasKind.Select_Yes)
        {
            var miss = new OptionMismatchLog("SelectYesno");
            LogBlankParsedOptions(aliasName);
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m) && m.IsAddonReady)
                {
                    //PluginLog.Debug($"Parsed text: [{m.Text}], options: {SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern).Print("\n")}");
                    var text = m.Text;
                    // 讀到 U+FFFD ＝ 窗記憶體變動中(多半是上一個步驟按過、正在關閉),這一幀不碰。
                    if(AddonPressGuard.IsTextUnstable("SelectYesno", text)) return false;
                    var options = ParsedSelectOptions();
                    if(!text.ContainsAny(options))
                    {
                        miss.Report([text], options);
                    }
                    // 別名鏈可連續配置兩個 Select_Yes:下一步驟在下一 tick 就會掃到仍 IsAddonReady 的關閉中同址窗,
                    // 節流 key 帶步驟 ID 互不相干擋不到 ⇒ 由 AddonPressGuard 記位址,同一扇窗只按一次。
                    else if(EzThrottler.Throttle($"CustomCommandSelectYesno_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectYesno", m, $"CustomAlias.SelectYes({ID})"))
                    {
                        m.Yes();
                        return true;
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 10000));
        }
        else if(Kind == CustomAliasKind.Select_List_Option)
        {
            var stringMiss = new OptionMismatchLog("SelectString");
            var iconStringMiss = new OptionMismatchLog("SelectIconString");
            LogBlankParsedOptions(aliasName);
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                {
                    if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
                    {
                        if(AddonPressGuard.AnyTextUnstable("SelectString", m.Entries.Select(x => x.Text))) return false;
                        var options = ParsedSelectOptions();
                        if(!Utils.TryFindEqualsOrContains(m.Entries, e => e.Text, options, out var e))
                        {
                            stringMiss.Report(m.Entries.Select(x => x.Text), options);
                        }
                        else if(EzThrottler.Throttle($"CustomCommandSelectString_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectString", m, $"CustomAlias.SelectListOption({ID})", paramKey: e.Index.ToString()))
                        {
                            e.Select();
                            return true;
                        }
                    }
                }
                {
                    if(TryGetAddonMaster<AddonMaster.SelectIconString>(out var m) && m.IsAddonReady)
                    {
                        if(AddonPressGuard.AnyTextUnstable("SelectIconString", m.Entries.Select(x => x.Text))) return false;
                        var options = ParsedSelectOptions();
                        if(!Utils.TryFindEqualsOrContains(m.Entries, e => e.Text, options, out var e))
                        {
                            iconStringMiss.Report(m.Entries.Select(x => x.Text), options);
                        }
                        else if(EzThrottler.Throttle($"CustomCommandSelectString_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectIconString", m, $"CustomAlias.SelectListOption({ID})", paramKey: e.Index.ToString()))
                        {
                            e.Select();
                            return true;
                        }
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 10000));
        }
        else if(Kind == CustomAliasKind.Confirm_Contents_Finder)
        {
            P.TaskManager.Enqueue((Action)(() => EzThrottler.Throttle($"CustomCommandCFCConfirm_{ID}", 1000, true)));
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                if(TryGetAddonMaster<AddonMaster.ContentsFinderConfirm>(out var m) && m.IsAddonReady)
                {
                    if(EzThrottler.Throttle($"CustomCommandCFCConfirm_{ID}", 2000) && AddonPressGuard.TryPressOnce("ContentsFinderConfirm", m, $"CustomAlias.CFCConfirm({ID})"))
                    {
                        m.Commence();
                        return true;
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 20000));
        }
        else if(Kind == CustomAliasKind.Wait_for_Transition)
        {
            P.TaskManager.Enqueue(() =>
            {
                var territory = Svc.ClientState.TerritoryType;
                P.TaskManager.InsertStack(() =>
                {
                    P.TaskManager.Enqueue(() => !IsScreenReady(), $"{Kind}: Wait for screen not ready");
                    P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable && (!RequireTerritoryChange || Svc.ClientState.TerritoryType != territory), $"{Kind}: Wait for screen ready {ExcelTerritoryHelper.GetName(territory, true)}");
                });
            }, $"{Kind}: Wait for transition");
        }
    }

    public bool IsUsingTAForMovement() => UseTA && Svc.PluginInterface.InstalledPlugins.Any(x => x.Name == "TextAdvance" && x.IsLoaded);

    // 解析後是空白的選項不參與比對:空字串對任何確認框的 Contains 都成立、對清單第一項的 StartsWith 也成立。
    private string[] ParsedSelectOptions() => [.. SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern).Where(x => !string.IsNullOrWhiteSpace(x))];

    // 每次排入這一步各查一次;每幀的比對只拿 ParsedSelectOptions 的結果,不在這裡記。
    private void LogBlankParsedOptions(string aliasName)
    {
        foreach(var raw in SelectOption)
        {
            if(raw.Length == 0 || !string.IsNullOrWhiteSpace(Utils.ParseSheetPattern(raw))) continue;
            PluginLog.Information($"[CustomAlias] 別名「{aliasName ?? "?"}」的 {Kind} 選項「{raw}」解析後是空白，已略過（空白會比中任何選項與確認框）");
        }
    }

    /// <summary>
    /// 選項比對不中的 Information 診斷：同一組畫面文字持續比不中 2 秒才寫一行，
    /// 按完之後上一扇窗還在關的那幾幀不會被記。只在框架執行緒使用，每次排入步驟各一份。
    /// </summary>
    private sealed class OptionMismatchLog(string addonName)
    {
        private string shownKey;
        private long shownSince;
        private bool logged;

        public void Report(IEnumerable<string> shown, string[] options)
        {
            var key = string.Join("、", shown.Select(x => $"\"{x}\""));
            if(key != shownKey)
            {
                shownKey = key;
                shownSince = Environment.TickCount64;
                logged = false;
                return;
            }
            if(logged || Environment.TickCount64 - shownSince < 2000) return;
            logged = true;
            PluginLog.Information($"[CustomAlias] {addonName} 的選項對不上（已持續 2 秒）：畫面上＝{key}；要找的＝{string.Join("、", options.Select(x => $"\"{x}\""))}");
        }
    }

    /// <summary>
    /// 這一步在目前角色身上做不做得到。只用來提示（編輯器標示、執行時警告），不擋下別名、不改資料；
    /// 角色不在等判斷不了的情況一律回 true。「傳送到乙太之光」刻意不檢查，與上游現行版本一致。
    /// </summary>
    /// <param name="error">做不到時的原因（已在地化）；做得到時為 null。</param>
    public unsafe bool CanExecute(out string error)
    {
        error = null;
        if(!Player.Available) return true;
        if(Kind == CustomAliasKind.Use_Aethernet)
        {
            if(Aetheryte == TaskAetheryteAethernetTeleport.FirmamentAethernetId)
            {
                // 蒼天街由任務「仰望蒼天之街」(69208) 開放。
                var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
                if(ui != null && !ui->IsUnlockLinkUnlockedOrQuestCompleted(69208))
                {
                    error = "The Firmament is not unlocked yet".Loc();
                    return false;
                }
            }
            else if(S.Data.DataStore.Aetherytes.Values.Any(x => x.Any(s => s.ID == Aetheryte)))
            {
                if(!TaskGotoDestination.IsAetheryteUnlocked(Aetheryte))
                {
                    error = "Aethernet shard ?? is not attuned yet".Loc(Utils.KnownAetherytes.SafeSelect(Aetheryte, Aetheryte.ToString()));
                    return false;
                }
            }
        }
        else if(Kind == CustomAliasKind.Change_world)
        {
            if(World == 0)
            {
                error = "No world selected; this step will be skipped".Loc();
                return false;
            }
            var name = ExcelWorldHelper.GetName(World);
            if(World != Player.Object.CurrentWorld.RowId && !S.Data.DataStore.Worlds.Contains(name) && !S.Data.DataStore.DCWorlds.Contains(name))
            {
                error = "Destination ?? can not be visited from ??; this step will be skipped".Loc(name, Player.CurrentWorld);
                return false;
            }
        }
        return true;
    }

    // 有額外座標時每次執行在主座標與額外座標中隨機挑一個；散布範圍對每個點都一樣。
    internal Vector3 PickPoint() => ExtraPoints is { Count: > 0 } ? GenericHelpers.GetRandom<Vector3>([Point, .. ExtraPoints]) : Point;

    // 角色被事件佔用（Occupied33）或已離開限定區域時停止跟隨路徑，避免繼續推著角色走。
    private bool WaitForMoveEndOrOccupied()
    {
        if(Svc.Condition[ConditionFlag.Occupied33] || (Territory != 0 && Territory != Player.Territory))
        {
            P.FollowPath.Stop();
        }
        return P.FollowPath.Waypoints.Count == 0;
    }
}
