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
        OnHover = h =>
        {
            // hover 只更新地圖預覽的對象，不觸發任何動作
            SelectedId = h.Id;
            SelectedSub = h.SubIndex;
        };
    }

    private string Search = "";
    private uint SelectedId;
    private byte SelectedSub;

    /// <summary>
    /// hover 回呼快取成欄位。⚠️ 只捕捉 <c>this</c> 的 lambda **不會**被編譯器快取，
    /// 寫在 DrawRow 裡等於每一列每一幀配置一個 delegate(清單可以有好幾百列)。
    /// </summary>
    private Action<TeleportPanelEntry> OnHover;

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
        if(ImGui.SmallButton($"★ {"Favorites".Loc()}")) S.Gui.TeleportFavoritesWindow.IsOpen = true;
        ImGuiEx.Tooltip("Open the favorites window (custom order and categories).".Loc());

        // ⚠️ 分成兩行：原本一行塞得下是因為只有兩個核取方塊加一段靜態文字，
        // 現在多了核取方塊之後在預設視窗寬度會被推出右邊界。
        ImGui.Checkbox("Map".Loc(), ref C.TeleportPanelShowMap);
        ImGui.SameLine();
        ImGui.Checkbox("Hide aethernet in party".Loc(), ref C.TeleportPanelHideAethernetInParty);
        ImGuiEx.HelpMarker("While in a party, hide city aethernet shards from the list.".Loc());

        // 「直接寫入座標」原本只是一行靜態警示字。它其實是既有的第二層開關
        // (Config.AetheryteLandingDirectWrite，預設關)，改成核取方塊讓它在面板上就能開關，
        // 行為與預設值沒有變 —— 沒打勾時走的一直都是 vnavmesh 導航。
        TeleportRowUI.DrawDirectWriteToggle();
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
            foreach(var x in matches) DrawRow(x, showZone: true, idScope: "search");
            return;
        }

        // ── 我的最愛 ──────────────────────────────────────────────────────────
        var favorites = entries.Where(x => C.Favorites.Contains(x.Id) && Visible(x)).OrderBy(x => x.DisplayName).ToList();
        if(favorites.Count > 0)
        {
            if(ImGui.CollapsingHeader($"★ {"Favorites".Loc()} ({favorites.Count})###LifestreamTpFav", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                // 🔴 idScope 必須跟地區分組那邊不同：我的最愛的項目在下面的地區分組裡會**再出現一次**，
                // 兩邊用同一組 ImGui id 會讓右鍵選單整份被畫兩遍，而且只有後畫的那份收得到輸入。
                foreach(var x in favorites) DrawRow(x, showZone: true, idScope: "fav");
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
                    DrawRow(x, showZone: false, idScope: "grp");
                }
            }
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// 一列目的地。實際繪製在 <see cref="TeleportRowUI"/>(與我的最愛視窗共用)，
    /// 這裡只負責面板自己的狀態：地圖預覽的選取對象、以及點下去要排傳送。
    /// </summary>
    private void DrawRow(TeleportPanelEntry x, bool showZone, string idScope)
    {
        var selected = SelectedId == x.Id && SelectedSub == x.SubIndex;
        var clicked = TeleportRowUI.Draw(x, idScope, showZone, selected, OnHover);
        if(clicked)
        {
            SelectedId = x.Id;
            SelectedSub = x.SubIndex;
            TaskTeleportPanelGo.Enqueue(x);
        }
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
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, LocText.NoMapAvailableForDestination.Loc());
            return;
        }

        var mapId = map.Id.ExtractText();
        if(mapId == "")
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, LocText.NoMapAvailableForDestination.Loc());
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
