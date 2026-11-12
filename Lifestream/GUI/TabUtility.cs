using ECommons.GameHelpers;
using Lifestream.Paissa;
using Lumina.Excel.Sheets;
using System.Threading;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;

namespace Lifestream.GUI;
public static class TabUtility
{
    /// <summary>「抵達就關遊戲」的目標世界；0＝未武裝。只有使用者在 Utility 分頁能武裝，停止入口只會解除。</summary>
    public static int TargetWorldID = 0;

    /// <summary>
    /// 停止入口共用：解除「抵達就關遊戲」（倒數中也一併取消，倒數只在目標非 0 時跑）。
    /// 可在任何執行緒呼叫：IPC <c>Abort</c> 在呼叫端執行緒先解除，閘門逾時或卸載期不進框架執行緒也照樣生效。
    /// </summary>
    internal static void DisarmShutdown(string source)
    {
        var old = Interlocked.Exchange(ref TargetWorldID, 0);
        if(old == 0) return;
        // 解除已經生效；提示失敗不能擲回呼叫端（IPC Abort 接著還要中止任務鏈）。
        try
        {
            var name = Svc.Data.GetExcelSheet<World>().GetRowOrDefault((uint)old)?.Name.ToString();
            if(string.IsNullOrEmpty(name)) name = $"#{old}";
            PluginLog.Information($"[Lifestream] {source}：已解除「抵達世界後關閉遊戲」（目標＝{name}）。");
            ChatQueue.Yellow($"[Lifestream] {"Stopped: cancelled \"shutdown game upon arriving to ??\".".Loc(name)}");
        }
        catch(Exception e)
        {
            PluginLog.Warning($"[Lifestream] {source}：已解除「抵達世界後關閉遊戲」（目標 #{old}），但提示失敗：{e.Message}");
        }
    }
    private static WorldSelector WorldSelector = new()
    {
        DisplayCurrent = false,
        EmptyName = "Disabled".Loc(),
        // 去不了的世界永遠不會抵達，所以也不該被選成「抵達就關遊戲」的目標。
        ShouldHideWorld = (x) => x == Player.Object?.CurrentWorld.RowId || PublicWorlds.IsUnavailable(x)
    };
    private static PaissaImporter PaissaImporter = new();

    public static void Draw()
    {
        new NuiBuilder()
            .Section("Shutdown game upon arriving to the world".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                WorldSelector.Draw(ref TargetWorldID);
            })
            .Section("Import house listings from PaissaDB".Loc())
            .Widget(() =>
            {
                ImGuiEx.SetNextItemFullWidth();
                PaissaImporter.Draw();
            })
            .Draw();
    }
}
