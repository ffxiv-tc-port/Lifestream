using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.Game;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.Shortcuts;
using Callback = ECommons.Automation.Callback;

namespace Lifestream.Tasks.SameWorld;

/// <summary>
/// 經好友名單的「房屋傳送」：呼叫遊戲自己的 <c>AgentFriendlist.OpenFriendEstateTeleportation</c>（＝好友名單右鍵那一項），
/// 在「好友房屋傳送」視窗挑一格按下，抵達後照既有房屋流程走到門口或進門。
/// </summary>
/// <remarks>
/// 契約：好友只以 ContentId 跨任務傳遞，每一步現查；角色名只給畫面與通知用，log 只寫遮罩後的名字。
/// 傳送券確認框刻意不按：用券還是付 Gil 由使用者決定（本插件執行中 YesAlready 已暫停）。
/// </remarks>
public static unsafe class TaskFriendEstateTeleport
{
    public readonly record struct FriendRef(ulong ContentId, string Name, uint HomeWorld);

    private static readonly FriendEstateKind[] AutoOrder = [FriendEstateKind.Private, FriendEstateKind.Apartment, FriendEstateKind.FreeCompany];

    /// <summary>按下之後到開始傳送，最多等這麼久沒任何動靜（沒在詠唱、沒有確認框、清單已關）就當作沒出發。</summary>
    private const long NoDepartureGraceMs = 10_000;

    private static FriendEstateEntry? Chosen;
    private static long PressedAt;

    public static bool IsSupported => AgentFriendlist.MemberFunctionPointers.OpenFriendEstateTeleportation != null;

    public static string KindName(FriendEstateKind? kind) => kind switch
    {
        FriendEstateKind.Private => "Private estate".Loc(),
        FriendEstateKind.FreeCompany => "Free company estate".Loc(),
        FriendEstateKind.Apartment => "Apartment".Loc(),
        _ => "Automatic".Loc(),
    };

    /// <summary>log 用：只留第一個字。</summary>
    public static string MaskName(string name) => string.IsNullOrEmpty(name) ? "?" : $"{name[0]}…";

    /// <summary>每次呼叫現讀好友名單並複製成值；名單沒載入回空清單。</summary>
    public static List<FriendRef> SnapshotFriends()
    {
        var list = new List<FriendRef>();
        var proxy = InfoProxyFriendList.Instance();
        if(proxy == null) return list;
        var common = (InfoProxyCommonList*)proxy;
        if(common->CharData == null) return list;
        foreach(var x in common->CharDataSpan)
        {
            if(x.ContentId == 0) continue;
            list.Add(new(x.ContentId, x.NameString, x.HomeWorld));
        }
        return list;
    }

    /// <summary>完全相同 → 開頭相同 → 包含；同一層有多位符合時，只有剛好一位在目前世界才採用，否則要使用者打完整一點。</summary>
    public static bool TryFindFriend(string query, out FriendRef friend, out string error)
    {
        friend = default;
        error = null;
        var all = SnapshotFriends();
        if(all.Count == 0)
        {
            error = "Your friend list is not loaded. Open the in-game friend list once and try again.".Loc();
            return false;
        }
        Func<FriendRef, bool>[] levels =
        [
            x => x.Name.Equals(query, StringComparison.OrdinalIgnoreCase),
            x => x.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
        ];
        foreach(var level in levels)
        {
            var hits = all.Where(level).ToList();
            if(hits.Count == 0) continue;
            if(hits.Count == 1)
            {
                friend = hits[0];
                return true;
            }
            var sameWorld = hits.Where(x => x.HomeWorld == Player.CurrentWorldId).ToList();
            if(sameWorld.Count == 1)
            {
                friend = sameWorld[0];
                return true;
            }
            error = "?? friends match ??. Please type more of the name.".Loc(hits.Count, query);
            return false;
        }
        error = "No friend matches ??.".Loc(query);
        return false;
    }

    public static void Enqueue(string query, FriendEstateKind? kind)
    {
        query = query?.Trim() ?? "";
        if(query == "")
        {
            Notify.Error("Please type a friend's name.".Loc());
            return;
        }
        if(!Player.Available) return;
        if(!TryFindFriend(query, out var friend, out var error))
        {
            Notify.Error(error);
            return;
        }
        Enqueue(friend, kind);
    }

