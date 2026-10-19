namespace Lifestream;


// 跨檔(與同檔多處)逐字重複的使用者可見字串收斂處。
// 🔴 `.Loc()` 是**拿英文原文當 key** 去查 LanguageChineseTraditional.ini:同一句被複製到兩個地方時,只要有人改了其中一份的英文,
//    那一份就查不到翻譯而**靜默**退回英文(不擲例外、不寫 log)。這裡的字面值就是 ini 的 key,改字等於丟掉翻譯。
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
