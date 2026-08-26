namespace Lifestream;


// 跨檔(與同檔多處)逐字重複的使用者可見字串收斂處。
//
// 🔴 為什麼要收斂:`.Loc()` 是**拿英文原文當 key** 去查 LanguageChineseTraditional.ini,
//    而 ini 是字典、同一句只存一條。同一句被複製到兩個地方時,只要有人改了其中一份的英文,
//    那一份就查不到翻譯而**靜默**退回英文,另一份照樣顯示中文 —— 看起來像「漏翻一句」,
//    而不像「兩個複製品走散了」。查不到不會擲例外、也不會寫 log。
//    集中成常數之後,改一次兩邊一起改,key 也永遠只有一個。
//
// 🔑 這裡的每個值都是**原始碼 span 取代**搬過來的(逐字沿用原本的字面值,含跳脫序列),
//    所以執行期字串值逐位元組不變 -> ini 的 key 不變 -> 現有翻譯不受影響。
//    收斂當下已機械驗證這 13 條全部仍能在 ini 的 483 個 key 裡命中
//    (⚠️ 比對時要照 ECommons Localization.Init() 的做法先把 ini 行裡的 \n 還原成真換行,
//     否則含換行的兩條會假性判定成「ini 沒有」)。
//
// ⚠️ 這裡只放**真的出現在兩個以上位置**的字串;只用一次的留在使用處比較好讀。
// ⚠️ 內插字串 $"..." 的外層不能當 const,那類重複刻意不收斂(內插洞裡的內層字面值可以)。
public static class LocText {
    public const string CouldNotPasteFromClipboard = "Could not paste from clipboard:\n??";
    public const string CopyChatFriendlyName = "Copy chat-friendly name to clipboard";
    public const string HoldCtrlToDeleteEntry = "Hold CTRL and click to delete an entry";
    public const string ReorderOrMoveToOtherFolder = "Reorder or move to other folder";
    public const string GoToRegisteredPlotToEditPath = "Go to registered plot to edit path";
    public const string MemoryTeleportWarning = "WARNING - use at your own risk. This writes your character's coordinates straight into game memory to teleport you instantly. It is not something the normal client ever does, the server can detect it, and it may get your account actioned.";
    public const string MemoryTeleportRefusalNote = "It is refused automatically while in a duty, in combat, casting, or zoning, and falls back to walking.";
    public const string IntervalBetweenRetriesSeconds = "Interval between retries, seconds";
    public const string NoMapAvailableForDestination = "No map available for this destination.";
    public const string CustomLandingDisabled = "Custom landing is disabled in settings.";
    public const string CannotReachDestinationNoAetheryte = "Cannot reach destination - no unlocked aetheryte in target zone:";
    public const string VnavmeshNotInstalledFlagged = "vnavmesh is not installed - destination flagged on map, please walk there manually:";
    public const string VnavmeshNoPathToShard = "vnavmesh could not find a path to this aethernet shard:";
    public const string PointLeftClickToFinish = "Point: ??\nLeft-click to finish";
}
