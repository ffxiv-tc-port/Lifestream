using ECommons.EzIpcManager;
using ECommons.GameHelpers;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;

namespace Lifestream.IPC;

// 舊的傳送端點在「什麼都沒排」時也可能回 true（例：已站在城內乙太之晶旁、找不到房子、已在目標世界），
// 呼叫端因此乾等到逾時。舊端點不改型別也不改語意（已有消費端照舊簽章接線），另開同名加 V2 的端點：
// 動作與舊端點相同，回 IpcQueueResult 的 int 值。全部走 IpcFrameworkGate，等不到主執行緒回 Unavailable(6)。
public partial class IPCProvider
{
    /// <summary>同 <see cref="TeleportToFavorite"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int TeleportToFavoriteV2(uint id, byte subIndex)
        => RunV2(nameof(TeleportToFavoriteV2), $"{id}, {subIndex}", () => TeleportToFavoriteV2Core(id, subIndex));

    /// <summary>同 <see cref="AethernetTeleport"/>（必須已站在乙太之光範圍內）；回 <see cref="IpcQueueResult"/> 的 int 值。
    /// Queued 只代表排入；目的地在排程途中才發現找不到時，佇列會自己停（<c>IsBusy</c> 轉 false）。</summary>
    [EzIPC]
    public int AethernetTeleportV2(string destination)
        => RunV2(nameof(AethernetTeleportV2), destination ?? "null", () => AethernetTeleportV2Core(destination));

    /// <summary>同 <see cref="AethernetTeleportByPlaceNameId"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int AethernetTeleportByPlaceNameIdV2(uint placeNameRowId)
        => RunV2(nameof(AethernetTeleportByPlaceNameIdV2), $"{placeNameRowId}", () =>
            Svc.Data.GetExcelSheet<PlaceName>().TryGetRow(placeNameRowId, out var row)
                ? AethernetTeleportV2Core(row.Name.GetText())
                : (IpcQueueResult.NotFound, "PlaceName 表查不到這個 id"));

    /// <summary>同 <see cref="AethernetTeleportById"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int AethernetTeleportByIdV2(uint aethernetSheetRowId)
        => RunV2(nameof(AethernetTeleportByIdV2), $"{aethernetSheetRowId}", () =>
            Utils.GetAethernetNameWithOverrides(aethernetSheetRowId) is { } name
                ? AethernetTeleportV2Core(name)
                : (IpcQueueResult.NotFound, "Aetheryte 表查不到這個 id"));

    /// <summary>同 <see cref="HousingAethernetTeleportById"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int HousingAethernetTeleportByIdV2(uint housingAethernetSheetRow)
        => RunV2(nameof(HousingAethernetTeleportByIdV2), $"{housingAethernetSheetRow}", () =>
            Svc.Data.GetExcelSheet<HousingAethernet>().TryGetRow(housingAethernetSheetRow, out var row)
                ? AethernetTeleportV2Core(row.PlaceName.Value.Name.GetText())
                : (IpcQueueResult.NotFound, "HousingAethernet 表查不到這個 id"));

    /// <summary>同 <see cref="AethernetTeleportToFirmament"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int AethernetTeleportToFirmamentV2()
        => RunV2(nameof(AethernetTeleportToFirmamentV2), "", () =>
            AethernetTeleportV2Core(Utils.GetAethernetNameWithOverrides(TaskAetheryteAethernetTeleport.FirmamentAethernetId)));

    /// <summary>同 <see cref="ChangeWorld"/>；已在該世界回 AlreadyThere(1)、跨資料中心但設定沒開回 Refused(5)。</summary>
    [EzIPC]
    public int ChangeWorldV2(string world)
        => RunV2(nameof(ChangeWorldV2), world ?? "null", () => ChangeWorldV2Core(world));

    /// <summary>同 <see cref="ChangeWorldById"/>；回 <see cref="IpcQueueResult"/> 的 int 值。</summary>
    [EzIPC]
    public int ChangeWorldByIdV2(uint worldId)
        => RunV2(nameof(ChangeWorldByIdV2), $"{worldId}", () =>
            Svc.Data.GetExcelSheet<World>().TryGetRow(worldId, out var row)
                ? ChangeWorldV2Core(row.Name.GetText())
                : (IpcQueueResult.NotFound, "World 表查不到這個 id"));

    /// <summary>同 <see cref="TeleportToHome"/>；在本世界且查不到個人房屋回 NotFound(4)。不在本世界時查不到房屋資料，照舊排入（先回本世界）。</summary>
    [EzIPC]
    public int TeleportToHomeV2()
        => RunV2(nameof(TeleportToHomeV2), "", () => PropertyV2Core(TaskPropertyShortcut.PropertyType.Home));

    /// <summary>同 <see cref="TeleportToFC"/>；判定方式同 <see cref="TeleportToHomeV2"/>。</summary>
    [EzIPC]
    public int TeleportToFCV2()
        => RunV2(nameof(TeleportToFCV2), "", () => PropertyV2Core(TaskPropertyShortcut.PropertyType.FC));

    /// <summary>同 <see cref="TeleportToApartment"/>；判定方式同 <see cref="TeleportToHomeV2"/>。</summary>
    [EzIPC]
    public int TeleportToApartmentV2()
        => RunV2(nameof(TeleportToApartmentV2), "", () => PropertyV2Core(TaskPropertyShortcut.PropertyType.Apartment));

