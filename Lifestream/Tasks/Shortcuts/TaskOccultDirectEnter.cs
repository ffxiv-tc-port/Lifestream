using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Systems.TeleportPanel;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Shortcuts;

/// <summary>
/// 傳送面板的「直接進島」：逐步執行 <see cref="StaticAlias.OccultCrescent"/>（與 <c>/li occult</c> 同一份步驟），
/// 並在每一步記下目前是哪一步；關鍵步驟之後檢查結果，不成立就寫一行 Information、清空佇列停下。
/// 佇列提前清空（逾時中止、找不到目標、右鍵進度條）由 <see cref="Tick"/> 補記是停在哪一步。
/// 只在框架執行緒使用（按鈕與 Framework_Update）。
/// </summary>
public static class TaskOccultDirectEnter
{
    // Warp 表寫的是 1269，台服 7.25 實際落在 1278（同一張地圖、地名都是蜃景鎮）。
    private static readonly uint[] MirageTownTerritories = [1269, 1278];
    private static readonly uint[] SouthHornTerritories = [1252];
    private const float NpcNearDistance = 5f;

    private static bool Active;
    private static bool Reported;
    private static int CurrentStep = -1;
    private static string CurrentLabel = "";
    private static int TimedOutStep = -1;
    private static bool TimeoutHooked;

    // 延後到第一次畫面板才算：Tick 每幀碰本類別的靜態欄位，不要讓它在插件啟動的第一幀就去反序列化 StaticAlias。
    private static uint? aethernetId;

    /// <summary>這條路線第二步搭的城內乙太之光；傳送面板只在這一列旁邊畫按鈕。</summary>
    public static uint AethernetId => aethernetId ??= StaticAlias.OccultCrescent.Commands.FirstOrDefault(c => c.Kind == CustomAliasKind.Use_Aethernet)?.Aetheryte ?? 0;

    public static bool IsEntryRow(TeleportPanelEntry x) => !x.IsAetheryte && x.SubIndex == 0 && x.Id != 0 && x.Id == AethernetId;

