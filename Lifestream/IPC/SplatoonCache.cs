using ECommons.SplatoonAPI;

namespace Lifestream.IPC;
public class SplatoonCache
{
    public List<Element> WaymarkLineCache = [];
    public int WaymarkLinePos = 0;
    public List<Element> WaymarkPointCache = [];
    public int WaymarkPointPos = 0;
    // 與上面兩份逐格對應：true＝這一格上一次是經公開的 GetNextLine/GetNextPoint 交出去的，呼叫端可能改過任何欄位。
    public List<bool> WaymarkLineTouched = [];
    public List<bool> WaymarkPointTouched = [];
    public List<string> WaymarkPointOverlay = [];
    // 與上面兩份逐格對應：RenderPath 上一次寫進該元素的座標與顏色（Known=false＝不知道，下次全寫）。
    public List<SplatoonWritten> WaymarkLineWritten = [];
    public List<SplatoonWritten> WaymarkPointWritten = [];
    public uint LineDefaultColor;
    public int SplatoonVersion = Splatoon.Version;
}

public sealed class SplatoonWritten
{
    public bool Known;
    public Vector3 Ref;
    public Vector3 Off;
    public uint Color;
}
