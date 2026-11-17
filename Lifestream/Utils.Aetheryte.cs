using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons.Automation;
using ECommons.ChatMethods;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.ExcelServices.Sheets;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.Interop;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.Residential;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using NightmareUI;
using System;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using TerraFX.Interop.Windows;
using static FFXIVClientStructs.FFXIV.Client.UI.AddonAirShipExploration;
using Action = System.Action;
using CharaData = (string Name, ushort World);
using FXWindows = TerraFX.Interop.Windows.Windows;

namespace Lifestream;

internal static unsafe partial class Utils
{
    public static Dictionary<uint, string> KnownAetherytes
    {
        get
        {
            if(field == null)
            {
                field = [];
                foreach(var x in KnownAetherytesByCategories)
                {
                    foreach(var v in x.Value)
                    {
                        field[v.Key] = v.Value;
                    }
                }
            }
            return field;
        }
    }

    public static string GetAethernetNameWithOverrides(uint id)
    {
        // TC (Traditional Chinese) isn't one of the languages the bundled KnownAetherytes dataset was captured in,
        // so its names never match the TC client's aethernet menu ("Destination could not be found"). Try the live
        // Excel sheet first - it resolves in whatever language the running client's game data is - and only fall
        // back to the bundled dataset for entries that aren't real Aetheryte sheet rows (Firmament's synthetic ID).
        var live = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(id)?.AethernetName.Value.Name.GetText();
        if(!string.IsNullOrEmpty(live)) return live;
        if(Utils.KnownAetherytes.TryGetValue(id, out var ret)) return ret;
        return null;
    }

    public static IEnumerable<uint> GetAllRegisteredAethernetDestinations()
    {
        foreach(var x in S.Data.DataStore.Aetherytes)
        {
            yield return x.Key.ID;
            foreach(var v in x.Value)
            {
                yield return v.ID;
            }
        }
    }

