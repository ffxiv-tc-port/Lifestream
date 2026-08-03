using ECommons.Configuration;
using ECommons.GameHelpers;
using ECommons.SimpleGui;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;
// ⚠️ ECommons.GameHelpers 也有一個 Map，這裡指的一律是 Excel 表
using Map = Lumina.Excel.Sheets.Map;

namespace Lifestream.GUI.Windows;

/// <summary>
/// 傳送面板(移植自 DailyRoutines 的 BetterTeleport，UI 全部重寫)。
/// 搜尋 / 我的最愛 / 備註 / 地圖預覽，點一下就傳送。
///
/// 與 DR 的差別：
///   - 傳送一律走 <see cref="TaskTeleportPanelGo"/> 的安全路徑(Telepo / 既有乙太之光流程)，
///     沒有封包偽造。
///   - 自訂落點預設關閉，開了也是先走 vnavmesh；要直接寫座標得再開第二個開關。
///   - 我的最愛與備註直接沿用 Lifestream 既有的 <see cref="Data.Config.Favorites"/> /
///     <see cref="Data.Config.Renames"/>，所以跟浮動視窗(Overlay)、<c>/li</c> 指令是同一份資料。
/// </summary>
public class TeleportPanelWindow : Window
{
    private TeleportPanelWindow() : base("Lifestream: Teleport###LifestreamTeleportPanel")
    {
        EzConfigGui.WindowSystem.AddWindow(this);
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(480, 320),
            MaximumSize = new(4000, 4000),
        };
        Size = new(820, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private string Search = "";
    private uint SelectedId;
    private byte SelectedSub;
    private string RenameBuffer = "";
    private uint RenameTarget;

    /// <summary>地圖材質是 2048x2048，世界→材質座標的換算基準。</summary>
    private const float MapTextureSize = 2048f;

    public override void OnOpen()
    {
        // 解鎖狀態、房屋清單都可能在關窗期間變動，開窗時重建一次即可(不要每幀重建)。
        TeleportPanelIndex.Invalidate();
    }

    public override bool DrawConditions() => Player.Available;

    public override void Draw()
    {
        var entries = TeleportPanelIndex.Get();

        DrawToolbar();
        ImGui.Separator();

        var showMap = C.TeleportPanelShowMap;
        var mapWidth = showMap ? Math.Max(240f, ImGui.GetContentRegionAvail().X * 0.42f) : 0f;
        var listWidth = ImGui.GetContentRegionAvail().X - mapWidth - (showMap ? ImGui.GetStyle().ItemSpacing.X : 0f);

        if(ImGui.BeginChild("##LifestreamTpList", new Vector2(listWidth, 0), false))
        {
            DrawList(entries);
        }
        ImGui.EndChild();

        if(showMap)
        {
            ImGui.SameLine();
            if(ImGui.BeginChild("##LifestreamTpMap", new Vector2(0, 0), true))
            {
                DrawMap(entries);
            }
            ImGui.EndChild();
        }
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(220f.Scale());
        ImGui.InputTextWithHint("##LifestreamTpSearch", "Search destinations...".Loc(), ref Search, 100);
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Redo)) TeleportPanelIndex.Invalidate();
        ImGuiEx.Tooltip("Rebuild destination list".Loc());
        ImGui.SameLine();
        ImGui.Checkbox("Map".Loc(), ref C.TeleportPanelShowMap);
        ImGui.SameLine();
        ImGui.Checkbox("Hide aethernet in party".Loc(), ref C.TeleportPanelHideAethernetInParty);
        ImGuiEx.HelpMarker("While in a party, hide city aethernet shards from the list.".Loc());

