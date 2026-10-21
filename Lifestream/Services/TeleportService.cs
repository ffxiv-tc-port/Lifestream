using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lifestream.Systems;
using LogMessage = Lumina.Excel.Sheets.LogMessage;

namespace Lifestream.Services;
public unsafe class TeleportService
{
    private TeleportService() { }

    private readonly TeleportRefusalWatch refusal = new();

    /// <remarks><c>abortChainWhenRefused</c>＝false：被拒時不計入「中止任務鏈」的計時（IPC 呼叫端自己管重試）。</remarks>
    public bool TeleportToAetheryte(uint id, uint sub = 0, bool wait = false, bool abortChainWhenRefused = true)
    {
        if(!CanTeleport(out var err, out var status))
        {
            if(abortChainWhenRefused && Svc.Framework.IsInFrameworkUpdateThread && P.TaskManager.IsBusy)
            {
                refusal.Arm(status, Environment.TickCount64);
            }
            InternalLog.Warning(err);
            // InternalLog 只進 ECommons 的記憶體緩衝、不進 Dalamud 的記錄檔；呼叫端多半逐幀重試，所以要節流。
            if(TravelDiag.ShouldLog($"Teleport.refused.{id}.{sub}.{err}"))
            {
                PluginLog.Information($"[Teleport] 沒有送出傳送（乙太之光 {TravelDiag.AetheryteName(id)}({id})、sub {sub}）：{err}。");
            }
            return false;
        }
        foreach(var x in Svc.AetheryteList)
        {
            if(x.AetheryteId == id && x.SubIndex == sub)
            {
                refusal.Clear();
                // 送出之前先給 GilDelta 一個歸因提示：接下來自己錢包的那筆減少是傳送費。
                // 這裡是 Lifestream 唯一一個會扣錢的傳送收口（乙太網與住宅乙太網免費，不經過這裡），
                // 所以提示只要打在這一行，不必每個 UI 入口各打一次。純通知。
                IPC.GilDeltaIPC.HintTeleportFee(id, sub);
                var accepted = Telepo.Instance()->Teleport(id, (byte)sub);
                TravelDiag.Record($"送出傳送：{TravelDiag.AetheryteName(id)}({id})、sub {sub}");
                PluginLog.Information($"[Teleport] 送出傳送：乙太之光 {TravelDiag.AetheryteName(id)}({id})、sub {sub}，遊戲回傳 {accepted}。");
                if(wait)
                {
                    P.TaskManager.InsertMulti(
                        new(() => !IsScreenReady()),
                        new(() => IsScreenReady() && Player.Interactable)
                        );
                }
                return true;
            }
        }
        InternalLog.Warning($"Could not find teleport destination for {id}");
        if(TravelDiag.ShouldLog($"Teleport.notfound.{id}.{sub}"))
        {
            PluginLog.Information($"[Teleport] 沒有送出傳送：已解鎖的傳送清單裡沒有乙太之光 {TravelDiag.AetheryteName(id)}({id})、sub {sub}。");
        }
        return false;
    }

    /// <summary>
    /// 框架執行緒每幀呼叫：任務鏈裡的傳送被遊戲以非暫時性的理由拒絕、持續約 3 秒，就中止任務鏈並在聊天欄說明。
    /// 沒有待判的拒絕時不碰遊戲記憶體。
    /// </summary>
    internal void Tick()
    {
        if(!refusal.Armed) return;
        var busy = P.TaskManager.IsBusy;
        var status = 0u;
        if(busy) CanTeleport(out _, out status);
        if(!refusal.Observe(status, busy, Environment.TickCount64, out var elapsedMs)) return;
        var refused = refusal.Status;
        refusal.Clear();
        var gameText = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(refused)?.Text.ExtractText();
        if(string.IsNullOrWhiteSpace(gameText)) gameText = "Game status code ??.".Loc(refused);
        PluginLog.Information($"[Teleport] 傳送被遊戲拒絕已 {elapsedMs / 1000.0:F1} 秒（狀態 {refused}「{gameText}」），不再等待、中止任務鏈。");
        TravelDiag.LogAbort($"傳送被遊戲拒絕（狀態 {refused}）");
        Movement.TravelStop.StopAll("傳送被遊戲拒絕");
        ChatQueue.Red($"[Lifestream] {"Teleport refused by the game: ?? Stopped the remaining steps.".Loc(gameText)}");
    }

    public static bool CanTeleport(out string error) => CanTeleport(out error, out _);

    /// <remarks><c>status</c>＝遊戲的拒絕狀態碼（LogMessage 列號）；0＝不是遊戲狀態碼造成的（沒有角色、動畫鎖）或可以傳送。</remarks>
    public static bool CanTeleport(out string error, out uint status)
    {
        error = null;
        status = 0;
        if(!Player.Interactable)
        {
            error = ("Can't teleport - no player");
            return false;
        }
        status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 5);
        if(status != 0)
        {
            error = $"Can't execute teleport action (status {status})";
            return false;
        }
        if(Player.IsAnimationLocked)
        {
            error = ("Can't teleport - animation locked");
            return false;
        }
        return true;
    }
}
