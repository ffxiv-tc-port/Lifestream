using ECommons;
using ECommons.EzIpcManager;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using Lifestream.GUI.Windows;
using Lifestream.Tasks;
using Lifestream.Tasks.Login;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;

namespace Lifestream.IPC;
public class IPCProvider
{
    private IPCProvider()
    {
        ECommonsMain.ReducedLogging = true;
        EzIPC.Init(this);
        ECommonsMain.ReducedLogging = false;
    }

    [EzIPC]
    public IDalamudPlugin Instance()
    {
        return P;
    }

    [EzIPC]
    public void ExecuteCommand(string arguments)
    {
        // 這是別的外掛在驅動 Lifestream，不是使用者主動下的 /li ——
        // 走內層，不掛「抵達提醒」哨兵。
        P.ProcessCommandInternal("/li", arguments);
    }

    [EzIPC]
    public AddressBookEntryTuple BuildAddressBookEntry(string worldStr, string cityStr, string wardNum, string plotApartmentNum, bool isApartment, bool isSubdivision)
    {
        return Utils.BuildAddressBookEntry(worldStr, cityStr, wardNum, plotApartmentNum, isApartment, isSubdivision).AsTuple();
    }

    [EzIPC]
    public bool IsHere(AddressBookEntryTuple addressBookEntryTuple)
    {
        return Utils.IsHere(AddressBookEntry.FromTuple(addressBookEntryTuple));
    }

    [EzIPC]
    public bool IsQuickTravelAvailable(AddressBookEntryTuple addressBookEntryTuple)
    {
        return Utils.IsQuickTravelAvailable(AddressBookEntry.FromTuple(addressBookEntryTuple));
    }

    [EzIPC]
    public void GoToHousingAddress(AddressBookEntryTuple addressBookEntryTuple)
    {
        AddressBookEntry.FromTuple(addressBookEntryTuple).GoTo();
    }

    [EzIPC]
    public bool IsBusy()
    {
        return P.TaskManager.IsBusy || (P.followPath != null && P.followPath.Waypoints.Count > 0);
    }

    [EzIPC]
    public void Abort()
    {
        P.TaskManager.Abort();
        P.followPath?.Stop();
    }

    /// <summary>
    /// 前往「地圖上的一個點」:自動判斷跨不跨區、選最近的乙太之光傳送過去,再由 vnavmesh
    /// 走(或飛)到那個點。編排與 <c>/li &lt;自訂落點&gt;</c> 共用同一條鏈。
    ///
    /// 📌 端點名 <c>Lifestream.GoToMapPoint</c>。要中止請用既有的 <c>Lifestream.Abort</c>;
    ///    要問還在不在跑用 <c>Lifestream.IsBusy</c>。
    /// 🔴 參數順序與型別是對外契約,已有消費端(Mappy 地圖右鍵的「移動到這裡」)照此接線,不要改。
    /// </summary>
    /// <param name="territory">目標區域的 TerritoryType row id。</param>
    /// <param name="worldX">目標點的世界座標 X。</param>
    /// <param name="worldZ">目標點的世界座標 Z。**不需要 Y** —— 抵達之後由 Lifestream
    /// 向 vnavmesh 問這個 XZ 底下的地板高度。</param>
    /// <param name="fly">允許使用飛行坐騎跑最後一段。區域不可飛、或起飛失敗時會自動退回
    /// 地面路線,不會因此失敗。</param>
    /// <returns>
    /// true = 已經排進佇列(呼叫端可用 <c>IsBusy</c> 追蹤)。
    /// false = **什麼都沒排**,呼叫端不要等。發生於:區域 id 為 0、Lifestream 正在忙、
    /// 角色不可互動(讀取中/過場/未登入)、vnavmesh 沒安裝或沒載入,
    /// 或目標區域沒有任何已解鎖的乙太之光可用(最後這項會另外在聊天欄說明原因)。
    /// </returns>
    [EzIPC]
    public bool GoToMapPoint(uint territory, float worldX, float worldZ, bool fly)
    {
        if(territory == 0) return false;
        if(IsBusy()) return false;
        if(!Player.Interactable) return false;
        // 這個功能整段都靠 vnavmesh(解地板高度 + 尋路),沒有它連「插旗請你自己走」都做不好
        // (我們只有 XZ,多層地圖插的旗會落在錯的樓層)。直接拒絕比排一條走不完的佇列誠實。
        if(!Tasks.Utility.TaskGotoDestination.IsVnavmeshLoaded()) return false;
        // 抵達提醒哨兵：這條 IPC 的來源是使用者在地圖上右鍵點的一下（Mappy），
        // 語意上就是使用者主動下的指令，所以跟 /li 一樣要出聲（2026-08-31 使用者回報）。
        // 哨兵自帶「設定開關/有沒有真的排任務/去重」判斷，失敗路徑（回 false 什麼都沒排）不會響。
        var queuedBefore = P.TaskManager.NumQueuedTasks;
        var queued = Tasks.Utility.TaskGotoDestination.EnqueueToMapPoint(new()
        {
            Name = "the point on the map".Loc(),
            Territory = territory,
            WorldX = worldX,
            WorldZ = worldZ,
        }, fly);
        if(queued) Tasks.Utility.TaskAnnounceArrival.EnqueueIfChainStarted(queuedBefore);
        return queued;
    }

