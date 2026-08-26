using ECommons.Configuration;
using ECommons.GameHelpers;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Utility;

namespace Lifestream.GUI.Windows;

/// <summary>
/// 一個傳送目的地的「一列」＋它的右鍵選單。傳送面板與我的最愛視窗共用同一份繪製程式碼，
/// 免得兩邊各長一套之後只修好其中一邊。
///
/// 🔴 <c>idScope</c> 不是裝飾用的參數 —— 它是 v7.20.0.24 那個「右鍵選單整份畫兩次、
/// 只有下面那份輸入得了」的根因所在：
///   ImGui 的 popup id 是從**目前的 id 堆疊**算出來的。同一個目的地在同一幀被畫兩次
///   (例如同時出現在「我的最愛」區與它所屬的地區分組)時，兩處推的 id 完全一樣，於是
///     1. <c>BeginPopup</c> 在同一幀對同一個 id 回 true 兩次，而 ImGui 的 <c>Begin</c>
///        對同名視窗是**接續寫入**，所以選單內容整份被畫兩遍；
///     2. 兩個重新命名輸入框的 id 也一樣，同 id 的項目由**後畫的那個**接管輸入 ——
///        正好就是使用者看到的「只有下面那份有效」。
///   ⚠️ 非我的最愛的項目只會出現在自己的地區分組裡，所以不會重複 —— 這也是這個假設的
///   決定性驗證方式。
/// 每個清單區塊都必須給一個彼此不同的 idScope；新增顯示同一批項目的視窗時同理。
/// </summary>
internal static class TeleportRowUI
{
    /// <summary>
    /// 有自訂落點的項目整列改用亮淺藍。
    /// 刻意不用 <c>ImGuiColors.TankBlue</c>(0, 0.6, 1)：那個在深色底上偏暗，掃視時跟白字差別不夠大。
    /// 也刻意**不動「★」的畫法** —— 星號是字形不是顏色，兩者疊在同一列不會互相打架。
    /// </summary>
    private static readonly Vector4 LandingColor = new(0.60f, 0.85f, 1.00f, 1f);

    private static uint RenameTarget;
    private static string RenameBuffer = "";

    /// <summary>
    /// 畫一列。回傳 true 代表使用者點了它(呼叫端自行決定要不要排傳送)。
    /// </summary>
    /// <param name="x">要畫的目的地。</param>
    /// <param name="idScope">見類別註解。同一幀內畫同一個項目的每個區塊都要不同。</param>
    /// <param name="showZone">名稱後面要不要補上區域名(分組清單裡已經有區域標題就不用)。</param>
    /// <param name="selected">是否畫成選取狀態(面板用來標示地圖預覽的對象)。</param>
    /// <param name="onHover">滑鼠移上去時呼叫。面板用它更新地圖預覽，其他視窗可以不給。</param>
    public static bool Draw(TeleportPanelEntry x, string idScope, bool showZone, bool selected,
        Action<TeleportPanelEntry> onHover = null)
    {
        ImGui.PushID($"{idScope}/tp{x.Id}_{x.SubIndex}");

        var label = x.DisplayName;
        if(C.Favorites.Contains(x.Id)) label = "★ " + label;
        if(!x.IsAetheryte) label = "» " + label;
        var hasLanding = C.AetheryteLandings.ContainsKey(x.Id);
        if(hasLanding) label += " ⚑";
        if(showZone && x.ZoneName != "" && !label.Contains(x.ZoneName)) label += $"  ({x.ZoneName})";

        if(hasLanding) ImGui.PushStyleColor(ImGuiCol.Text, LandingColor);
        var clicked = ImGui.Selectable(label, selected);
        if(hasLanding) ImGui.PopStyleColor();

        if(ImGui.IsItemHovered())
        {
            // hover 只更新預覽選取，不觸發任何動作
            onHover?.Invoke(x);
            var tooltip = x.GilCost > 0 ? $"{x.ZoneName}\n{x.GilCost} gil"
                : x.ZoneName != "" ? x.ZoneName : "";
            if(hasLanding)
            {
                var landing = C.AetheryteLandings[x.Id];
                // 「有沒有自訂落點」用列上的顏色與 ⚑ 就看得到；具體座標與「功能關著」這種
                // 起疑才會想查的細節放 tooltip。
                if(tooltip != "") tooltip += "\n";
                tooltip += $"⚑ {landing.X:F1}, {landing.Y:F1}, {landing.Z:F1}";
                if(!C.EnableAetheryteLanding) tooltip += $"\n{LocText.CustomLandingDisabled.Loc()}";
            }
            if(tooltip != "") ImGuiEx.Tooltip(tooltip);
        }

        DrawContextMenu(x);

        ImGui.PopID();
        return clicked;
    }

    private static void DrawContextMenu(TeleportPanelEntry x)
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
            // 我的最愛會影響 Overlay 的排序，跟 Overlay 的做法一致重建資料存放區。
            // 取消我的最愛時要把自訂排序/分類一併清掉，否則設定檔會累積孤兒 id。
            FavoriteOrganizer.PruneIfNotFavorite(x.Id);
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
    private static void DrawLandingMenu(TeleportPanelEntry x)
    {
        var has = C.AetheryteLandings.TryGetValue(x.Id, out var landing);

        if(!C.EnableAetheryteLanding)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, LocText.CustomLandingDisabled.Loc());
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

    /// <summary>
    /// 傳送面板/我的最愛視窗共用的「直接寫入座標」開關。
    /// 這是 <see cref="Data.Config.AetheryteLandingDirectWrite"/> 這個**既有**的第二層開關，
    /// 只是從設定分頁裡的核取方塊也搬一份到面板上，行為與預設值(關)完全沒變。
    /// 危險狀態要在列上一眼看得見(紅字)，理由與免責放 tooltip。
    /// </summary>
    /// <param name="sameLine">要不要接在前一個控制項後面。⚠️ 這個方法在兩種狀態下**什麼都不畫**
    /// (功能關著又沒有任何落點)，所以 SameLine 必須由它自己決定，不能由呼叫端無條件先呼叫 ——
    /// 否則會有一個懸空的 SameLine 把後面的分隔線拉到同一行。</param>
    public static void DrawDirectWriteToggle(bool sameLine = true)
    {
        if(C.EnableAetheryteLanding)
        {
            if(sameLine) ImGui.SameLine();
            var on = C.AetheryteLandingDirectWrite;
            if(on) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            if(ImGui.Checkbox("Write position directly".Loc(), ref on))
            {
                C.AetheryteLandingDirectWrite = on;
                EzConfig.Save();
            }
            if(C.AetheryteLandingDirectWrite) ImGui.PopStyleColor();
            ImGuiEx.Tooltip(C.AetheryteLandingDirectWrite
                ? $"{LocText.MemoryTeleportWarning.Loc()}\n\n{LocText.MemoryTeleportRefusalNote.Loc()}"
                : "Off: Lifestream walks you to the custom landing with vnavmesh. Turn this on only if you accept the risk explained in the settings.".Loc());
        }
        else if(C.AetheryteLandings.Count > 0)
        {
            if(sameLine) ImGui.SameLine();
            // 「有落點但整個功能關著」= 被忽略。它本身要在列上看得見，不能只藏在 tooltip 裡。
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "[landings off]".Loc());
            ImGuiEx.Tooltip($"{LocText.CustomLandingDisabled.Loc()}\n{C.AetheryteLandings.Count} {"custom landing positions".Loc()}");
        }
    }
}
