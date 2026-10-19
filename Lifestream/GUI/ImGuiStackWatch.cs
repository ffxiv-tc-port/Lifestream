using ECommons.SimpleGui;
using Lifestream.Systems;

namespace Lifestream.GUI;

/// <summary>
/// 「ImGui 文字全消失」的現場診斷：在 Lifestream 的繪製回呼前後各量一次 ImGui 全域樣式堆疊深度、
/// 文字色透明度與全域透明度，狀態有變才寫一行 Information。
/// </summary>
/// <remarks>
/// 契約：兩個回呼都跑在繪製執行緒，只讀不寫 ImGui 狀態。
/// 進入時堆疊不為 0＝Lifestream 的視窗之前就有人推了沒彈（負數＝彈過頭，ImGui 1.88 沒有下溢防護）；
/// 離開時與進入時不同＝Lifestream 自己這一幀推彈不成對。
/// </remarks>
internal static class ImGuiStackWatch
{
    private readonly record struct Probe(int Colors, int Vars, int Fonts, float TextAlpha, float Alpha);

    /// <summary>整個載入期間的行數上限：狀態每幀來回跳時不洗版。</summary>
    private const int MaxLines = 40;
    private const long MinIntervalMs = 2000;

    private static Probe entry;
    private static bool entryValid;
    private static string lastEntryIssue = "";
    private static string lastLeak = "";
    private static int linesLeft = MaxLines;

    /// <summary>要在 EzConfigGui.Init 之前訂閱，才會排在 Lifestream 的視窗前面執行。</summary>
    internal static void AttachBefore() => Svc.PluginInterface.UiBuilder.Draw += Before;

    internal static void AttachAfter() => Svc.PluginInterface.UiBuilder.Draw += After;

    internal static void Detach()
    {
        Svc.PluginInterface.UiBuilder.Draw -= Before;
        Svc.PluginInterface.UiBuilder.Draw -= After;
    }

    private static bool TryProbe(out Probe probe)
    {
        probe = default;
        var context = ImGui.GetCurrentContext();
        if(context.IsNull) return false;
        var style = ImGui.GetStyle();
        probe = new(context.ColorStack.Size, context.StyleVarStack.Size, context.FontStack.Size,
            style.Colors[(int)ImGuiCol.Text].W, style.Alpha);
        return true;
    }

    private static bool Visible(float alpha) => alpha >= 0.2f && alpha <= 1.001f;

    private static void Before()
    {
        entryValid = TryProbe(out entry);
        if(!entryValid) return;
        var healthy = entry.Colors == 0 && entry.Vars == 0 && entry.Fonts == 0 && Visible(entry.TextAlpha) && Visible(entry.Alpha);
        var issue = healthy ? "" : Describe(entry);
        if(issue == lastEntryIssue || !TravelDiag.ShouldLog("ImGuiStackWatch.entry", MinIntervalMs)) return;
        lastEntryIssue = issue;
        Write(healthy
            ? "[ImGui 診斷] Lifestream 開始繪製時的 ImGui 樣式狀態已恢復正常。"
            : $"[ImGui 診斷] Lifestream 開始繪製時 ImGui 樣式狀態就已異常：{issue}（是 Lifestream 的視窗之前留下的）。");
    }

    private static void After()
    {
        if(!entryValid || !TryProbe(out var exit)) return;
        var leak = SameState(entry, exit) ? "" : $"{Describe(entry)} → {Describe(exit)}";
        if(leak == lastLeak || !TravelDiag.ShouldLog("ImGuiStackWatch.leak", MinIntervalMs)) return;
        lastLeak = leak;
        Write(leak == ""
            ? "[ImGui 診斷] Lifestream 這一幀的 ImGui 樣式推彈又成對了。"
            : $"[ImGui 診斷] Lifestream 的視窗在這一幀改變了 ImGui 樣式狀態：{leak}；開著的 Lifestream 視窗：{OpenWindows()}。");
    }

    private static bool SameState(Probe a, Probe b)
        => a.Colors == b.Colors && a.Vars == b.Vars && a.Fonts == b.Fonts
        && a.TextAlpha.Equals(b.TextAlpha) && a.Alpha.Equals(b.Alpha);

    private static string Describe(Probe p)
        => $"顏色堆疊 {p.Colors}、樣式變數堆疊 {p.Vars}、字型堆疊 {p.Fonts}、文字色透明度 {p.TextAlpha:F3}、全域透明度 {p.Alpha:F3}";

    private static string OpenWindows()
    {
        var names = EzConfigGui.WindowSystem?.Windows.Where(x => x.IsOpen).Select(x => x.WindowName).ToList();
        return names == null || names.Count == 0 ? "無" : string.Join("、", names);
    }

    private static void Write(string line)
    {
        if(linesLeft <= 0) return;
        linesLeft--;
        PluginLog.Information(linesLeft == 0 ? $"{line}（ImGui 診斷已達這次載入的行數上限，之後不再記錄）" : line);
    }
}
