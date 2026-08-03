using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lifestream.Systems.Legacy;
using Lumina.Excel.Sheets;

namespace Lifestream.Systems.TeleportPanel;

/// <summary>
/// 傳送面板的一個可選目的地。可能是主水晶(走 Telepo)或城內乙太之光(走乙太之光網路)。
/// ⚠️ 這裡只存 ID 與靜態表資料，不存任何原生指標，也不存 IGameObject。
/// </summary>
public sealed class TeleportPanelEntry
{
    /// <summary>Aetheryte 表的 RowId。這也是 <see cref="Data.Config.Favorites"/> /
    /// <see cref="Data.Config.Renames"/> / <see cref="Data.Config.AetheryteLandings"/> 的鍵，
    /// 與 DailyRoutines BetterTeleport 使用的鍵**完全相同**，所以設定可以直接沿用。</summary>
    public uint Id;

    /// <summary>房屋類乙太之光的子索引(私人房屋/公會房屋/公寓)。主水晶恆為 0。</summary>
    public byte SubIndex;

    /// <summary>true = 主水晶(可直接 Telepo)；false = 城內乙太之光(要走乙太之光網路)。</summary>
    public bool IsAetheryte;

    /// <summary>城內乙太之光所屬的主水晶 RowId；主水晶自身為 0。</summary>
    public uint MasterId;

    public uint Territory;
    public uint MapId;
    public string Name = "";
    public string ZoneName = "";
    public string RegionName = "";
    public uint GilCost;

    /// <summary>乙太之光本體在世界中的座標(來自地圖標記，Y 多半是 0)。解析不到時為 null。</summary>
    public Vector3? Position;

    /// <summary>套用使用者備註後的顯示名稱(備註會取代原名，與 DR 的行為一致)。</summary>
    public string DisplayName => C.Renames.TryGetValue(Id, out var v) && v != "" ? v : Name;

    /// <summary>
    /// 搜尋比對用的靜態字串(原名 + 區域名 + 地區名)，建索引時就組好。
    /// ⚠️ 刻意**不**用屬性每次組字串：清單是逐幀重畫的，那等於每幀配置數百個字串。
    /// 會變動的備註改在 <see cref="Matches"/> 裡另外比對，不需要重組。
    /// </summary>
    public string SearchText = "";

