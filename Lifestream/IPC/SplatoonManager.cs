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

    // 換幀只把游標歸零、元素沿用；Splatoon 重連後舊元素一律失效（Element.IsValid），整份重建。
    private unsafe void ResetOnFrameChange()
    {
        if(Cache.SplatoonVersion != Splatoon.Version) Reset();
        // Framework 是 isPointer: true 的靜態位址,合法回 null。拿不到就這一次不重設快取。
        var framework = CSFramework.Instance();
        if(framework == null) return;
        var frame = framework->FrameCounter;
        if(frame != Frame)
        {
            Frame = frame;
            Cache.WaymarkLinePos = 0;
            Cache.WaymarkPointPos = 0;
        }
    }

    public void RenderPath(IReadOnlyList<Vector3> path, bool addPlayer = true, bool addNumbers = false)
    {
        if(!Splatoon.IsConnected()) return;
        // Player.Object 在登出/切角色/區域轉換期間為 null,這裡擲例外會讓 ProgressOverlay.Draw() 進錯誤視窗狀態。
        // 拿不到玩家位置時,只是不畫「玩家 → 第一個路徑點」那一條線,其餘照畫。
        Vector3? playerPos = Player.Available ? Player.Object.Position : null;
        Vector3? prev = null;
        if(path != null && path.Count > 0)
        {
            for(var i = 0; i < path.Count; i++)
            {
                var point = NextPoint(addNumbers ? (i + 1).ToString() : "", false, out var pw);
                var pointKnown = pw.Known;
                pw.Known = false;
                WriteRef(point, pw, pointKnown, path[i]);
                pw.Known = true;
                var offCoord = prev ?? playerPos;
                var line = NextLine(false, out var lw);
                var lineKnown = lw.Known;
                lw.Known = false;
                WriteRef(line, lw, lineKnown, path[i]);
                WriteOff(line, lw, lineKnown, offCoord ?? path[i]);
                var color = (prev != null ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen).ToUint();
                if(!lineKnown || lw.Color != color) line.color = color;
                lw.Color = color;
                lw.Known = true;
                Splatoon.DisplayOnce(point);
                if(offCoord != null && (prev != null || addPlayer))
                {
                    Splatoon.DisplayOnce(line);
                }
                prev = path[i];
            }
        }
    }

    // 只寫和上一次不同的分量（逐位元比，-0 與 0 算不同）：ECommons Element 的 setter 每次反射＋裝箱。
    // 前提：Splatoon 只讀注入的元素、不回寫欄位。
    private static void WriteRef(Element e, SplatoonWritten w, bool known, Vector3 v)
    {
        if(!known || Differs(w.Ref.X, v.X)) e.refX = v.X;
        if(!known || Differs(w.Ref.Z, v.Z)) e.refY = v.Z;
        if(!known || Differs(w.Ref.Y, v.Y)) e.refZ = v.Y;
        w.Ref = v;
    }

    private static void WriteOff(Element e, SplatoonWritten w, bool known, Vector3 v)
    {
        if(!known || Differs(w.Off.X, v.X)) e.offX = v.X;
        if(!known || Differs(w.Off.Z, v.Z)) e.offY = v.Z;
        if(!known || Differs(w.Off.Y, v.Y)) e.offZ = v.Y;
        w.Off = v;
    }

    private static bool Differs(float a, float b) => BitConverter.SingleToUInt32Bits(a) != BitConverter.SingleToUInt32Bits(b);

    /// <summary>
    /// 交出的元素等同新建的：座標每次都要自己設；其餘欄位只能改這裡重設得回去的那幾個（radius、thicc、color）。
    /// </summary>
    public Element GetNextLine() => NextLine(true, out _);

    // callerMayChangeAnything=false 只給 RenderPath：它每次重設座標與 color，不碰其餘欄位，所以前後兩次都是它時免重設。
    private Element NextLine(bool callerMayChangeAnything, out SplatoonWritten written)
    {
        ResetOnFrameChange();
        var c = Cache;
        var pos = c.WaymarkLinePos++;
        Element ret;
        if(pos < c.WaymarkLineCache.Count)
        {
            ret = c.WaymarkLineCache[pos];
            written = c.WaymarkLineWritten[pos];
            if(callerMayChangeAnything) written.Known = false;
            if(callerMayChangeAnything || c.WaymarkLineTouched[pos])
            {
                ret.radius = 0f;
                ret.thicc = 1f;
                ret.color = c.LineDefaultColor;
            }
            c.WaymarkLineTouched[pos] = callerMayChangeAnything;
        }
        else
        {
            ret = new Element(ElementType.LineBetweenTwoFixedCoordinates)
            {
                radius = 0f,
                thicc = 1f,
            };
            if(pos == 0) c.LineDefaultColor = ret.color;
            c.WaymarkLineCache.Add(ret);
            c.WaymarkLineTouched.Add(callerMayChangeAnything);
            written = new();
            c.WaymarkLineWritten.Add(written);
        }
        return ret;
    }

    public Element GetNextLine(Vector4 color, float thickness)
    {
        var ret = GetNextLine();
        ret.color = color.ToUint();
        ret.thicc = thickness;
        return ret;
    }

    /// <summary>
    /// 交出的元素等同新建的：座標每次都要自己設；其餘欄位只能改這裡重設得回去的那幾個（radius、thicc、color、overlayVOffset、overlayText、Filled）。
    /// </summary>
    public Element GetNextPoint(string overlay = "") => NextPoint(overlay, true, out _);

    // callerMayChangeAnything=false 只給 RenderPath：它只設座標，overlayText 由這裡比對後才寫。
    private Element NextPoint(string overlay, bool callerMayChangeAnything, out SplatoonWritten written)
    {
        ResetOnFrameChange();
        var c = Cache;
        var pos = c.WaymarkPointPos++;
        Element ret;
        if(pos < c.WaymarkPointCache.Count)
        {
            ret = c.WaymarkPointCache[pos];
            written = c.WaymarkPointWritten[pos];
            if(callerMayChangeAnything) written.Known = false;
            if(callerMayChangeAnything || c.WaymarkPointTouched[pos])
            {
                ret.radius = 0f;
                ret.thicc = 3f;
                ret.color = ImGuiColors.DalamudRed.ToUint();
                ret.overlayVOffset = 1f;
                ret.Filled = false;
                ret.overlayText = overlay;
            }
            else if(c.WaymarkPointOverlay[pos] != overlay)
            {
                ret.overlayText = overlay;
            }
            c.WaymarkPointTouched[pos] = callerMayChangeAnything;
            c.WaymarkPointOverlay[pos] = overlay;
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
            c.WaymarkPointCache.Add(ret);
            c.WaymarkPointTouched.Add(callerMayChangeAnything);
            written = new();
            c.WaymarkPointWritten.Add(written);
            c.WaymarkPointOverlay.Add(overlay);
        }
        return ret;
    }
}
