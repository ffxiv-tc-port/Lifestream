using ECommons.SimpleGui;
using ECommons.SplatoonAPI;

namespace Lifestream.GUI;

public class ProgressOverlay : Window
{
    /// <summary>
    /// 只在「連續失敗的第一次」記錄,避免每幀刷 log。成功一次就歸零。
    /// </summary>
    private bool SplatoonRenderFailureLogged = false;

    public ProgressOverlay() : base("Lifestream progress overlay", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize, true)
    {
        EzConfigGui.WindowSystem.AddWindow(this);
        IsOpen = true;
        RespectCloseHotkey = false;
    }

    /// <summary>
    /// 手動重置進度條視窗。
    ///
    /// 為什麼要「換一個新實例」而不是把 IsOpen 設回 true 就好:
    /// Dalamud 的 <see cref="Window"/> 在 Draw() 擲例外時會把視窗鎖進私有的 hasError 狀態,
    /// 並在畫面上換成錯誤面板。10 秒內連兩次例外會讓自動重試被關閉(autoRetrySuppressed),
    /// 此後只剩手動按鈕;而錯誤面板上的「Close Window」會把 IsOpen 設成 false —— 這兩個狀態
    /// 外掛都無法從外部清除(hasError 是 private,沒有公開的重設入口),結果就是進度條永久消失。
    /// 丟掉舊實例、註冊一個全新的,是唯一能同時清掉這兩種狀態的做法。
    ///
    /// 執行緒/迭代安全:WindowSystem.Draw() 迭代的是 windows.ToArray() 的快照,
    /// 因此在繪製途中(例如設定視窗的按鈕回呼裡)增刪視窗是安全的;新視窗會從下一幀開始參與繪製。
    /// AddWindow() 會拒絕同名視窗,所以必須先 RemoveWindow 再建構。
    /// </summary>
    public static void ResetOverlay()
    {
        var old = S.Gui.ProgressOverlay;
        if(old != null)
        {
            EzConfigGui.WindowSystem.RemoveWindow(old);
        }
        S.Gui.ProgressOverlay = new ProgressOverlay();
        PluginLog.Information($"[ProgressOverlay] 進度條已手動重置(舊實例已移除={old != null}, 隱藏進度條={C.NoProgressBar}, 顯示於頂部={C.ProgressOverlayToTop})");
    }

    public override void PreDraw()
    {
        // 漏 base.PreDraw() 會讓 Dalamud 的每視窗不透明度靜默失效(本類沒有 override PostDraw,
        // base.PostDraw 本來就會跑,所以只缺這一邊)。
        base.PreDraw();
        SizeConstraints = new()
        {
            MinimumSize = new(ImGuiHelpers.MainViewport.Size.X, 0),
            MaximumSize = new(0, float.MaxValue)
        };
    }

    public override void Draw()
    {
        CImGui.igBringWindowToDisplayBack(CImGui.igGetCurrentWindow());
        if(ImGui.IsWindowHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Right click to stop all tasks and movement".Loc());
            if(ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                P.TaskManager.Abort();
                P.followPath?.Stop();
            }
        }
        float percent;
        Vector4 col;
        string overlay;
        if(P.followPath != null && P.followPath.Waypoints.Count > 0)
        {
            percent = 1f - (float)P.FollowPath.Waypoints.Count / (float)P.FollowPath.MaxWaypoints;
            col = GradientColor.Get(EColor.Red, EColor.Violet);
            overlay = $"Lifestream Movement: {P.FollowPath.MaxWaypoints - P.FollowPath.Waypoints.Count}/{P.FollowPath.MaxWaypoints}";
            if(Splatoon.IsConnected())
            {
                // 這裡擲出的例外會被 Dalamud 鎖進「錯誤視窗」狀態,讓整條進度條永久消失
                // (而且外掛清不掉,見 ResetOverlay 的說明)。此處尚未 Push 任何 ImGui 樣式,
                // 吞掉例外不會弄壞樣式堆疊,所以在這裡隔離是安全的。
                try
                {
                    S.Ipc.SplatoonManager.RenderPath(P.FollowPath.Waypoints);
                    SplatoonRenderFailureLogged = false;
                }
                catch(Exception e)
                {
                    if(!SplatoonRenderFailureLogged)
                    {
                        SplatoonRenderFailureLogged = true;
                        PluginLog.Warning($"[ProgressOverlay] Splatoon 路徑繪製失敗,已略過以保住進度條: {e}");
                    }
                }
            }
        }
        else
        {
            percent = 1f - (float)P.TaskManager.NumQueuedTasks / (float)P.TaskManager.MaxTasks;
            col = EColor.Violet;
            overlay = $"Lifestream Progress: {P.TaskManager.MaxTasks - P.TaskManager.NumQueuedTasks}/{P.TaskManager.MaxTasks}";
        }
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, col);
        ImGui.ProgressBar(percent, new(ImGui.GetContentRegionAvail().X, 20), overlay);
        ImGui.PopStyleColor();
        // Toggle ProgressOverlay position logic
        // 註:位置每幀從目前的 MainViewport 重算,不會被持久化,所以不可能卡在畫面外。
        if(C.ProgressOverlayToTop)
        {
            Position = new(0, 0);
        }
        else
        {
            Position = new(0, ImGuiHelpers.MainViewport.Size.Y - ImGui.GetWindowSize().Y);
        }
    }

    public override bool DrawConditions()
    {
        //return ((P.TaskManager.IsBusy && P.TaskManager.MaxTasks > 0)) && !C.NoProgressBar;
        return ((P.TaskManager.IsBusy && P.TaskManager.MaxTasks > 0) || (P.followPath != null && P.followPath.Waypoints.Count > 0)) && !C.NoProgressBar;
    }
}