    private (IpcQueueResult, string) TeleportToFavoriteV2Core(uint id, byte subIndex)
    {
        if(!C.Favorites.Contains(id)) return (IpcQueueResult.NotFound, "不在我的最愛裡");
        if(IsBusyCore()) return (IpcQueueResult.Busy, DescribeBusy());
        if(!Player.Interactable) return (IpcQueueResult.NotReady, "角色目前不可互動");
        var entry = Systems.TeleportPanel.TeleportPanelIndex.GetSnapshot().FirstOrDefault(x => x.Id == id && x.SubIndex == subIndex);
        if(entry == null) return (IpcQueueResult.NotFound, "傳送面板索引裡沒有這一項（索引還沒建好，或這隻角色還沒共鳴）");
        if(Tasks.Utility.TaskTeleportPanelGo.IsAlreadyThereNoOp(entry)) return (IpcQueueResult.AlreadyThere, "已站在這座城內乙太之晶旁，且沒有啟用中的自訂落點");
        return MeasureEnqueue(() => Tasks.Utility.TaskTeleportPanelGo.Enqueue(entry));
    }

    private (IpcQueueResult, string) AethernetTeleportV2Core(string destination)
    {
        if(string.IsNullOrWhiteSpace(destination)) return (IpcQueueResult.NotFound, "目的地是空的");
        if(IsBusyCore()) return (IpcQueueResult.Busy, DescribeBusy());
        // 開了「等畫面就緒」時，不論找不找得到目的地都會先排一項等待；要再多出至少一項才算排入。
        return MeasureEnqueue(() => TaskTryTpToAethernetDestination.Enqueue(destination), C.WaitForScreenReady ? 2 : 1);
    }

    private (IpcQueueResult, string) ChangeWorldV2Core(string world)
    {
        if(string.IsNullOrWhiteSpace(world)) return (IpcQueueResult.NotFound, "世界名是空的");
        if(IsBusyCore()) return (IpcQueueResult.Busy, DescribeBusy());
        if(!Player.Available) return (IpcQueueResult.NotReady, "沒有角色");
        if(world == Player.CurrentWorld) return (IpcQueueResult.AlreadyThere, "已在這個世界");
        bool isDcTransfer;
        if(CanVisitCrossDC(world)) isDcTransfer = true;
        else if(CanVisitSameDC(world)) isDcTransfer = false;
        else return (IpcQueueResult.NotFound, "不是可前往的世界");
        if(isDcTransfer && !C.AllowDcTransfer) return (IpcQueueResult.Refused, "設定裡沒有開啟跨資料中心移動");
        return MeasureEnqueue(() => P.TPAndChangeWorld(world, isDcTransfer));
    }

    private (IpcQueueResult, string) PropertyV2Core(TaskPropertyShortcut.PropertyType type)
    {
        if(IsBusyCore()) return (IpcQueueResult.Busy, DescribeBusy());
        if(!Player.Available) return (IpcQueueResult.NotReady, "沒有角色");
        var has = type switch
        {
            TaskPropertyShortcut.PropertyType.Home => HasPrivateHouseCore(),
            TaskPropertyShortcut.PropertyType.FC => HasFreeCompanyHouseCore(),
            _ => HasApartmentCore(),
        };
        if(has == false) return (IpcQueueResult.NotFound, "這隻角色沒有這間房（在本世界查過）");
        return MeasureEnqueue(() => TaskPropertyShortcut.Enqueue(type));
    }

    private static string DescribeBusy()
        => $"Lifestream 正在忙（目前任務 {P.TaskManager.CurrentTask?.Name ?? "無"}、排隊 {P.TaskManager.Tasks.Count} 項、自動移動剩 {P.followPath?.Waypoints.Count ?? 0} 個航點）";

    // 呼叫前已確認不忙＝佇列是空的，所以判定失敗時清掉的只會是這次排的。
    private static (IpcQueueResult, string) MeasureEnqueue(System.Action enqueue, int minAdded = 1)
    {
        string error = null;
        var result = IpcQueueMeasure.Run(() => P.TaskManager.NumQueuedTasks, () => { enqueue(); return true; }, minAdded,
            () => P.TaskManager.Abort(), e => { error = e.Message; e.Log(); });
        return (result, result switch
        {
            IpcQueueResult.Queued => "已排入",
            IpcQueueResult.Error => $"排程時發生例外，已撤掉這次排的任務：{error}",
            _ => "前置檢查都過了，但沒有排入任何任務（原因見前後的 log／聊天欄）",
        });
    }

    private static int RunV2(string endpoint, string args, Func<(IpcQueueResult Result, string Why)> body)
        => (int)IpcFrameworkGate.Get(endpoint, () =>
        {
            (IpcQueueResult Result, string Why) r;
            try
            {
                r = body();
            }
            catch(Exception e)
            {
                e.Log();
                r = (IpcQueueResult.Error, $"前置檢查發生例外：{e.Message}");
            }
            if(Systems.TravelDiag.ShouldLog($"{endpoint}.{args}.{r.Result}"))
            {
                PluginLog.Information($"[Lifestream IPC] {endpoint}({args}) → {(int)r.Result} {r.Result}：{r.Why}");
            }
            if(r.Result == IpcQueueResult.Queued) Systems.TravelDiag.Record($"IPC {endpoint}({args}) 已排入");
            return r.Result;
        }, IpcQueueResult.Unavailable);
}
