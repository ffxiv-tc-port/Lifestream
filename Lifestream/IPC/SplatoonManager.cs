using ECommons.GameHelpers;
using ECommons.SplatoonAPI;

namespace Lifestream.IPC;
public class SplatoonManager
{
    private ulong Frame = 0;
    private SplatoonCache Cache = new();

    private SplatoonManager()
    {
        Splatoon.SetOnConnect(Reset);
        if(Splatoon.IsConnected()) Reset();
    }

    private void Reset()
    {
        Cache = new();
    }

    private unsafe void ResetOnFrameChange()
    {
        // Framework 是 isPointer: true 的靜態位址,合法回 null。拿不到就這一次不重設快取。
        var framework = CSFramework.Instance();
        if(framework == null) return;
        var frame = framework->FrameCounter;
        if(frame != Frame)
        {
            Frame = frame;
            Reset();
        }
    }

    public void RenderPath(IReadOnlyList<Vector3> path, bool addPlayer = true, bool addNumbers = false)
    {
        if(!Splatoon.IsConnected()) return;
        // Player.Object 在登出/切角色/區域轉換期間為 null(2026-08-26 實機 log 實證:
        // 本行的 NullReferenceException 讓 ProgressOverlay.Draw() 連兩幀擲例外,
        // 進而被 Dalamud 鎖進錯誤視窗狀態,使用者的進度條就此消失)。
        // 拿不到玩家位置時,只是不畫「玩家 → 第一個路徑點」那一條線,其餘照畫。
        // 注意原本的寫法在 addPlayer=false 時也會先解參考 Player.Object,所以那條路徑同樣會炸。
        Vector3? playerPos = Player.Available ? Player.Object.Position : null;
        Vector3? prev = null;
        if(path != null && path.Count > 0)
        {
            for(var i = 0; i < path.Count; i++)
            {
                var point = GetNextPoint(addNumbers ? (i + 1).ToString() : "");
                point.SetRefCoord(path[i]);
                var offCoord = prev ?? playerPos;
                var line = GetNextLine();
                line.SetRefCoord(path[i]);
                line.SetOffCoord(offCoord ?? path[i]);
                line.color = (prev != null ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen).ToUint();
                Splatoon.DisplayOnce(point);
                if(offCoord != null && (prev != null || addPlayer))
                {
                    Splatoon.DisplayOnce(line);
                }
                prev = path[i];
            }
        }
    }

    public Element GetNextLine()
    {
        ResetOnFrameChange();
        Element ret;
        if(Cache.WaymarkLineCache.Count < Cache.WaymarkLinePos)
        {
            ret = Cache.WaymarkLineCache[Cache.WaymarkLinePos];
        }
        else
        {
            ret = new Element(ElementType.LineBetweenTwoFixedCoordinates)
            {
                radius = 0f,
                thicc = 1f,
            };
            Cache.WaymarkLineCache.Add(ret);
        }
        Cache.WaymarkLinePos++;
        return ret;
    }

    public Element GetNextPoint(string overlay = "")
    {
        ResetOnFrameChange();
        Element ret;
        if(Cache.WaymarkPointCache.Count < Cache.WaymarkPointPos)
        {
            ret = Cache.WaymarkPointCache[Cache.WaymarkPointPos];
        }
        else
        {
            ret = new Element(ElementType.CircleAtFixedCoordinates)
            {
                radius = 0f,
                thicc = 3f,
                color = ImGuiColors.DalamudRed.ToUint(),
                overlayVOffset = 1f,
                overlayText = overlay,
                Filled = false,
            };
            Cache.WaymarkPointCache.Add(ret);
        }
        Cache.WaymarkPointPos++;
        return ret;
    }
}