    [EzIPC]
    public bool CanVisitSameDC(string world)
    {
        return S.Data.DataStore.Worlds.Contains(world);
    }

    [EzIPC]
    public bool CanVisitCrossDC(string world)
    {
        return S.Data.DataStore.DCWorlds.Contains(world);
    }

    [EzIPC]
    public void TPAndChangeWorld(string w, bool isDcTransfer, string secondaryTeleport, bool noSecondaryTeleport, int? gateway, bool? doNotify, bool? returnToGateway)
    {
        P.TPAndChangeWorld(w, isDcTransfer, secondaryTeleport, noSecondaryTeleport, (WorldChangeAetheryte?)gateway, doNotify, returnToGateway);
    }

    [EzIPC]
    public int? GetWorldChangeAetheryteByTerritoryType(uint territoryType)
    {
        return (int)Utils.GetWorldChangeAetheryteByTerritoryType(territoryType);
    }

    [EzIPC]
    public bool ChangeWorld(string world)
    {
        if(IsBusy()) return false;
        if(CanVisitCrossDC(world))
        {
            P.TPAndChangeWorld(world, true);
            return true;
        }
        else if(CanVisitSameDC(world))
        {
            P.TPAndChangeWorld(world, false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Requests Lifestream to change world of current character to a different one.
    /// </summary>
    /// <param name="worldId"></param>
    /// <returns></returns>
    [EzIPC]
    public bool ChangeWorldById(uint worldId)
    {
        if(Svc.Data.GetExcelSheet<World>().TryGetRow(worldId, out var sheet))
        {
            return ChangeWorld(sheet.Name.GetText());
        }
        return false;
    }

    /// <summary>
    /// Requests aethernet teleport to be executed by name, if possible. Must be within an aetheryte or aetheryte shard range.
    /// </summary>
    /// <param name="destination"></param>
    /// <returns></returns>
    [EzIPC]
    public bool AethernetTeleport(string destination)
    {
        if(IsBusy()) return false;
        TaskTryTpToAethernetDestination.Enqueue(destination);
        return true;
    }

    /// <summary>
    /// Requests aethernet teleport to be executed by Place Name ID from <see cref="PlaceName"/> sheet, if possible. Must be within an aetheryte or aetheryte shard range. 
    /// </summary>
    /// <param name="placeNameRowId"></param>
    /// <returns></returns>
    [EzIPC]
    public bool AethernetTeleportByPlaceNameId(uint placeNameRowId)
    {
        if(Svc.Data.GetExcelSheet<PlaceName>().TryGetRow(placeNameRowId, out var row))
        {
            return AethernetTeleport(row.Name.GetText());
        }
        return false;
    }

    /// <summary>
    /// Requests aethernet teleport to be executed by ID from <see cref="Aetheryte"/> sheet, if possible. Must be within an aetheryte or aetheryte shard range. 
    /// </summary>
    /// <param name="aethernetSheetRowId"></param>
    /// <returns></returns>
    [EzIPC]
    public bool AethernetTeleportById(uint aethernetSheetRowId)
    {
        var name = Utils.GetAethernetNameWithOverrides(aethernetSheetRowId);
        if(name == null) return false;
        return AethernetTeleport(name);
    }

    /// <summary>
    /// Requests aethernet teleport to be executed by ID from <see cref="HousingAethernet"/> sheet, if possible. Must be within an aetheryte shard range. 
    /// </summary>
    /// <returns></returns>
    [EzIPC]
    public bool HousingAethernetTeleportById(uint housingAethernetSheetRow)
    {
        if(Svc.Data.GetExcelSheet<HousingAethernet>().TryGetRow(housingAethernetSheetRow, out var row))
        {
            return AethernetTeleport(row.PlaceName.Value.Name.GetText());
        }
        return false;
    }

    /// <summary>
    /// Requests aethernet teleport to Firmament. Must be within a Foundation aetheryte range. 
    /// </summary>
    /// <returns></returns>
    [EzIPC]
    public bool AethernetTeleportToFirmament()
    {
        return AethernetTeleport(Utils.GetAethernetNameWithOverrides(TaskAetheryteAethernetTeleport.FirmamentAethernetId));
    }

    /// <summary>
    /// Retrieves active aetheryte/aetheryte shard ID if present
    /// </summary>
    /// <returns></returns>
    [EzIPC]
    public uint GetActiveAetheryte()
    {
        if(P.ActiveAetheryte != null)
        {
            return P.ActiveAetheryte.Value.ID;
        }
        return 0;
    }

    /// <summary>
    /// Retrieves active custom aetheryte ID if present
    /// </summary>
    /// <returns></returns>
    [EzIPC]
    public uint GetActiveCustomAetheryte()
    {
        if(S.Data.CustomAethernet.ActiveAetheryte != null)
        {
            return S.Data.CustomAethernet.ActiveAetheryte.Value.ID;
        }
        return 0;
    }

    /// <summary>
    /// Retrieves active housing aetheryte shard ID if present
    /// </summary>
    /// <returns></returns>
    [EzIPC]
    public uint GetActiveResidentialAetheryte()
    {
        if(S.Data.ResidentialAethernet.ActiveAetheryte != null)
        {
            return S.Data.ResidentialAethernet.ActiveAetheryte.Value.ID;
        }
        return 0;
    }

    [EzIPC]
    public bool Teleport(uint destination, byte subIndex)
    {
        return S.TeleportService.TeleportToAetheryte(destination, subIndex);
    }

    [EzIPC]
    public bool TeleportToFC()
    {
        if(!P.TaskManager.IsBusy)
        {
            TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.FC);
            return true;
        }
        return false;
    }

    [EzIPC]
    public bool TeleportToHome()
    {
        if(!P.TaskManager.IsBusy)
        {
            TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.Home);
            return true;
        }
        return false;
    }

    [EzIPC]
    public bool TeleportToApartment()
    {
        if(!P.TaskManager.IsBusy)
        {
            TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.Apartment);
            return true;
        }
        return false;
    }