        if(C.EnableAetheryteLanding)
        {
            ImGui.SameLine();
            ImGuiEx.Text(C.AetheryteLandingDirectWrite ? ImGuiColors.DalamudRed : ImGuiColors.DalamudYellow,
                C.AetheryteLandingDirectWrite ? "[direct position write]".Loc() : "[custom landings]".Loc());
        }
    }

    private void DrawList(List<TeleportPanelEntry> entries)
    {
        var hideAethernet = C.TeleportPanelHideAethernetInParty && Svc.Party.Length > 1;
        var search = Search.Trim();

        bool Visible(TeleportPanelEntry x)
        {
            if(C.Hidden.Contains(x.Id)) return false;
            if(hideAethernet && !x.IsAetheryte) return false;
            if(search != "" && !x.Matches(search)) return false;
            return true;
        }

        if(search != "")
        {
            var matches = entries.Where(Visible).OrderByDescending(x => C.Favorites.Contains(x.Id)).ThenBy(x => x.DisplayName).ToList();
            if(matches.Count == 0)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "No matching destinations.".Loc());
                return;
            }
            foreach(var x in matches) DrawRow(x, showZone: true);
            return;
        }

        // ── 我的最愛 ──────────────────────────────────────────────────────────
        var favorites = entries.Where(x => C.Favorites.Contains(x.Id) && Visible(x)).OrderBy(x => x.DisplayName).ToList();
        if(favorites.Count > 0)
        {
            if(ImGui.CollapsingHeader($"★ {"Favorites".Loc()} ({favorites.Count})###LifestreamTpFav", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                foreach(var x in favorites) DrawRow(x, showZone: true);
                ImGui.Unindent();
            }
        }

        // ── 依地區分組 ────────────────────────────────────────────────────────
        foreach(var group in entries.Where(Visible).GroupBy(x => x.RegionName).OrderBy(x => x.Key))
        {
            if(!ImGui.CollapsingHeader($"{group.Key}###LifestreamTpRegion{group.Key}")) continue;
            ImGui.Indent();
            foreach(var zone in group.GroupBy(x => x.ZoneName).OrderBy(x => x.Key))
            {
                if(zone.Key != "")
                {
                    ImGuiEx.Text(ImGuiColors.DalamudGrey3, zone.Key);
                }
                foreach(var x in zone.OrderByDescending(x => x.IsAetheryte).ThenBy(x => x.DisplayName))
                {
                    DrawRow(x, showZone: false);
                }
            }
            ImGui.Unindent();
        }
    }

    private void DrawRow(TeleportPanelEntry x, bool showZone)
    {
        ImGui.PushID($"tp{x.Id}_{x.SubIndex}");

        var label = x.DisplayName;
        if(C.Favorites.Contains(x.Id)) label = "★ " + label;
        if(!x.IsAetheryte) label = "» " + label;
        if(C.AetheryteLandings.ContainsKey(x.Id)) label += " ⚑";
        if(showZone && x.ZoneName != "" && !label.Contains(x.ZoneName)) label += $"  ({x.ZoneName})";

        var selected = SelectedId == x.Id && SelectedSub == x.SubIndex;
        if(ImGui.Selectable(label, selected))
        {
            SelectedId = x.Id;
            SelectedSub = x.SubIndex;
            TaskTeleportPanelGo.Enqueue(x);
        }
        if(ImGui.IsItemHovered())
        {
            // hover 只更新預覽選取，不觸發任何動作
            SelectedId = x.Id;
            SelectedSub = x.SubIndex;
            if(x.GilCost > 0) ImGuiEx.Tooltip($"{x.ZoneName}\n{x.GilCost} gil");
            else if(x.ZoneName != "") ImGuiEx.Tooltip(x.ZoneName);
        }
        DrawContextMenu(x);

        ImGui.PopID();
    }

    private void DrawContextMenu(TeleportPanelEntry x)
    {
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup($"LifestreamTpPopup{x.Id}_{x.SubIndex}");
            RenameTarget = x.Id;
            RenameBuffer = C.Renames.TryGetValue(x.Id, out var r) ? r : "";
        }
        if(!ImGui.BeginPopup($"LifestreamTpPopup{x.Id}_{x.SubIndex}")) return;

        ImGuiEx.Text(ImGuiColors.DalamudGrey, x.Name);
        ImGui.Separator();

        if(ImGuiEx.CollectionCheckbox("Favorite".Loc(), x.Id, C.Favorites))
        {
            // 我的最愛會影響 Overlay 的排序，跟 Overlay 的做法一致重建資料存放區
            S.Data.DataStore = new();
            EzConfig.Save();
        }
        if(ImGuiEx.CollectionCheckbox("Hidden".Loc(), x.Id, C.Hidden)) EzConfig.Save();

        ImGuiEx.Text("Rename:".Loc());
        ImGui.SetNextItemWidth(200f.Scale());
        if(RenameTarget == x.Id && ImGui.InputText("##LifestreamTpRename", ref RenameBuffer, 100))
        {
            if(RenameBuffer.Trim() == "") C.Renames.Remove(x.Id);
            else C.Renames[x.Id] = RenameBuffer.Trim();
            EzConfig.Save();
        }

        ImGui.Separator();
        DrawLandingMenu(x);

        ImGui.EndPopup();
    }

    /// <summary>
    /// 自訂落點的操作。功能關閉時仍然看得到既有落點(才知道匯入的資料還在)，但操作全部反白。
    /// </summary>
    private void DrawLandingMenu(TeleportPanelEntry x)
    {
        var has = C.AetheryteLandings.TryGetValue(x.Id, out var landing);

        if(!C.EnableAetheryteLanding)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "Custom landing is disabled in settings.".Loc());
            if(has) ImGuiEx.Text(ImGuiColors.DalamudGrey3, $"⚑ {landing.X:F1}, {landing.Y:F1}, {landing.Z:F1}");
            return;
        }

        if(has)
        {
            ImGuiEx.Text(ImGuiColors.DalamudYellow, $"⚑ {landing.X:F1}, {landing.Y:F1}, {landing.Z:F1}");
        }
        else
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "No custom landing set.".Loc());
        }

        // 只能在該乙太之光所屬的區域裡記錄落點 —— 在別的地圖記下來的座標沒有意義。
        var canSave = Player.Interactable && P.Territory == x.Territory;
        if(ImGuiEx.Button(has ? "Update landing to current position".Loc() : "Save current position as landing".Loc(), canSave))
        {
            C.AetheryteLandings[x.Id] = Player.Position;
            EzConfig.Save();
        }
        if(!canSave)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey3, $"{"Must be in".Loc()} {x.ZoneName}");
        }

        if(has && ImGuiEx.Button("Clear landing".Loc(), ImGuiEx.Ctrl))
        {
            C.AetheryteLandings.Remove(x.Id);
            EzConfig.Save();
        }
        if(has) ImGuiEx.Tooltip("Hold CTRL and click to delete".Loc());
    }

    private void DrawMap(List<TeleportPanelEntry> entries)
    {
        var entry = entries.FirstOrDefault(x => x.Id == SelectedId && x.SubIndex == SelectedSub);
        if(entry == null)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Hover a destination to preview its map.".Loc());
            return;
        }

        ImGuiEx.Text(entry.DisplayName);
        ImGuiEx.Text(ImGuiColors.DalamudGrey3, entry.ZoneName);

        if(entry.MapId == 0 || !Svc.Data.GetExcelSheet<Map>().TryGetRow(entry.MapId, out var map))
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "No map available for this destination.".Loc());
            return;
        }

        var mapId = map.Id.ExtractText();
        if(mapId == "")
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "No map available for this destination.".Loc());
            return;
        }
        var path = $"ui/map/{mapId}/{mapId.Replace("/", "")}_m.tex";

        // ⚠️ 每幀重新取得材質包裝，**絕不跨幀保存** ——
        // 共享的即時 texture wrap 存下來跨幀使用是已知的致命崩潰模式。
        if(!Svc.Texture.GetFromGame(path).TryGetWrap(out var wrap, out _))
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "Loading map...".Loc());
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var side = Math.Min(avail.X, avail.Y);
        if(side < 48f) return;

        if(ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
        {
            C.TeleportPanelMapZoom = Math.Clamp(C.TeleportPanelMapZoom + ImGui.GetIO().MouseWheel * 0.15f, 1f, 6f);
        }

        // 以乙太之光(或落點)為中心裁切
        var focusWorld = C.AetheryteLandings.TryGetValue(entry.Id, out var landing) && C.EnableAetheryteLanding
            ? landing
            : entry.Position ?? Vector3.Zero;
        var centerUv = WorldToTextureUv(focusWorld, map);

        var half = 0.5f / C.TeleportPanelMapZoom;
        // 夾在materials 範圍內，避免縮放後拉出圖外露出重複邊緣
        var cx = Math.Clamp(centerUv.X, half, 1f - half);
        var cy = Math.Clamp(centerUv.Y, half, 1f - half);
        var uv0 = new Vector2(cx - half, cy - half);
        var uv1 = new Vector2(cx + half, cy + half);

        var origin = ImGui.GetCursorScreenPos();
        ImGui.Image(wrap.Handle, new Vector2(side), uv0, uv1);

        Vector2 UvToScreen(Vector2 uv) => origin + (uv - uv0) / (uv1 - uv0) * side;

        var draw = ImGui.GetWindowDrawList();
        if(entry.Position != null)
        {
            var p = UvToScreen(WorldToTextureUv(entry.Position.Value, map));
            draw.AddCircleFilled(p, 5f, ImGui.GetColorU32(ImGuiColors.TankBlue));
            draw.AddCircle(p, 6f, ImGui.GetColorU32(ImGuiColors.DalamudWhite), 0, 1.5f);
        }
        if(C.EnableAetheryteLanding && C.AetheryteLandings.TryGetValue(entry.Id, out var lp))
        {
            var p = UvToScreen(WorldToTextureUv(lp, map));
            draw.AddCircleFilled(p, 4f, ImGui.GetColorU32(ImGuiColors.DalamudOrange));
            draw.AddCircle(p, 6f, ImGui.GetColorU32(ImGuiColors.DalamudWhite), 0, 1.5f);
        }
        // 人在同一張地圖時也標出來，方便判斷落點對不對
        if(P.Territory == entry.Territory && Player.Available)
        {
            var p = UvToScreen(WorldToTextureUv(Player.Position, map));
            draw.AddCircleFilled(p, 3.5f, ImGui.GetColorU32(ImGuiColors.HealerGreen));
        }
    }

    /// <summary>
    /// 世界座標 → 地圖材質的 UV(0..1)。
    /// 換算式 <c>(pos.XZ + Offset) * SizeFactor/100 + 1024</c>，材質固定 2048x2048。
    /// 這與 HuntHelper 已離線驗證過的 <c>ui/map</c> 對齊分析一致(SizeFactor 100 時 1 world unit = 1 px)。
    /// </summary>
    private static Vector2 WorldToTextureUv(Vector3 pos, Map map)
    {
        var scale = map.SizeFactor / 100f;
        var tex = (new Vector2(pos.X, pos.Z) + new Vector2(map.OffsetX, map.OffsetY)) * scale + new Vector2(1024f, 1024f);
        return tex / MapTextureSize;
    }
}