    public static void Enqueue()
    {
        if(Utils.IsBusy())
        {
            Notify.Error("Lifestream is busy".Loc());
            return;
        }
        if(!Player.Available) return;
        var alias = StaticAlias.OccultCrescent;
        var cmds = alias.Commands;
        var aethernetIdx = cmds.FindIndex(c => c.Kind == CustomAliasKind.Use_Aethernet);
        var warpIdx = cmds.FindIndex(c => c.Kind == CustomAliasKind.Select_Yes);
        var npcIdx = cmds.FindLastIndex(c => c.Kind == CustomAliasKind.Interact);
        if(cmds.Count == 0 || cmds[0].Kind != CustomAliasKind.Teleport_to_Aetheryte || aethernetIdx < 0 || warpIdx < aethernetIdx || npcIdx < warpIdx)
        {
            PluginLog.Error("[直接進島] 內建的新月島路線步驟不是預期的形狀，不執行。");
            return;
        }
        var cityTerritory = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(cmds[0].Aetheryte)?.Territory.RowId ?? 0;
        var labels = cmds.Select(Describe).ToArray();

        var territory = Player.Territory;
        if(SouthHornTerritories.Contains(territory))
        {
            Notify.Info("You are already in the Occult Crescent.".Loc());
            return;
        }
        int start;
        if(MirageTownTerritories.Contains(territory))
            start = IsObjectNear(cmds[npcIdx].DataID) ? npcIdx : warpIdx + 1;
        // 站在這條路線要搭的那座城內乙太之光旁：對自己傳送不會成功，直接從走路那步開始。
        else if(territory == cityTerritory && P.ActiveAetheryte?.ID == cmds[aethernetIdx].Aetheryte)
            start = aethernetIdx + 1;
        else
            start = 0;

        if(!TimeoutHooked)
        {
            P.TaskManager.DefaultConfiguration.OnTaskTimeout += OnTaskTimeout;
            TimeoutHooked = true;
        }
        Active = true;
        Reported = false;
        CurrentStep = -1;
        CurrentLabel = "";
        TimedOutStep = -1;
        PluginLog.Information($"[直接進島] 開始：從第 {start + 1}/{cmds.Count} 步「{labels[start]}」起（目前區域 {territory}）。");

        for(var i = start; i < cmds.Count;)
        {
            var end = i + 1;
            if(cmds[i].Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point, CustomAliasKind.Circular_movement))
            {
                // 與 CustomAlias.Enqueue 相同的併段規則，連續的移動點仍走成一條路徑。
                while(end < cmds.Count && cmds[end].Kind == CustomAliasKind.Move_to_point && alias.IsChainedWithNext(end - 1)) end++;
            }
            var step = i;
            if(step == aethernetIdx + 1 && start <= aethernetIdx)
                EnqueueExpectTerritory(aethernetIdx, labels, [cityTerritory], 10000, "搭完城內乙太之光後不在圖萊尤拉");
            if(step == warpIdx + 1 && start <= warpIdx)
                EnqueueExpectTerritory(warpIdx, labels, MirageTownTerritories, 30000, "30 秒內沒有抵達蜃景鎮");
            P.TaskManager.Enqueue(() =>
            {
                CurrentStep = step;
                CurrentLabel = labels[step];
            }, $"OccultDirectEnter: step {step + 1}");
            alias.Enqueue(true, i, end);
            // 這兩種步驟逾時不中止佇列，只會被略過；略過就代表選單沒按到，不能接著往下走。
            if(cmds[step].Kind.EqualsAny(CustomAliasKind.Select_Yes, CustomAliasKind.Select_List_Option))
            {
                P.TaskManager.Enqueue(() => TimedOutStep == step ? Fail(step, labels, "對話框沒有出現或選項對不上（詳見上方 [CustomAlias] 那行）") : true,
                    $"OccultDirectEnter: verify step {step + 1}");
            }
            i = end;
        }
        EnqueueExpectTerritory(cmds.Count - 1, labels, SouthHornTerritories, 60000, "60 秒內沒有進入新月島（進入確認沒出現、還在排隊，或選單沒按到）");
        P.TaskManager.Enqueue(() =>
        {
            Active = false;
            Reported = true;
            PluginLog.Information("[直接進島] 已進入新月島。");
        }, "OccultDirectEnter: done");
    }

    /// <summary>每幀呼叫。佇列在走完之前就空了（中止或逾時）時，記一行停在哪一步。</summary>
    internal static void Tick()
    {
        if(!Active || P.TaskManager.IsBusy) return;
        Active = false;
        if(Reported) return;
        P.followPath?.Stop();
        var where = CurrentStep < 0 ? "第一步開始前" : $"第 {CurrentStep + 1} 步「{CurrentLabel}」";
        var why = TimedOutStep >= 0 && TimedOutStep == CurrentStep ? "這一步逾時" : "任務被中止（找不到目標、逾時，或手動右鍵進度條／停止）";
        PluginLog.Information($"[直接進島] 停在{where}：{why}，沒有進入新月島；接下來請手動操作。");
    }

    private static void OnTaskTimeout(TaskManagerTask task, ref long remainingTimeMS)
    {
        if(Active) TimedOutStep = CurrentStep;
    }

    private static void EnqueueExpectTerritory(int blameStep, string[] labels, uint[] territories, int waitMs, string reason)
    {
        long deadline = 0;
        P.TaskManager.Enqueue(() =>
        {
            if(deadline == 0) deadline = Environment.TickCount64 + waitMs;
            // 只看區域：換區在讀取一開始就生效，之後的「畫面就緒、可操作」由下一步自己等。
            if(territories.Contains(Player.Territory)) return true;
            return Environment.TickCount64 > deadline ? Fail(blameStep, labels, reason) : false;
        }, $"OccultDirectEnter: expect territory after step {blameStep + 1}", new(timeLimitMS: waitMs + 10000));
    }

    // 回傳 null＝要求 TaskManager 中止整條佇列。
    private static bool? Fail(int step, string[] labels, string reason)
    {
        Active = false;
        Reported = true;
        P.followPath?.Stop();
        PluginLog.Information($"[直接進島] 停在第 {step + 1} 步「{labels[step]}」：{reason}；已停止，接下來請手動操作。");
        return null;
    }

    private static bool IsObjectNear(uint dataId)
        => Svc.Objects.Any(o => o.BaseId == dataId && o.IsTargetable && Player.DistanceTo(o) <= NpcNearDistance);

    private static string Describe(CustomAliasCommand c) => c.Kind switch
    {
        CustomAliasKind.Teleport_to_Aetheryte => $"傳送到{Utils.KnownAetherytes.SafeSelect(c.Aetheryte, c.Aetheryte.ToString())}",
        CustomAliasKind.Use_Aethernet => $"城內乙太之光到{Utils.KnownAetherytes.SafeSelect(c.Aetheryte, c.Aetheryte.ToString())}",
        CustomAliasKind.Move_to_point => $"走到 {c.Point.X:F0}, {c.Point.Z:F0}",
        CustomAliasKind.Interact => $"和「{ObjectName(c.DataID)}」互動",
        CustomAliasKind.Select_Yes => $"確認框「{LastOption(c)}」按是",
        CustomAliasKind.Select_List_Option => $"選單選「{LastOption(c)}」",
        CustomAliasKind.Confirm_Contents_Finder => "任務搜尋器的進入確認",
        _ => c.Kind.ToString(),
    };

    private static string ObjectName(uint id)
    {
        if(Svc.Data.GetExcelSheet<EObjName>().TryGetRow(id, out var e)) return e.Singular.ExtractText();
        if(Svc.Data.GetExcelSheet<ENpcResident>().TryGetRow(id, out var n)) return n.Singular.ExtractText();
        return id.ToString();
    }

    private static string LastOption(CustomAliasCommand c)
        => c.SelectOption.Select(Utils.ParseSheetPattern).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
}
