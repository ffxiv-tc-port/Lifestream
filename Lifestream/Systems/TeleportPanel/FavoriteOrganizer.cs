using ECommons.Configuration;
using Lifestream.Data;

namespace Lifestream.Systems.TeleportPanel;

/// <summary>
/// 我的最愛的排序與分類。
///
/// 設計前提(不要改)：
///   - <see cref="Config.Favorites"/> 是既有的 <c>HashSet&lt;uint&gt;</c>，浮動視窗、<c>/li</c> 指令、
///     <see cref="Legacy.DataStore"/> 的排序都在讀它 —— **這裡完全不動它**，
///     只讀「某個 id 在不在我的最愛裡」。
///   - 順序表(<see cref="Config.FavoriteOrder"/>)是一份**排名**而不是成員名單：
///     沒被列出來的我的最愛不會消失，只是排在後面、彼此照字母序。
///     這讓新欄位是純加法 —— 舊設定檔第一次載入時兩張表都是空的，
///     看到的順序就跟改動前的字母序一模一樣。
/// </summary>
internal static class FavoriteOrganizer
{
    /// <summary>「未分類」的分類 Id。真正的分類一律從 1 起跳。</summary>
    public const uint Uncategorized = 0;

    public static uint AddCategory(string name)
    {
        var id = 1u;
        foreach(var x in C.FavoriteCategories)
        {
            if(x.Id >= id) id = x.Id + 1;
        }
        C.FavoriteCategories.Add(new() { Id = id, Name = name });
        EzConfig.Save();
        return id;
    }

    public static bool CategoryExists(uint id) => id != Uncategorized && C.FavoriteCategories.Any(x => x.Id == id);

    public static string CategoryName(uint id)
    {
        foreach(var x in C.FavoriteCategories)
        {
            if(x.Id == id) return x.Name == "" ? $"#{x.Id}" : x.Name;
        }
        return "Uncategorized".Loc();
    }

    /// <summary>
    /// 某個我的最愛屬於哪個分類。歸屬指向已被刪除的分類時回「未分類」——
    /// 所以刪分類不需要另外清歸屬表，項目只會回到未分類，不會消失。
    /// </summary>
    public static uint GetCategory(uint aetheryteId)
        => C.FavoriteCategoryAssignment.TryGetValue(aetheryteId, out var cat) && CategoryExists(cat) ? cat : Uncategorized;

    public static void SetCategory(uint aetheryteId, uint categoryId)
    {
        if(categoryId == Uncategorized || !CategoryExists(categoryId)) C.FavoriteCategoryAssignment.Remove(aetheryteId);
        else C.FavoriteCategoryAssignment[aetheryteId] = categoryId;
        EzConfig.Save();
    }

    public static void RemoveCategory(uint categoryId)
    {
        C.FavoriteCategories.RemoveAll(x => x.Id == categoryId);
        // 歸屬表刻意**不清**：GetCategory 查不到分類就當未分類，項目自動回到未分類。
        // 真正會累積孤兒的是「取消我的最愛」那條路，由 Forget 處理。
        EzConfig.Save();
    }

    public static void MoveCategory(uint categoryId, int delta)
    {
        var i = C.FavoriteCategories.FindIndex(x => x.Id == categoryId);
        if(i < 0) return;
        var j = i + delta;
        if(j < 0 || j >= C.FavoriteCategories.Count) return;
        (C.FavoriteCategories[i], C.FavoriteCategories[j]) = (C.FavoriteCategories[j], C.FavoriteCategories[i]);
        EzConfig.Save();
    }

    /// <summary>
    /// 排序鍵。在順序表裡的照表；不在的一律 <see cref="int.MaxValue"/>，
    /// 由呼叫端再用名稱當第二鍵 —— 也就是「沒排過的落回字母序」。
    /// </summary>
    public static int Rank(uint aetheryteId)
    {
        var i = C.FavoriteOrder.IndexOf(aetheryteId);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>
    /// 在同一個分類內把某個項目上/下移一格。
    /// <paramref name="idsInDisplayOrder"/> 是該分類**目前顯示的**去重 id 清單。
    ///
    /// 順序表是全域的一份排名，但排序永遠只在同一個分類內部比較，
    /// 所以做法是「把這個分類的項目整批從表中移除、再依新順序接到表尾」——
    /// 其他分類的相對順序完全不受影響，也不必處理交錯。
    /// </summary>
    public static void Move(List<uint> idsInDisplayOrder, int index, int delta)
    {
        var target = index + delta;
        if(index < 0 || index >= idsInDisplayOrder.Count) return;
        if(target < 0 || target >= idsInDisplayOrder.Count) return;
        var reordered = new List<uint>(idsInDisplayOrder);
        (reordered[index], reordered[target]) = (reordered[target], reordered[index]);
        C.FavoriteOrder.RemoveAll(reordered.Contains);
        C.FavoriteOrder.AddRange(reordered);
        EzConfig.Save();
    }

    /// <summary>
    /// 🔴 從我的最愛移除時**必須**呼叫這個。
    /// 不清的話順序表與歸屬表會一直累積再也對不到任何我的最愛的孤兒 id，
    /// 而且是靜默累積 —— 設定檔會慢慢長大，重新加回同一個地點時還會撿到舊排名。
    /// </summary>
    public static void Forget(uint aetheryteId)
    {
        var changed = C.FavoriteOrder.Remove(aetheryteId);
        changed |= C.FavoriteCategoryAssignment.Remove(aetheryteId);
        if(changed) EzConfig.Save();
    }

    /// <summary>
    /// 我的最愛被取消之後的收尾。呼叫端在 <see cref="Config.Favorites"/> 改完之後叫它，
    /// 只有真的已經不在我的最愛裡才會清 —— 重複呼叫是安全的。
    /// </summary>
    public static void PruneIfNotFavorite(uint aetheryteId)
    {
        if(!C.Favorites.Contains(aetheryteId)) Forget(aetheryteId);
    }
}