    public static void Enqueue(FriendRef friend, FriendEstateKind? kind)
    {
        if(P.TaskManager.IsBusy)
        {
            DuoLog.Error("Lifestream is busy");
            return;
        }
        if(!Player.Available) return;
        if(!IsSupported)
        {
            Notify.Error("Teleporting to a friend's estate is not available in this game version.".Loc());
            return;
        }
        Chosen = null;
        var cid = friend.ContentId;
        PluginLog.Information($"[FriendEstate] 開始：好友 {MaskName(friend.Name)}，原始伺服器 {friend.HomeWorld}，目標 {kind?.ToString() ?? "Auto"}");
        if(friend.HomeWorld != Player.CurrentWorldId && !EnqueueWorldTravel(friend.HomeWorld)) return;
        P.TaskManager.Enqueue(() => Player.Interactable && IsScreenReady(), "FriendEstate.WaitReady", TaskSettings.Timeout1M);
        P.TaskManager.Enqueue(CloseStaleList, "FriendEstate.CloseStaleList");
        P.TaskManager.Enqueue(() => Request(cid), "FriendEstate.Request", new(timeLimitMS: 15_000));
        P.TaskManager.Enqueue(() => ChooseAndPress(cid, kind), "FriendEstate.Choose", new(timeLimitMS: 15_000));
        P.TaskManager.Enqueue(WaitDeparture, "FriendEstate.WaitDeparture", TaskSettings.Timeout2M);
        P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, "FriendEstate.WaitArrival", TaskSettings.Timeout2M);
        P.TaskManager.Enqueue(AfterArrival, "FriendEstate.AfterArrival");
    }

    /// <summary>房屋只在好友的原始伺服器上；先跨界過去。回 false＝沒排成（原因已顯示）。</summary>
    private static bool EnqueueWorldTravel(uint homeWorld)
    {
        var worldName = ExcelWorldHelper.GetName(homeWorld);
        if(string.IsNullOrEmpty(worldName) || PublicWorlds.IsUnavailable(homeWorld))
        {
            Notify.Error("Can not travel to your friend's home world.".Loc());
            return false;
        }
        var before = P.TaskManager.NumQueuedTasks;
        if(S.Data.DataStore.Worlds.Contains(worldName))
        {
            P.TPAndChangeWorld(worldName, false, null, true, null, false, false);
        }
        else if(S.Data.DataStore.DCWorlds.Contains(worldName))
        {
            P.TPAndChangeWorld(worldName, true, null, true, null, false, false);
        }
        // 沒排出任務＝TPAndChangeWorld 已自己通知原因（設定不允許跨資料中心等）。
        if(P.TaskManager.NumQueuedTasks == before)
        {
            PluginLog.Information($"[FriendEstate] 沒有排出前往 {homeWorld} 的跨界傳送，停止");
            return false;
        }
        P.TaskManager.Enqueue(() => Player.Interactable && Player.CurrentWorldId == homeWorld && IsScreenReady(), "FriendEstate.WaitHomeWorld", TaskSettings.Timeout5M);
        return true;
    }

    /// <summary>已開著的清單是上一次請求的資料；關掉重開才能用「讀取中 → 有筆數」判斷這次的回應到了。</summary>
    private static bool? CloseStaleList()
    {
        if(!TryGetAddonByName<AtkUnitBase>(FriendEstateList.AddonName, out var addon) || !addon->IsVisible) return true;
        if(!IsAddonReady(addon)) return false;
        if(AddonPressGuard.TryPressOnce(FriendEstateList.AddonName, addon, "FriendEstate.CloseStale"))
        {
            Callback.Fire(addon, true, -1);
        }
        return false;
    }

    /// <summary>剛跨界時好友名單可能還沒回來：等到名單裡有這位好友才送請求，逾時交給任務設定。</summary>
    private static bool? Request(ulong cid)
    {
        if(!SnapshotFriends().Any(x => x.ContentId == cid)) return false;
        var agent = AgentFriendlist.Instance();
        if(agent == null) return false;
        agent->OpenFriendEstateTeleportation(cid);
        return true;
    }

    private static bool? ChooseAndPress(ulong cid, FriendEstateKind? wanted)
    {
        if(!TryGetAddonByName<AtkUnitBase>(FriendEstateList.AddonName, out var addon) || !IsAddonReady(addon)) return false;
        var state = FriendEstateList.TryRead(addon, cid, out var entries, out var diag);
        if(state == FriendEstateReadState.Loading) return false;
        // 失敗時清單視窗留著：使用者可以直接在遊戲視窗上自己點。
        if(state == FriendEstateReadState.Mismatch)
        {
            PluginLog.Information($"[FriendEstate] 清單內容和預期的布局對不上，停止：{diag}");
            Notify.Error("Could not read the friend's estate list. Please choose in the game window.".Loc());
            return null;
        }
        if(entries.Count == 0)
        {
            PluginLog.Information("[FriendEstate] 好友沒有可傳送的房屋");
            Notify.Error("Your friend has no estate you can teleport to.".Loc());
            return null;
        }
        FriendEstateEntry? pick = null;
        var order = wanted == null ? AutoOrder : new[] { wanted.Value };
        foreach(var k in order)
        {
            if(entries.TryGetFirst(x => x.Kind == k, out var e))
            {
                pick = e;
                break;
            }
        }
        if(pick == null)
        {
            PluginLog.Information($"[FriendEstate] 沒有 {wanted}，可傳送：{entries.Select(x => x.Kind).Print()}");
            Notify.Error("Your friend has no ??. Available: ??".Loc(KindName(wanted), entries.Select(x => KindName(x.Kind)).Print("、")));
            return null;
        }
        if(!AddonPressGuard.TryPressOnce(FriendEstateList.AddonName, addon, "FriendEstate.Select")) return false;
        var p = pick.Value;
        Callback.Fire(addon, true, (int)p.Kind);
        Chosen = p;
        PressedAt = Environment.TickCount64;
        PluginLog.Information($"[FriendEstate] 選擇 {p.Kind}：區域 {p.House.TerritoryTypeId}、第 {p.House.WardIndex + 1} 區、{(p.Kind == FriendEstateKind.Apartment ? $"房間 {p.House.RoomNumber}" : $"{p.House.PlotIndex + 1} 號")}、費用 {p.Cost}、可進門 {p.CanEnter}");
        Utils.DisplayInfo("Teleporting to your friend's ??".Loc(KindName(p.Kind)));
        return true;
    }

    private static bool? WaitDeparture()
    {
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return true;
        if(Environment.TickCount64 - PressedAt < NoDepartureGraceMs) return false;
        if(Player.IsCasting) return false;
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && yesno->IsVisible) return false;
        if(TryGetAddonByName<AtkUnitBase>(FriendEstateList.AddonName, out var list) && list->IsVisible) return false;
        PluginLog.Information("[FriendEstate] 按下後沒有開始傳送，停止");
        Notify.Error("The teleport did not start.".Loc());
        Chosen = null;
        return null;
    }

    private static void AfterArrival()
    {
        if(Chosen is not { } e) return;
        Chosen = null;
        if(P.Territory != e.House.TerritoryTypeId)
        {
            PluginLog.Information($"[FriendEstate] 抵達區域 {P.Territory} 不是房屋所在的 {e.House.TerritoryTypeId}，不再往下走");
            return;
        }
        if(e.Kind == FriendEstateKind.Apartment)
        {
            if(!e.CanEnter)
            {
                Utils.DisplayInfo("Your friend's apartment is not open to visitors; stopping at the entrance.".Loc());
                return;
            }
            // RoomNumber 與遊戲清單上顯示的房號相同（從 1 起算）；SelectApartment 吃從 0 起算。
            var room = e.House.RoomNumber - 1;
            P.TaskManager.InsertStack(() =>
            {
                TaskApproachAndInteractWithApartmentEntrance.Enqueue(false);
                P.TaskManager.Enqueue(TaskTpAndGoToWard.SelectGoToSpecifiedApartment, "FriendEstate.SelectGoToSpecifiedApartment");
                P.TaskManager.Enqueue(() => TaskTpAndGoToWard.SelectApartment(room), "FriendEstate.SelectApartment");
                if(!C.AddressApartmentNoEntry) P.TaskManager.Enqueue(TaskTpAndGoToWard.ConfirmApartmentEnterYesno, "FriendEstate.ConfirmApartmentEnter");
            });
            return;
        }
        var district = Utils.GetResidentialAetheryteByTerritoryType(e.House.TerritoryTypeId);
        // 沒登記過這塊地的路線＝停在傳送抵達點（房屋門前），和遊戲手動傳送一樣。
        var data = district == null ? null : Utils.GetCustomPathData(district.Value, e.House.WardIndex, e.House.PlotIndex);
        if(data == null) return;
        var mode = data.GetHouseEnterMode();
        if(mode == HouseEnterMode.Enter_house && !e.CanEnter) mode = HouseEnterMode.Walk_to_door;
        TaskPropertyShortcut.ExecuteTpAndPathfind(0, 0, data, mode, teleport: false);
    }
}
