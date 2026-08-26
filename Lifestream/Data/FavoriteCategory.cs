namespace Lifestream.Data;

/// <summary>
/// 我的最愛的自訂分類。
///
/// 刻意用數字 <see cref="Id"/> 當鍵而不是用名稱：
///   - 改名時不必回頭修每一筆歸屬(<see cref="Config.FavoriteCategoryAssignment"/>)；
///   - 兩個同名分類不會互相吃掉對方的項目。
/// 分類被刪除時**不需要**清理歸屬表：
/// <see cref="Systems.TeleportPanel.FavoriteOrganizer.GetCategory"/> 查不到分類就當「未分類」，
/// 所以刪分類永遠只是把項目放回未分類，不會讓它們消失。
/// </summary>
public class FavoriteCategory
{
    /// <summary>1 起跳。0 保留給「未分類」，永遠不會被指派給真正的分類。</summary>
    public uint Id = 0;

    public string Name = "";
}
