using Lifestream.Systems.Custom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lifestream.Data;
public unsafe class ZoneDetail
{
    /// <summary>
    /// 沒有特別指定時的互動距離。抽成常數是為了讓「這個區域沒有自訂值」的呼叫端有東西可引用，
    /// 不必再抄一次數字（欄位初始式讀不到）。
    /// </summary>
    public const float DefaultMaxInteractionDistance = 4.6f;

    public List<CustomAetheryte> Aetherytes = [];
    public float MaxInteractionDistance = DefaultMaxInteractionDistance;
    public List<string> GenericAetheryteNames = [];

    public ZoneDetail(List<CustomAetheryte> aetherytes)
    {
        Aetherytes = aetherytes;
    }

    public ZoneDetail(List<CustomAetheryte> aetherytes, float maxInteractionDistance)
    {
        Aetherytes = aetherytes;
        MaxInteractionDistance = maxInteractionDistance;
    }

    public ZoneDetail(List<CustomAetheryte> aetherytes, List<string> genericAetheryteNames) : this(aetherytes)
    {
        GenericAetheryteNames = genericAetheryteNames;
    }

    public ZoneDetail(List<CustomAetheryte> aetherytes, float maxInteractionDistance, List<string> genericAetheryteNames)
    {
        Aetherytes = aetherytes;
        MaxInteractionDistance = maxInteractionDistance;
        GenericAetheryteNames = genericAetheryteNames;
    }
}