    internal static IGameObject GetReachableMasterAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.Value.IsAetheryte, littleDistance);

    internal static IGameObject GetReachableWorldChangeAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae?.IsWorldChangeAetheryte() == true, littleDistance);

    internal static IGameObject GetReachableResidentialAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae?.IsResidentialAetheryte() == true, littleDistance);

    internal static IGameObject GetReachableAetheryte(Predicate<IGameObject> predicate, bool littleDistance = false)
    {
        if(!Player.Available) return null;
        var a = Svc.Objects.OrderBy(x => Vector3.DistanceSquared(Player.Object.Position, x.Position)).FirstOrDefault(x => predicate(x));
        if(a != null && a.IsTargetable && Vector3.Distance(a.Position, Player.Object.Position) < (littleDistance ? 13f : 30f))
        {
            return a;
        }
        return null;
    }

    /// <summary>
    /// 找出「屬於指定以太之光網路」且摸得到的節點——主水晶本身,或它底下任一個子節點(城內以太之光)。
    /// 用途:2.0 的城市各有兩張地圖(利姆薩上/下層甲板、烏爾達哈娜爾神殿/太陽神草原、格里達尼亞
    /// 新/舊街),兩張圖屬於同一個以太之光網路,但只有其中一張圖上有主水晶本體。人站在沒有主水晶
    /// 的那張圖時,<see cref="GetReachableMasterAetheryte"/> 永遠摸不到東西;這個方法改成只要是
    /// 同一個網路的任何節點都算,身邊摸得到就能直接互動、不必先傳送到另一張圖的主水晶。
    /// </summary>
    /// <param name="root">要找哪一個乙太網的節點。</param>
    /// <param name="excludeId">
    /// 要排除的節點 ID,通常是「這趟的目的地本身」。用目的地自己開選單再選自己會被遊戲以
    /// LogMessage 1478「此處為目前所在地。」拒絕,所以找可走節點時要把它排掉,讓流程退回原本的
    /// 「傳送到主水晶再走乙太網」。
    /// </param>
    internal static IGameObject GetReachableAethernetNetworkNode(TinyAetheryte root, uint? excludeId = null)
    {
        if(!Player.Available) return null;
        if(!S.Data.DataStore.Aetherytes.TryGetValue(root, out var children)) return null;
        var a = Svc.Objects.OrderBy(x => Vector3.DistanceSquared(Player.Object.Position, x.Position))
            .FirstOrDefault(x => Utils.TryGetTinyAetheryteFromIGameObjectLenient(x, out var ae)
                && ae.Value.ID != excludeId
                && (ae.Value.ID == root.ID || children.Any(c => c.ID == ae.Value.ID)));
        if(a != null && a.IsTargetable && Vector3.Distance(a.Position, Player.Object.Position) < AethernetNetworkNodeApproachRange)
        {
            return a;
        }
        return null;
    }

    /// <summary>
    /// 找出「指定的那一座乙太之光」在物件表裡的實體。找不到代表它沒載入(太遠,或根本不在這一區),
    /// 呼叫端必須要有退路。判定用寬鬆版,理由見 <see cref="TryGetTinyAetheryteFromIGameObjectLenient"/>。
    ///
    /// 🔴 回傳的 <see cref="IGameObject"/> **只在當下這一幀有效**,絕對不要跨幀保存 —— 呼叫端一律
    /// 每次重查(物件表的包裝是共用的,存取時會就地改寫 <c>Address</c>)。
    /// </summary>
    internal static IGameObject GetAethernetNodeObject(TinyAetheryte node)
    {
        if(!Player.Available) return null;
        if(node.TerritoryType != P.Territory) return null;
        return Svc.Objects.FirstOrDefault(x => Utils.TryGetTinyAetheryteFromIGameObjectLenient(x, out var ae) && ae.Value.ID == node.ID);
    }

    /// <summary>
    /// 目前 <see cref="Lifestream.ActiveAetheryte"/>(每幀依 <see cref="GetValidAetheryte"/> 更新,
    /// 只在真的站到節點旁邊才會設值)是否已經是指定以太之光網路裡的節點——主水晶本身或任一子節點。
    /// 用來判斷「走到能互動的節點了嗎」,不管走到的是主水晶還是網路裡的哪一個城內以太之光。
    /// </summary>
    /// <param name="root">要比對哪一個乙太網。</param>
    /// <param name="excludeId">
    /// 與 <see cref="GetReachableAethernetNetworkNode"/> 的同名參數同一個理由:走位途中剛好經過目的地
    /// 本身時,不要把它當成「已抵達可用節點」而拿它開選單(選自己會被拒絕)。
    /// </param>
    internal static bool IsActiveAetheryteInNetwork(TinyAetheryte root, uint? excludeId = null)
    {
        if(P.ActiveAetheryte == null) return false;
        if(P.ActiveAetheryte.Value.ID == excludeId) return false;
        if(P.ActiveAetheryte.Value.ID == root.ID) return true;
        return S.Data.DataStore.Aetherytes.TryGetValue(root, out var children) && children.Any(c => c.ID == P.ActiveAetheryte.Value.ID);
    }

    internal static AetheryteUseState CanUseAetheryte()
    {
        if(P.TaskManager.IsBusy || IsOccupied() || IsDisallowedToUseAethernet()) return AetheryteUseState.None;
        if(S.Data.DataStore.Territories.Contains(P.Territory) && P.ActiveAetheryte != null) return AetheryteUseState.Normal;
        if(S.Data.ResidentialAethernet.IsInResidentialZone() && S.Data.ResidentialAethernet.ActiveAetheryte != null) return AetheryteUseState.Residential;
        if(S.Data.CustomAethernet.ZoneInfo.ContainsKey(P.Territory) && S.Data.CustomAethernet.ActiveAetheryte != null) return AetheryteUseState.Custom;
        return AetheryteUseState.None;
    }

    internal static TinyAetheryte GetMaster()
    {
        return P.ActiveAetheryte.Value.GetMaster();
    }

    internal static TinyAetheryte GetMaster(this TinyAetheryte a)
    {
        return a.IsAetheryte ? a : S.Data.DataStore.GetMaster(a);
    }

    // 這兩支 Overlay 每幀都會呼叫：用 is 樣式比對，EqualsAny 每次都會配置 params 陣列與委派。
    internal static bool IsWorldChangeAetheryte(this TinyAetheryte t)
    {
        return t.ID is 2 or 8 or 9;
    }

    internal static bool IsResidentialAetheryte(this TinyAetheryte t)
    {
        return t.ID is 2 or 8 or 9 or 70 or 111;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns>Aetheryte city (Limsa, Uldah, etc)</returns>
    internal static uint GetTerritory(this ResidentialAetheryteKind r)
    {
        return TerritoryForResidentialAetheryte[r];
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns>Residential TerritoryType id (mist, goblet, etc)</returns>
    internal static uint GetResidentialTerritory(this ResidentialAetheryteKind r)
    {
        return ResidentialTerritoryForResidentialAetheryte[r];
    }

    internal static string GetName(this ResidentialAetheryteKind r)
    {
        return ExcelTerritoryHelper.Get(r.GetResidentialTerritory())?.PlaceName.ValueNullable?.Name.ToString();
    }

    internal static uint GetTerritory(this WorldChangeAetheryte r)
    {
        return TerritoryForWorldChangeAetheryte[r];
    }

    internal static ResidentialAetheryteKind? GetResidentialAetheryteByTerritoryType(uint territoryType)
    {
        var t = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
        if(t == null) return null;
        if(t.Value.PlaceNameRegion.RowId == 2402) return ResidentialAetheryteKind.Kugane;
        if(t.Value.PlaceNameRegion.RowId == 25) return ResidentialAetheryteKind.Foundation;
        if(t.Value.PlaceNameRegion.RowId == 23) return ResidentialAetheryteKind.Gridania;
        if(t.Value.PlaceNameRegion.RowId == 24) return ResidentialAetheryteKind.Uldah;
        if(t.Value.PlaceNameRegion.RowId == 22) return ResidentialAetheryteKind.Limsa;
        return null;
    }

    internal static WorldChangeAetheryte? GetWorldChangeAetheryteByTerritoryType(uint territoryType)
    {
        var c = TerritoryForWorldChangeAetheryte.FindKeysByValue(territoryType);
        return c.Any() ? c.First() : null;
    }

    internal static bool TryGetTinyAetheryteFromIGameObject(IGameObject a, out TinyAetheryte? t, uint? TerritoryType = null)
        => TryGetTinyAetheryteFromIGameObjectCore(a, false, out t, TerritoryType);

    /// <summary>
    /// 跟 <see cref="TryGetTinyAetheryteFromIGameObject"/> 完全相同,只差在「什麼算乙太之光物件」的判定
    /// 放寬成 <see cref="IsAetheryte(IGameObject)"/>:<see cref="ObjectKind.Aetheryte"/> 算,DataId 落在 <see cref="AethernetShards"/> 裡的 EventObj 也算。
    /// 🔴 嚴格版硬性要求 <c>ObjectKind == ObjectKind.Aetheryte</c>,但有些乙太之光在物件表裡不是這個 ObjectKind、只能靠 DataId 認出來,
    /// 每幀決定 <see cref="Lifestream.ActiveAetheryte"/> 的 <see cref="GetValidAetheryte"/> 用的正是這個寬鬆的判定;
    /// 拿嚴格版去找「身邊摸得到的同網路節點」,在那些城市會永遠回 null,而**失敗形式是完全靜默地退回傳送**。
    /// ⚠️ 嚴格版刻意不放寬:它被用在世界移動/住宅區/主水晶身分比對等流程,放寬會連帶改變那些流程的行為。
    /// </summary>
    internal static bool TryGetTinyAetheryteFromIGameObjectLenient(IGameObject a, out TinyAetheryte? t, uint? TerritoryType = null)
        => TryGetTinyAetheryteFromIGameObjectCore(a, true, out t, TerritoryType);

    private static bool TryGetTinyAetheryteFromIGameObjectCore(IGameObject a, bool lenientObjectKind, out TinyAetheryte? t, uint? TerritoryType)
    {
        TerritoryType ??= P.Territory;
        if(a == null)
        {
            t = default;
            return false;
        }
        if(lenientObjectKind ? a.IsAetheryte() : a.ObjectKind == ObjectKind.Aetheryte)
        {
            var pos2 = a.Position.ToVector2();
            foreach(var x in S.Data.DataStore.Aetherytes)
            {
                if(x.Key.TerritoryType == TerritoryType && Vector2.Distance(x.Key.Position, pos2) < 10)
                {
                    t = x.Key;
                    return true;
                }
                foreach(var l in x.Value)
                {
                    if(l.TerritoryType == TerritoryType && Vector2.Distance(l.Position, pos2) < 10)
                    {
                        t = l;
                        return true;
                    }
                }
            }
        }
        t = default;
        return false;
    }

    internal static string[] GetAvailableWorldDestinations()
    {
        if(TryGetAddonByName<AtkUnitBase>("WorldTravelSelect", out var addon) && IsAddonReady(addon))
        {
            List<string> arr = [];
            for(var i = 3; i <= 9; i++)
            {
                // 🔴 六跳節點鏈每一層都要驗。取不到就 break ＝ 與既有的「空字串代表清單到底了」同一條路徑,
                // 回傳截短的清單(fail-closed:少列出目的地只會讓人手動處理,列出錯的才會傳錯地方)。
                var item = GetComponentNodeSafe(addon, 4, i);
                if(!TryGetNodeText(GetComponentNodeSafe(item, 4), out var text)) break;
                if(text == "") break;
                arr.Add(text);
            }
            return [.. arr];
        }
        return Array.Empty<string>();
    }

    internal static string[] GetAvailableAethernetDestinations()
    {
        if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var addon) && IsAddonReady(addon))
        {
            List<string> arr = [];
            for(var i = 1; i <= 52; i++)
            {
                // 🔴 一樣是六跳節點鏈,而且迴圈上限 52 沒對 NodeListCount 驗過 ——
                // GetComponentNodeSafe 會把上界與元素判空一起做掉。
                var item = GetComponentNodeSafe(addon, 16, i);
                if(!TryGetNodeText(GetComponentNodeSafe(item, 3), out var rawText)) break;
                var text = rawText.Trim();
                if(text == "") break;
                arr.Add(text);
            }
            return [.. arr];
        }
        return Array.Empty<string>();
    }

    internal static IGameObject GetValidAetheryte()
    {
        foreach(var x in Svc.Objects)
        {
            if(x.IsAetheryte())
            {
                var d2d = Vector2.Distance(Svc.Objects.LocalPlayer.Position.ToVector2(), x.Position.ToVector2());
                var d3d = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(S.Data.ResidentialAethernet.IsInResidentialZone() && d3d > 4.6f) continue;
                if(S.Data.CustomAethernet.ZoneInfo.TryGetValue(P.Territory, out var zinfo) && d3d > zinfo.MaxInteractionDistance) continue;

                if(d2d < 11f
                    && d3d < 15f
                    && x.IsVPosValid()
                    && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }

    public static bool IsAetheryte(this IGameObject obj)
    {
        if(obj.ObjectKind == ObjectKind.Aetheryte) return true;
        return Utils.AethernetShards.Contains(obj.BaseId);
    }
}
