using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Tasks.Shortcuts;

/// <summary>
/// 傳送面板／我的最愛的「直接進無人島」「直接進渴望灣」：使用者按下才排入與 <c>/li island</c>、<c>/li cosmic</c>
/// 完全相同的流程，不另抄步驟；中止一律走既有的佇列中止（右鍵進度條、<c>/li stop</c>、IPC <c>Abort</c>）。
/// <see cref="Tick"/> 在佇列清空時記一行有沒有抵達。只在框架執行緒使用。
/// </summary>
public static class TaskDirectEnter
{
    private const string IslandTag = "[直接進無人島]";
    private const string CosmicTag = "[直接進渴望灣]";

    private static string ActiveTag;
    private static string ActiveName;
    private static uint ActiveTerritory;

    // 延後到第一次畫面板才算：Tick 每幀碰本類別，不要讓它在插件啟動的第一幀就去反序列化 StaticAlias。
    private static uint? islandAetheryteId;

    /// <summary><c>/li island</c> 第一步傳送的主水晶（莫拉比造船廠）；按鈕畫在這一列。</summary>
    public static uint IslandAetheryteId => islandAetheryteId ??= StaticAlias.IslandSanctuary.Commands.FirstOrDefault(c => c.Kind == CustomAliasKind.Teleport_to_Aetheryte)?.Aetheryte ?? 0;

    public static bool IsIslandEntryRow(TeleportPanelEntry x) => x.IsAetheryte && x.SubIndex == 0 && x.Id != 0 && x.Id == IslandAetheryteId;

    /// <summary>渴望灣的玄關主水晶（最佳威兔洞）。渴望灣那一列本身點下去已經會走乙太之光選單，所以按鈕畫在玄關這列。</summary>
    public static bool IsCosmicEntryRow(TeleportPanelEntry x) => x.IsAetheryte && x.SubIndex == 0 && x.Id == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId;

    public static void EnqueueIsland()
    {
        if(Utils.IsBusy())
        {
            Notify.Error("Lifestream is busy".Loc());
            return;
        }
        if(!Player.Available) return;
        PluginLog.Information($"{IslandTag} 開始：目前區域 {P.Territory}，{(Player.IsInHomeWorld ? "在原始世界" : "不在原始世界、先回原始世界")}。");
        TaskISShortcut.Enqueue();
        Begin(IslandTag, "無人島", TaskISShortcut.IslandTerritories.Island);
    }

    public static void EnqueueCosmic()
    {
        if(Utils.IsBusy())
        {
            Notify.Error("Lifestream is busy".Loc());
            return;
        }
        if(!Player.Available) return;
        if(P.Territory == TaskAetheryteAethernetTeleport.SinusArdorumTerritoryId)
        {
            Notify.Info("You are already in Sinus Ardorum.".Loc());
            return;
        }
        PluginLog.Information($"{CosmicTag} 開始：目前區域 {P.Territory}，{(Player.IsInHomeWorld ? "在原始世界" : "不在原始世界、先回原始世界")}。");
        TaskCosmicShortcut.EnqueueReturnHomeFirst();
        Begin(CosmicTag, "渴望灣", TaskAetheryteAethernetTeleport.SinusArdorumTerritoryId);
    }

    private static void Begin(string tag, string name, uint territory)
    {
        if(!P.TaskManager.IsBusy)
        {
            PluginLog.Information($"{tag} 沒有排入任何步驟（詳見上方記錄），未開始。");
            return;
        }
        ActiveTag = tag;
        ActiveName = name;
        ActiveTerritory = territory;
    }

    /// <summary>每幀呼叫。佇列清空時記一行結果；不碰移動（無人島流程最後交給跟隨路徑走到 NPC，佇列空了路徑還在走）。</summary>
    internal static void Tick()
    {
        if(ActiveTag == null || P.TaskManager.IsBusy) return;
        var tag = ActiveTag;
        ActiveTag = null;
        var territory = P.Territory;
        if(territory == ActiveTerritory)
            PluginLog.Information($"{tag} 已抵達{ActiveName}（區域 {territory}）。");
        else
            PluginLog.Information($"{tag} 流程已結束但沒有抵達{ActiveName}（目前區域 {territory}）：被中止（右鍵進度條／停止／急停）、逾時或找不到目標，詳見上方記錄；接下來請手動操作。");
    }
}
