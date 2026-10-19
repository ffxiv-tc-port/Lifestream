using Dalamud.Game.ClientState.Keys;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 「使用者這一幀有沒有在操作移動」：鍵盤 WASD／QE／方向鍵／空白鍵、滑鼠左右鍵同按、手把左搖桿（同 BOCCHI HandsOnProbe 的讀法）。
/// 只在框架執行緒用。自訂按鍵綁定讀不到；讀取擲例外就當成沒按、之後不再讀（＝退回「停在半路就不接續」）。
/// </summary>
internal static class MoveInputProbe
{
    /// <summary>手把左搖桿的值域約 ±99，超過這個值才算在推。</summary>
    private const int StickThreshold = 30;

    private static readonly VirtualKey[] Candidates =
    [
        VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.Q, VirtualKey.E,
        VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT, VirtualKey.SPACE,
    ];

    private static VirtualKey[] keys;
    private static bool mouseValid;
    private static bool broken;

    internal static bool Pressed()
    {
        if(broken) return false;
        try
        {
            var state = Svc.KeyState;
            if(keys == null)
            {
                keys = Candidates.Where(state.IsVirtualKeyValid).ToArray();
                mouseValid = state.IsVirtualKeyValid(VirtualKey.LBUTTON) && state.IsVirtualKeyValid(VirtualKey.RBUTTON);
            }
            foreach(var key in keys)
            {
                if(state[key]) return true;
            }
            if(mouseValid && state[VirtualKey.LBUTTON] && state[VirtualKey.RBUTTON]) return true;
            var stick = Svc.GamepadState.LeftStick;
            return Math.Abs(stick.X) >= StickThreshold || Math.Abs(stick.Y) >= StickThreshold;
        }
        catch(Exception ex)
        {
            broken = true;
            PluginLog.Information($"[Goto] 讀不到按鍵／手把狀態（{ex.GetType().Name}：{ex.Message}）：之後飛行被中斷時不再判斷手動操作、不接續。");
            return false;
        }
    }
}