    [EzIPC]
    public (HousePathData Private, HousePathData FC) GetHousePathData(ulong CID)
    {
        return (Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == CID && x.IsPrivate), Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == CID && !x.IsPrivate));
    }

    [EzIPC]
    public uint GetResidentialTerritory(ResidentialAetheryteKind r)
    {
        return r.GetResidentialTerritory();
    }

    [EzIPC]
    public Vector3? GetPlotEntrance(uint territory, int plot)
    {
        return Utils.GetPlotEntrance(territory, plot);
    }

    [EzIPC]
    public void EnqueuePropertyShortcut(TaskPropertyShortcut.PropertyType type, HouseEnterMode? mode)
    {
        TaskPropertyShortcut.Enqueue(type, mode);
    }

    [EzIPC]
    public void EnterApartment(bool enter)
    {
        TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.Apartment, null, null, enter);
    }

    [EzIPC]
    public void EnqueueInnShortcut(int? innIndex)
    {
        TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.Inn, default, innIndex);
    }

    [EzIPC]
    public void EnqueueLocalInnShortcut(int? innIndex)
    {
        TaskPropertyShortcut.Enqueue(TaskPropertyShortcut.PropertyType.Inn, default, innIndex, useSameWorld: true);
    }

    [EzIPC]
    public (ResidentialAetheryteKind Kind, int Ward, int Plot)? GetCurrentPlotInfo()
    {
        if(UIHouseReg.TryGetCurrentPlotInfo(out var kind, out var ward, out var plot))
        {
            return (kind, ward, plot);
        }
        return null;
    }

    [EzIPC]
    public bool CanChangeInstance()
    {
        return S.InstanceHandler.CanChangeInstance();
    }

    [EzIPC]
    public int GetNumberOfInstances()
    {
        return S.InstanceHandler.InstancesInitizliaed(out var ret) ? ret : 0;
    }

    [EzIPC]
    public void ChangeInstance(int number)
    {
        TaskRemoveAfkStatus.Enqueue();
        TaskChangeInstance.Enqueue(number);
    }

    [EzIPC]
    public int GetCurrentInstance()
    {
        return S.InstanceHandler.GetInstance();
    }

    [EzIPC]
    public bool? HasApartment()
    {
        if(Player.Object.HomeWorld.RowId != Player.Object.CurrentWorld.RowId) return null;
        return TaskPropertyShortcut.GetApartmentAetheryteID().ID != 0;
    }

    [EzIPC]
    public bool? HasPrivateHouse()
    {
        if(Player.Object.HomeWorld.RowId != Player.Object.CurrentWorld.RowId) return null;
        return TaskPropertyShortcut.GetPrivateHouseAetheryteID() != 0;
    }

    [EzIPC]
    public bool? HasFreeCompanyHouse()
    {
        if(Player.Object.HomeWorld.RowId != Player.Object.CurrentWorld.RowId) return null;
        return TaskPropertyShortcut.GetFreeCompanyAetheryteID() != 0;
    }

    [EzIPC]
    public void Move(List<Vector3> path)
    {
        P.FollowPath.Move(path, true);
    }

    [EzIPC]
    public bool CanMoveToWorkshop()
    {
        var data = Utils.GetFCPathData();
        if(data == null) return false;
        var plotDataAvailable = UIHouseReg.TryGetCurrentPlotInfo(out var kind, out var ward, out var plot);
        if(plotDataAvailable)
        {
            return data.PathToWorkshop.Count > 0 && data.ResidentialDistrict == kind && data.Ward == ward && data.Plot == plot;
        }
        return false;
    }

    [EzIPC]
    public void MoveToWorkshop()
    {
        if(IsBusy()) return;
        var data = Utils.GetFCPathData();
        if(data == null) return;
        var plotDataAvailable = UIHouseReg.TryGetCurrentPlotInfo(out var kind, out var ward, out var plot);
        if(plotDataAvailable && data.PathToWorkshop.Count > 0 && data.PathToWorkshop.Count > 0 && data.ResidentialDistrict == kind && data.Ward == ward && data.Plot == plot)
        {
            P.FollowPath.Move(data.PathToWorkshop, true);
        }
    }

    [EzIPC]
    public uint GetRealTerritoryType()
    {
        return P.Territory;
    }

    [EzIPC]
    public bool CanAutoLogin() => Utils.CanAutoLogin();

    [EzIPC]
    public bool ConnectAndOpenCharaSelect(string charaName, string charaHomeWorld)
    {
        if(IsBusy())
        {
            return false;
        }
        return TaskConnectAndOpenCharaSelect.Enqueue(charaName, charaHomeWorld);
    }

    [EzIPC]
    public bool InitiateTravelFromCharaSelectScreen(string charaName, string charaHomeWorld, string destination, bool noLogin)
    {
        if(IsBusy())
        {
            return false;
        }
        return IpcUtils.InitiateTravelFromCharaSelectScreenInternal(charaName, charaHomeWorld, destination, noLogin);
    }

    [EzIPC]
    public bool CanInitiateTravelFromCharaSelectList()
    {
        return CharaSelectOverlay.TryGetValidCharaSelectListMenu(out var m);
    }

    [EzIPC]
    public bool ConnectAndTravel(string charaName, string charaHomeWorld, string destination, bool noLogin)
    {
        if(IsBusy() || !CanAutoLogin())
        {
            return false;
        }
        ConnectAndOpenCharaSelect(charaName, charaHomeWorld);
        P.TaskManager.Enqueue(() => IpcUtils.InitiateTravelFromCharaSelectScreenInternal(charaName, charaHomeWorld, destination, noLogin));
        return true;
    }

    #region Teleport panel favorites

    // 讓別的外掛把「使用者已經在傳送面板收藏好的地點」直接當成導航目標。
    // 🔴 這比讓呼叫端自己組路線安全得多:收藏項是**既知的乙太之光/乙太網點**,走的是面板按鈕
    //    本來就在走的那條路(TaskTeleportPanelGo),不會出現自組跨區路線跑到別的城市那種事。

    /// <summary>Teleport panel entries the user has starred, in the panel's own order.</summary>
    /// <returns>(Id, SubIndex, DisplayName, Territory) for each favourite. DisplayName already honours the
    /// user's rename. Id+SubIndex together identify an entry - the same aetheryte id can appear more than
    /// once (housing sub-indices), so callers must keep both. Empty when nothing is starred.</returns>
    [EzIPC]
    public List<(uint Id, byte SubIndex, string Name, uint Territory)> GetTeleportFavorites()
    {
        var result = new List<(uint, byte, string, uint)>();
        foreach(var x in Systems.TeleportPanel.TeleportPanelIndex.Get())
        {
            if(!C.Favorites.Contains(x.Id)) continue;
            result.Add((x.Id, x.SubIndex, x.DisplayName, x.Territory));
        }
        return result;
    }

    /// <summary>Travels to a starred teleport panel entry, exactly as clicking it in the favourites window does.</summary>
    /// <returns>False when the entry is not a current favourite, or travelling cannot start right now
    /// (Lifestream busy, no interactable player) - in that case nothing was queued, so the caller should
    /// stop rather than wait for a completion that will never come.</returns>
    [EzIPC]
    public bool TeleportToFavorite(uint id, byte subIndex)
    {
        if(!C.Favorites.Contains(id)) return false;
        if(P.TaskManager.IsBusy) return false;
        if(!Player.Interactable) return false;

        foreach(var x in Systems.TeleportPanel.TeleportPanelIndex.Get())
        {
            if(x.Id != id || x.SubIndex != subIndex) continue;
            Tasks.Utility.TaskTeleportPanelGo.Enqueue(x);
            return true;
        }
        return false;
    }

    #endregion

    [EzIPCEvent] public System.Action OnHouseEnterError;
}