    public bool Matches(string search)
        => SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (C.Renames.TryGetValue(Id, out var v) && v.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 建立並快取傳送面板的目的地清單。
///
/// 資料來源刻意分成兩半，因為兩者的「可傳送」定義不同：
///   - 主水晶：直接列舉 <see cref="Svc.AetheryteList"/>。它就是遊戲自己認定「你現在能傳送到哪」的
///     權威清單，還附帶正確的 SubIndex(房屋)與傳送費，不必自己推。
///   - 城內乙太之光：<see cref="Svc.AetheryteList"/> 不含它們，改由 Lifestream 既有的
///     <see cref="DataStore.Aetherytes"/> 取得，再用 <see cref="UIState.IsAetheryteUnlocked"/> 過濾。
///     這跟 TaskGotoDestination 判斷解鎖的方法一致(只讀 UIState 的解鎖點陣圖，無特徵碼、無 hook)。
/// </summary>
public static unsafe class TeleportPanelIndex
{
    private static List<TeleportPanelEntry> Cache;

    /// <summary>乙太之光座標查一次就永久快取：那是靜態遊戲資料，而且逐幀重算會全表掃 MapMarker。</summary>
    private static readonly Dictionary<uint, Vector3?> PositionCache = [];

    public static void Invalidate() => Cache = null;

    public static List<TeleportPanelEntry> Get()
    {
        Cache ??= Build();
        return Cache;
    }

    private static List<TeleportPanelEntry> Build()
    {
        var result = new List<TeleportPanelEntry>();
        var aetheryteSheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var territorySheet = Svc.Data.GetExcelSheet<TerritoryType>();

        // ── 主水晶(含房屋) ────────────────────────────────────────────────────────
        try
        {
            foreach(var entry in Svc.AetheryteList)
            {
                var data = entry.AetheryteData.ValueNullable;
                if(data == null) continue;
                var e = new TeleportPanelEntry
                {
                    Id = entry.AetheryteId,
                    SubIndex = entry.SubIndex,
                    IsAetheryte = true,
                    Territory = entry.TerritoryId,
                    GilCost = (uint)entry.GilCost,
                };
                FillNames(e, data.Value, territorySheet);
                // 同一個 RowId 會因為房屋子索引出現多筆，名稱要能分辨是哪一間。
                if(entry.SubIndex > 0)
                {
                    e.Name = entry.Ward > 0
                        ? $"{e.Name} ({"Ward".Loc()} {entry.Ward}, {(entry.Plot > 0 ? $"{"Plot".Loc()} {entry.Plot}" : $"#{entry.SubIndex}")})"
                        : $"{e.Name} #{entry.SubIndex}";
                }
                e.Position = GetPosition(e.Id);
                FinishEntry(e);
                result.Add(e);
            }
        }
        catch(Exception ex)
        {
            PluginLog.Warning($"[TeleportPanel] Could not enumerate AetheryteList: {ex.Message}");
        }

        // ── 城內乙太之光 ─────────────────────────────────────────────────────────
        // DataStore 是由 SingletonServiceManager 在啟動排程裡建立的。理論上視窗不可能比它早畫，
        // 但寧可少列一段清單也不要在 Draw 裡丟 NRE。
        var uiState = UIState.Instance();
        if(uiState != null && S.Data.DataStore?.Aetherytes != null)
        {
            foreach(var (master, children) in S.Data.DataStore.Aetherytes)
            {
                foreach(var child in children)
                {
                    // 選單上不會出現的隱藏節點(飛空艇著陸場之類)不能當目的地
                    if(child.Invisible) continue;
                    if(!uiState->IsAetheryteUnlocked(child.ID)) continue;
                    if(!aetheryteSheet.TryGetRow(child.ID, out var row)) continue;

                    var e = new TeleportPanelEntry
                    {
                        Id = child.ID,
                        SubIndex = 0,
                        IsAetheryte = false,
                        MasterId = master.ID,
                        Territory = child.TerritoryType,
                    };
                    FillNames(e, row, territorySheet);
                    if(e.Name == "") e.Name = child.Name;
                    e.Position = GetPosition(e.Id);
                    FinishEntry(e);
                    result.Add(e);
                }
            }
        }

        PluginLog.Debug($"[TeleportPanel] Built index: {result.Count(x => x.IsAetheryte)} aetherytes, {result.Count(x => !x.IsAetheryte)} aethernet shards.");
        return result;
    }

    private static void FillNames(TeleportPanelEntry e, Aetheryte row, Lumina.Excel.ExcelSheet<TerritoryType> territorySheet)
    {
        // 主水晶用 PlaceName，城內乙太之光用 AethernetName —— 兩者只會有一個非空。
        var place = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
        var aethernet = row.AethernetName.ValueNullable?.Name.ToString() ?? "";
        e.Name = row.IsAetheryte ? (place != "" ? place : aethernet) : (aethernet != "" ? aethernet : place);

        if(territorySheet.TryGetRow(e.Territory, out var terr))
        {
            e.MapId = terr.Map.RowId;
            e.ZoneName = terr.PlaceName.ValueNullable?.Name.ToString() ?? "";
            e.RegionName = terr.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
        }
        if(e.RegionName == "") e.RegionName = "Other".Loc();
        if(e.Name == "") e.Name = e.ZoneName != "" ? e.ZoneName : $"#{e.Id}";
    }

    /// <summary>建索引的最後一步：把搜尋字串組好，之後逐幀比對就不用再配置字串。</summary>
    private static void FinishEntry(TeleportPanelEntry e)
        => e.SearchText = $"{e.Name}\n{e.ZoneName}\n{e.RegionName}";

    /// <summary>
    /// 乙太之光的世界座標。ECommons 的 <see cref="ECommons.GameHelpers.Map.AetherytePosition"/>
    /// 對沒有 Level 資料的節點會全表掃 MapMarker，所以查一次就快取(null 也快取，避免重複丟例外)。
    /// </summary>
    public static Vector3? GetPosition(uint aetheryteId)
    {
        if(PositionCache.TryGetValue(aetheryteId, out var cached)) return cached;
        Vector3? pos;
        try
        {
            pos = ECommons.GameHelpers.Map.AetherytePosition(aetheryteId);
        }
        catch(Exception e)
        {
            PluginLog.Debug($"[TeleportPanel] Could not resolve position of aetheryte {aetheryteId}: {e.Message}");
            pos = null;
        }
        PositionCache[aetheryteId] = pos;
        return pos;
    }
}
