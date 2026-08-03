using ECommons.Configuration;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Lifestream.Systems.TeleportPanel;

/// <summary>
/// 從 DailyRoutines 的 <c>BetterTeleport.json</c> 匯入我的最愛／備註／自訂落點。
///
/// 為什麼可以直接匯入：DR 的三份資料都以 **Aetheryte 表的 RowId** 為鍵，
/// 而 Lifestream 的 <see cref="Data.Config.Favorites"/> 與 <see cref="Data.Config.Renames"/>
/// 用的是同一個鍵(<c>TinyAetheryte.ID</c> 就是 <c>Aetheryte</c> 的 RowId)，
/// 所以是 1:1 對應，不需要任何轉換表。落點 <c>Positions</c> 也一樣，對到
/// <see cref="Data.Config.AetheryteLandings"/>。
///
/// 匯入採**聯集、不覆蓋**：已經在 Lifestream 設過的備註/落點不會被 DR 的值蓋掉，
/// 所以重複按也不會弄壞既有設定。
/// </summary>
public static class DailyRoutinesImport
{
    /// <summary>
    /// DR 設定檔的預設位置。不寫死啟動器路徑，改由 Lifestream 自己的設定目錄推導
    /// (兩者都在同一個 <c>pluginConfigs</c> 底下)，換啟動器也找得到。
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var pluginConfigs = Svc.PluginInterface.ConfigDirectory.Parent;
            return pluginConfigs == null ? "" : Path.Combine(pluginConfigs.FullName, "DailyRoutines", "BetterTeleport.json");
        }
    }

    public static bool Exists => DefaultPath != "" && File.Exists(DefaultPath);

    /// <summary>匯入結果摘要，直接顯示在設定畫面上。</summary>
    public static string LastResult { get; private set; }

    public static void Import()
    {
        try
        {
            var path = DefaultPath;
            if(path == "" || !File.Exists(path))
            {
                LastResult = $"{"File not found:".Loc()} {path}";
                PluginLog.Information($"[TeleportPanel] DR import: file not found at {path}");
                return;
            }

            // ⚠️ 顯式指定 UTF-8：備註是中文，跟著系統 ANSI 讀會變亂碼。
            var json = File.ReadAllText(path, Encoding.UTF8);
            var root = JObject.Parse(json);

            var favAdded = 0;
            var remarkAdded = 0;
            var posAdded = 0;
            var skipped = 0;

            if(root["Favorites"] is JArray favorites)
            {
                foreach(var token in favorites)
                {
                    if(!TryGetId(token, out var id)) { skipped++; continue; }
                    if(C.Favorites.Add(id)) favAdded++;
                }
            }

            if(root["Remarks"] is JObject remarks)
            {
                foreach(var (key, value) in remarks)
                {
                    if(!uint.TryParse(key, out var id)) { skipped++; continue; }
                    var text = value?.ToString() ?? "";
                    if(text == "") continue;
                    // 不覆蓋既有的重新命名
                    if(C.Renames.ContainsKey(id)) continue;
                    C.Renames[id] = text;
                    remarkAdded++;
                }
            }

            if(root["Positions"] is JObject positions)
            {
                foreach(var (key, value) in positions)
                {
                    if(!uint.TryParse(key, out var id) || value is not JObject v) { skipped++; continue; }
                    var x = v["X"]?.Value<float>();
                    var y = v["Y"]?.Value<float>();
                    var z = v["Z"]?.Value<float>();
                    if(x == null || y == null || z == null) { skipped++; continue; }
                    if(C.AetheryteLandings.ContainsKey(id)) continue;
                    C.AetheryteLandings[id] = new Vector3(x.Value, y.Value, z.Value);
                    posAdded++;
                }
            }

            EzConfig.Save();
            // 我的最愛會影響排序，重建資料存放區(跟 Overlay 的 Favorite 勾選同一套做法)
            S.Data.DataStore = new();
            TeleportPanelIndex.Invalidate();

            LastResult = $"{"Imported".Loc()}: {favAdded} {"favorites".Loc()}, {remarkAdded} {"remarks".Loc()}, {posAdded} {"custom landing positions".Loc()}"
                + (skipped > 0 ? $" ({skipped} {"skipped".Loc()})" : "");
            PluginLog.Information($"[TeleportPanel] DR import from {path}: +{favAdded} favorites, +{remarkAdded} renames, +{posAdded} landings, {skipped} skipped.");
        }
        catch(Exception e)
        {
            LastResult = $"{"Import failed:".Loc()} {e.Message}";
            PluginLog.Error($"[TeleportPanel] DR import failed: {e}");
        }
    }

    private static bool TryGetId(JToken token, out uint id)
    {
        id = 0;
        try
        {
            id = token.Value<uint>();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
