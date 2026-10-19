using ECommons.Configuration;
using ECommons.GameHelpers;
using ECommons.SimpleGui;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;

namespace Lifestream.GUI.Windows;

/// <summary>
/// 我的最愛專用視窗：自訂排序 + 自訂分類。
/// 獨立視窗而不是改傳送面板裡那一區：面板的清單是「找地方」用的(搜尋、地區分組、地圖預覽)，
/// 排序與分類是「整理」用的，兩者塞在同一份清單裡會互相打架。
/// <see cref="Data.Config.Favorites"/>(既有的 HashSet)的資料模型不動，星號仍加在它上面；順序與分類另存在
/// <see cref="Data.Config.FavoriteOrder"/> / <see cref="Data.Config.FavoriteCategories"/> /
/// <see cref="Data.Config.FavoriteCategoryAssignment"/>：順序表空的落回字母序，歸屬表空的全部在「未分類」。
/// </summary>
public class TeleportFavoritesWindow : Window
{
    private TeleportFavoritesWindow() : base("Lifestream: Favorites###LifestreamTeleportFavorites")
    {
        EzConfigGui.WindowSystem.AddWindow(this);
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(360, 240),
            MaximumSize = new(4000, 4000),
        };
        Size = new(460, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private bool EditMode;
    private string NewCategoryName = "";
    private uint RenamingCategory;
    private string CategoryNameBuffer = "";

    /// <summary>
    /// 分類下拉選單的選項。⚠️ 每一列都重建一份 List+Dictionary 等於每幀配置數十份，
    /// 所以整理模式下每幀只在 <see cref="Draw"/> 開頭重建一次。
    /// </summary>
    private readonly List<uint> CategoryIds = [];
    private readonly Dictionary<uint, string> CategoryNames = [];

    // 每幀重用的暫存（只在繪製執行緒碰）。內容每幀重算、不跨幀沿用，
    // 所以沒有失效條件要追；省下的只是每幀新建集合與閉包。
    private readonly List<TeleportPanelEntry> FavoritesBuffer = [];
    private readonly List<uint> CategorySnapshot = [];
    private readonly List<TeleportPanelEntry> CategoryItems = [];
    private readonly List<SortRow> SortBuffer = [];
    private readonly List<uint> IdsBuffer = [];
    private readonly HashSet<uint> IdsSeen = [];
    private readonly HashSet<uint> KnownIds = [];
    private readonly List<uint> NotAttuned = [];
    private readonly Dictionary<uint, string> IdScopes = [];

    private struct SortRow
    {
        public TeleportPanelEntry Entry;
        public int Rank;
        public string Name;
        public int Position;
    }

    public override void OnOpen() => TeleportPanelIndex.Invalidate();

    public override bool DrawConditions() => Player.Available;

    public override void Draw()
    {
        var entries = TeleportPanelIndex.Get();

        // 我的最愛是以 Aetheryte RowId 為鍵，而同一個 RowId 可能對到多筆項目(房屋的子索引)。
        // 所以這裡取的是「所有 id 在我的最愛裡的項目」而不是「每個 id 一筆」——
        // 少列任何一筆等於使用者的目的地被靜默吃掉。
        var favorites = FavoritesBuffer;
        favorites.Clear();
        foreach(var x in entries)
        {
            if(C.Favorites.Contains(x.Id)) favorites.Add(x);
        }
        if(EditMode) RebuildCategoryChoices();

        DrawToolbar(favorites.Count);
        ImGui.Separator();

        if(ImGui.BeginChild("##LifestreamFavList", new Vector2(0, 0), false))
        {
            // ⚠️ 分類清單可能在這一輪被刪掉一筆，先做快照再走訪。
            CategorySnapshot.Clear();
            foreach(var category in C.FavoriteCategories) CategorySnapshot.Add(category.Id);
            foreach(var categoryId in CategorySnapshot)
            {
                DrawCategory(categoryId, FavoriteOrganizer.CategoryName(categoryId), CollectCategory(favorites, categoryId));
            }
            DrawCategory(FavoriteOrganizer.Uncategorized, "Uncategorized".Loc(),
                CollectCategory(favorites, FavoriteOrganizer.Uncategorized));

            DrawUnresolved(entries);
        }
        ImGui.EndChild();
    }

    /// <summary>某個分類底下的我的最愛，維持 <paramref name="favorites"/> 的順序。回傳的清單下一次呼叫就會被覆寫。</summary>
    private List<TeleportPanelEntry> CollectCategory(List<TeleportPanelEntry> favorites, uint categoryId)
    {
        CategoryItems.Clear();
        foreach(var x in favorites)
        {
            if(FavoriteOrganizer.GetCategory(x.Id) == categoryId) CategoryItems.Add(x);
        }
        return CategoryItems;
    }

    /// <summary>
    /// 等同 OrderBy(Rank).ThenBy(DisplayName).ThenBy(SubIndex)：字串用同一個預設比較子，
    /// 三個鍵都相同時比原本的位置——OrderBy 是穩定排序，補上這一鍵結果就唯一、與它一致。
    /// </summary>
    private static readonly Comparison<SortRow> CompareSortRows = static (a, b) =>
    {
        var c = a.Rank.CompareTo(b.Rank);
        if(c != 0) return c;
        c = Comparer<string>.Default.Compare(a.Name, b.Name);
        if(c != 0) return c;
        c = a.Entry.SubIndex.CompareTo(b.Entry.SubIndex);
        if(c != 0) return c;
        return a.Position.CompareTo(b.Position);
    };

    private List<SortRow> SortForDisplay(List<TeleportPanelEntry> items)
    {
        SortBuffer.Clear();
        for(var i = 0; i < items.Count; i++)
        {
            var x = items[i];
            SortBuffer.Add(new SortRow { Entry = x, Rank = FavoriteOrganizer.Rank(x.Id), Name = x.DisplayName, Position = i });
        }
        SortBuffer.Sort(CompareSortRows);
        return SortBuffer;
    }

    // 迴圈而不是 FirstOrDefault(x => x.Id == categoryId)：捕獲參數的閉包在方法開頭就建立，
    // 寫在 DrawCategory 裡等於每幀每個分類都配置一次，即使沒有在改名。
    private static Data.FavoriteCategory FindCategory(uint categoryId)
    {
        foreach(var x in C.FavoriteCategories)
        {
            if(x.Id == categoryId) return x;
        }
        return null;
    }

    private string IdScope(uint categoryId)
    {
        if(!IdScopes.TryGetValue(categoryId, out var scope))
        {
            scope = $"favwin{categoryId}";
            IdScopes[categoryId] = scope;
        }
        return scope;
    }

    private void DrawToolbar(int shown)
    {
        ImGui.Checkbox("Organize".Loc(), ref EditMode);
        ImGuiEx.Tooltip("Show reorder arrows and the category selector on every row.".Loc());
        ImGui.SameLine();
        if(ImGui.SmallButton("Teleport panel".Loc())) S.Gui.TeleportPanelWindow.IsOpen = true;
        ImGui.SameLine();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, $"{shown}/{C.Favorites.Count}");
        ImGuiEx.Tooltip("Destinations shown / entries in your favorites. They differ when some favorites are not attuned on this character, or belong to the residential / Eureka aethernet lists in the overlay - see \"Not available\" at the bottom.".Loc());
        TeleportRowUI.DrawDirectWriteToggle();

        if(EditMode)
        {
            ImGui.SetNextItemWidth(160f.Scale());
            ImGui.InputTextWithHint("##LifestreamFavNewCat", "New category...".Loc(), ref NewCategoryName, 60);
            ImGui.SameLine();
            if(ImGuiEx.Button("Add".Loc(), NewCategoryName.Trim() != ""))
            {
                FavoriteOrganizer.AddCategory(NewCategoryName.Trim());
                NewCategoryName = "";
            }
        }
    }

    /// <summary>
    /// 一個分類的標頭與它底下的項目。
    /// 空的分類只在整理模式下畫標頭 —— 平常不佔版面，但整理時要看得到才拖得進去。
    /// </summary>
    private void DrawCategory(uint categoryId, string name, List<TeleportPanelEntry> items)
    {
        if(items.Count == 0 && !EditMode) return;

        ImGui.PushID($"cat{categoryId}");

        // ⚠️ CollapsingHeader 預設吃滿整個可用寬度，接在它後面的 SameLine 會把按鈕推到看不見的
        // 地方。所以編輯用的按鈕畫在**前面**，標頭再接著佔用剩下的寬度。
        if(EditMode && categoryId != FavoriteOrganizer.Uncategorized)
        {
            if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowUp, "catup")) FavoriteOrganizer.MoveCategory(categoryId, -1);
            ImGuiEx.Tooltip("Move category up".Loc());
            ImGui.SameLine(0, 2);
            if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowDown, "catdown")) FavoriteOrganizer.MoveCategory(categoryId, 1);
            ImGuiEx.Tooltip("Move category down".Loc());
            ImGui.SameLine(0, 2);
            if(ImGuiEx.IconButton(FontAwesomeIcon.Pen, "catrename"))
            {
                RenamingCategory = categoryId;
                CategoryNameBuffer = name;
            }
            ImGuiEx.Tooltip("Rename category".Loc());
            ImGui.SameLine(0, 2);
            if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, "catdel", enabled: ImGuiEx.Ctrl)) FavoriteOrganizer.RemoveCategory(categoryId);
            // 刪分類只是把項目退回未分類，不會動到我的最愛本身 —— 講清楚才不會有人不敢按。
            ImGuiEx.Tooltip("Hold CTRL and click to delete this category. Its destinations move back to Uncategorized - nothing is removed from your favorites.".Loc());
            ImGui.SameLine();
        }

        var open = ImGui.CollapsingHeader($"{name} ({items.Count})###LifestreamFavCat{categoryId}", ImGuiTreeNodeFlags.DefaultOpen);

        if(EditMode && RenamingCategory == categoryId && categoryId != FavoriteOrganizer.Uncategorized)
        {
            ImGui.SetNextItemWidth(160f.Scale());
            if(ImGui.InputText("##LifestreamFavCatRename", ref CategoryNameBuffer, 60))
            {
                var target = FindCategory(categoryId);
                if(target != null)
                {
                    target.Name = CategoryNameBuffer;
                    EzConfig.Save();
                }
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("Done".Loc())) RenamingCategory = 0;
        }

        if(open)
        {
            ImGui.Indent();
            if(items.Count == 0)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "Empty - use the category selector on a destination to move it here.".Loc());
            }
            else
            {
                // 排序：有排過的照順序表，沒排過的落回字母序。
                var sorted = SortForDisplay(items);
                // 上下移動搬的是「一個 Aetheryte id」而不是「一列」：同一個 id 的多筆(房屋子索引)
                // 排名相同，會一起移動並保持相鄰。
                var ids = IdsBuffer;
                ids.Clear();
                IdsSeen.Clear();
                foreach(var x in sorted)
                {
                    if(IdsSeen.Add(x.Entry.Id)) ids.Add(x.Entry.Id);
                }
                foreach(var x in sorted) DrawRow(x.Entry, categoryId, ids);
            }
            ImGui.Unindent();
        }

        ImGui.PopID();
    }

    private void DrawRow(TeleportPanelEntry x, uint categoryId, List<uint> ids)
    {
        if(EditMode)
        {
            var idIndex = ids.IndexOf(x.Id);
            ImGui.PushID($"row{x.Id}_{x.SubIndex}");
            if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowUp, "up", enabled: idIndex > 0)) FavoriteOrganizer.Move(ids, idIndex, -1);
            ImGui.SameLine(0, 2);
            if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowDown, "down", enabled: idIndex >= 0 && idIndex < ids.Count - 1)) FavoriteOrganizer.Move(ids, idIndex, 1);
            ImGui.SameLine(0, 2);
            ImGui.SetNextItemWidth(120f.Scale());
            var cat = FavoriteOrganizer.GetCategory(x.Id);
            if(ImGuiEx.Combo("##cat", ref cat, CategoryIds, names: CategoryNames)) FavoriteOrganizer.SetCategory(x.Id, cat);
            ImGui.PopID();
            ImGui.SameLine();
        }

        // 🔴 idScope 帶上分類 id：同一個項目也會出現在傳送面板的兩個區塊裡，
        // 三處共用同一組 ImGui id 會讓右鍵選單被畫好幾遍(見 TeleportRowUI 的類別註解)。
        if(TeleportRowUI.Draw(x, IdScope(categoryId), showZone: true, selected: false))
        {
            TaskTeleportPanelGo.Enqueue(x);
        }
    }

    private void RebuildCategoryChoices()
    {
        CategoryIds.Clear();
        CategoryNames.Clear();
        CategoryIds.Add(FavoriteOrganizer.Uncategorized);
        CategoryNames[FavoriteOrganizer.Uncategorized] = "Uncategorized".Loc();
        foreach(var x in C.FavoriteCategories)
        {
            CategoryIds.Add(x.Id);
            CategoryNames[x.Id] = FavoriteOrganizer.CategoryName(x.Id);
        }
    }

    /// <summary>
    /// 我的最愛裡有、但這個視窗列不出來的 id。**「不知道」本身要看得見** ——
    /// 靜默少列幾筆會讓人以為我的最愛掉了。
    /// </summary>
    /// <remarks>⚠️ <see cref="Data.Config.Favorites"/> 是好幾個互不相干、範圍不重疊的 id 空間共用同一個 HashSet(既有的資料模型，不在這裡改)：
    ///   - <c>Aetheryte</c> 表：0..238 —— 傳送面板管的主力。列不出來＝還沒共鳴。
    ///   - <c>HousingAethernet</c> 表：1966080..1966162 —— 住宅區乙太之光，由浮動視窗管。
    ///   - 蒼天街 69420000..69420007 與玄關目的地(<c>uint.MaxValue</c>、<c>uint.MaxValue - 1</c>)：**傳送面板也管**，只有對應設定關掉時才會落到這裡。
    ///   - Lifestream 自訂 · 優雷卡/南方博茲雅/扎杜諾爾/新月島：69420100 起 —— 由浮動視窗管。
    /// 不屬於本視窗的那些**不是**「無法使用」，只是不歸這裡管，所以只報數量、不逐筆列，
    /// 更**不提供刪除** —— 在這裡刪掉會把使用者浮動視窗上的最愛弄不見。
    /// </remarks>
    private void DrawUnresolved(List<TeleportPanelEntry> entries)
    {
        var known = KnownIds;
        known.Clear();
        foreach(var x in entries) known.Add(x.Id);
        var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var notAttuned = NotAttuned;
        notAttuned.Clear();
        var otherSystem = 0;
        foreach(var id in C.Favorites)
        {
            if(known.Contains(id)) continue;
            if(sheet.HasRow(id)) notAttuned.Add(id);
            else otherSystem++;
        }
        if(notAttuned.Count == 0 && otherSystem == 0) return;

        if(!ImGui.CollapsingHeader($"? {"Not available".Loc()} ({notAttuned.Count + otherSystem})###LifestreamFavMissing")) return;
        ImGui.Indent();

        if(notAttuned.Count > 0)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "These favorites are not in this character's destination list - usually not attuned yet, or imported for another character. Nothing is lost; they reappear once unlocked.".Loc());
            uint toRemove = 0;
            foreach(var id in notAttuned)
            {
                var row = sheet.GetRowOrDefault(id);
                var name = C.Renames.TryGetValue(id, out var rn) && rn != "" ? rn
                    : row?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } pn ? pn
                    : row?.AethernetName.ValueNullable?.Name.ToString() is { Length: > 0 } an ? an
                    : $"#{id}";
                ImGuiEx.Text(ImGuiColors.DalamudGrey3, $"? {name}");
                ImGui.SameLine();
                if(ImGuiEx.SmallButton($"{"Remove".Loc()}##fav{id}", ImGuiEx.Ctrl)) toRemove = id;
                ImGuiEx.Tooltip("Hold CTRL and click to remove from favorites".Loc());
            }
            if(toRemove > 0)
            {
                C.Favorites.Remove(toRemove);
                FavoriteOrganizer.Forget(toRemove);
                S.Data.DataStore = new();
                EzConfig.Save();
            }
        }

        if(otherSystem > 0)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey3, $"{otherSystem} {"favorites belong to the residential / Eureka / Occult Crescent aethernet lists, which are managed from the Lifestream overlay - they are not shown or edited here.".Loc()}");
        }

        ImGui.Unindent();
    }
}
