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
    public static string[] LifestreamNativeCommands = ["auto", "home", "house", "private", "fc", "free", "company", "free company", "apartment", "apt", "shared", "inn", "hinn", "gc", "gcc", "hc", "hcc", "fcgc", "gcfc", "mb", "market", "island", "is", "sanctuary", "cosmic", "ardorum", "moon", "phaenna", "tp"];

    /// <summary>
    /// 第 <paramref name="index"/> 個指令執行時要不要把下一個「移動到座標」併成同一條路徑一起走。
    /// 兩邊都限定了區域且不同區時不併；交給 TextAdvance 移動的導航不併（它不會走追加點）。
    /// </summary>
    public static bool IsChainedWithNext(this CustomAlias alias, int index)
    {
        if(index < 0 || index >= alias.Commands.Count - 1)
        {
            return false;
        }
        var current = alias.Commands[index];
        if(current.Kind == CustomAliasKind.Navmesh_to_point && current.IsUsingTAForMovement())
        {
            return false;
        }
        if(current.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point, CustomAliasKind.Circular_movement))
        {
            var next = alias.Commands[index + 1];
            if(next.Kind == CustomAliasKind.Move_to_point)
            {
                return current.Territory == 0 || next.Territory == 0 || current.Territory == next.Territory;
            }
        }
        return false;
    }

    /// <summary>
    /// 編輯器視覺化用：把同一條鏈（<see cref="CustomAliasCommand.ChainGroup"/>，須先算好）相鄰的點連成線段；
    /// 有散布範圍時改畫左右兩條外緣線。只畫不限區域或限定在目前所在區域的鏈。
    /// </summary>
    public static List<(Vector3 Start, Vector3 End)> GenerateGroupConnectionLines(this CustomAlias alias, float extraScatter = 0f)
    {
        List<(Vector3 Start, Vector3 End)> lines = [];
        var grouped = alias.Commands
            .Where(c => c.ChainGroup > 0)
            .GroupBy(c => c.ChainGroup)
            .OrderBy(g => g.Key);

        foreach(var group in grouped)
        {
            var groupCommands = group.ToList();
            if(!groupCommands[0].Territory.EqualsAny<uint>(0, Player.Territory)) continue;

            Vector3? previousPoint = null;
            var previousRadius = 0f;
            var i = 0;
            if(groupCommands[0].Kind == CustomAliasKind.Circular_movement && groupCommands[0].WalkToExit)
            {
                previousPoint = groupCommands[0].CircularExitPoint;
                previousRadius = groupCommands[0].Scatter + extraScatter;
                i = 1;
            }
            else
            {
                while(i < groupCommands.Count)
                {
                    var cmd = groupCommands[i];
                    i++;
                    if(cmd.Kind != CustomAliasKind.Circular_movement)
                    {
                        previousPoint = cmd.Point;
                        previousRadius = cmd.Scatter + extraScatter;
                        break;
                    }
                }
            }
            if(!previousPoint.HasValue)
            {
                continue;
            }

            for(; i < groupCommands.Count; i++)
            {
                var current = groupCommands[i];
                if(current.Kind == CustomAliasKind.Circular_movement)
                {
                    continue;
                }
                var currentPoint = current.Point;
                var currentRadius = current.Scatter + extraScatter;
                var dir = Vector3.Normalize(currentPoint - previousPoint.Value);
                var right = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
                // 兩點重合或垂直相疊時 right 是 NaN：退回只畫中線，不把 NaN 座標交給 Splatoon。
                if((previousRadius == 0f && currentRadius == 0f) || !float.IsFinite(right.X) || !float.IsFinite(right.Z))
                {
                    lines.Add((previousPoint.Value, currentPoint));
                }
                else
                {
                    lines.Add((previousPoint.Value + right * previousRadius, currentPoint + right * currentRadius));
                    lines.Add((previousPoint.Value - right * previousRadius, currentPoint - right * currentRadius));
                }
                previousPoint = currentPoint;
                previousRadius = currentRadius;
            }
        }
        return lines;
    }

    /// <summary>把第 <paramref name="currentIndex"/> 個指令的限定區域複製到它與第 <paramref name="rangeOneBased"/> 號（1 起算）之間的每個指令，範圍夾在清單內。</summary>
    public static void CopyTerritoryRange(this CustomAlias alias, int currentIndex, int rangeOneBased)
    {
        var commands = alias.Commands;
        if(commands.Count == 0 || currentIndex < 0 || currentIndex >= commands.Count)
        {
            return;
        }
        var range = rangeOneBased - 1;
        var start = Math.Max(0, Math.Min(currentIndex, range));
        var end = Math.Min(commands.Count - 1, Math.Max(currentIndex, range));
        var territory = commands[currentIndex].Territory;
        for(var i = start; i <= end; i++)
        {
            commands[i].Territory = territory;
        }
    }

    public static Vector3 Scatter(this Vector3 point, float radius)
    {
        if(radius > 0)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2;

            var distance = Math.Sqrt(Random.Shared.NextDouble()) * radius;

            var offsetX = (float)(Math.Cos(angle) * distance);
            var offsetZ = (float)(Math.Sin(angle) * distance);

            return new Vector3(point.X + offsetX, point.Y, point.Z + offsetZ);
        }
        else
        {
            return point;
        }
    }

    public static bool EnqueueTeleport(string destination, string additionalCommand)
    {
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid))
        {
            if(x.AetheryteData.Value.AethernetName.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatQueue.Green($"[Lifestream] Destination (Aethernet): {x.AetheryteData
                        .Value.AethernetName.ValueNullable?.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid && s.AetheryteData.Value.PlaceName.IsValid))
        {
            if(x.AetheryteData.Value.PlaceName.Value.Name.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatQueue.Green($"[Lifestream] Destination (Place): {x.AetheryteData
                        .Value.PlaceName.ValueNullable?.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid && s.AetheryteData.Value.Territory.IsValid && s.AetheryteData.Value.Territory.Value.PlaceName.IsValid))
        {
            if(x.AetheryteData.Value.Territory.Value.PlaceName.Value.Name.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatQueue.Green($"[Lifestream] Destination (Zone): {x.AetheryteData
                        .Value.Territory.Value.PlaceName.Value.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        return false;
    }

    public static IGameObject GetWorkshopEntrance()
    {
        return Svc.Objects.FirstOrDefault(x => x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny(Lang.AdditionalChambersEntrance));
    }

    private static readonly (string Kind, Func<string, string, bool> Match)[] PartialMatchers =
    [
        ("前綴", (h, n) => h.StartsWith(n, StringComparison.OrdinalIgnoreCase)),
        ("包含", (h, n) => h.Contains(n, StringComparison.OrdinalIgnoreCase)),
    ];
    private static string LastAmbiguousPartialKey;
    private static long LastAmbiguousPartialAt;

    public static Dictionary<string, Dictionary<uint, string>> KnownAetherytesByCategories
    {
        get
        {
            if(field == null)
            {
                field = [];
                foreach(var x in S.Data.DataStore.Aetherytes)
                {
                    var dict = new Dictionary<uint, string>()
                    {
                        [x.Key.ID] = x.Key.Name,
                    };
                    field[ExcelTerritoryHelper.GetName(x.Key.TerritoryType)] = dict;
                    foreach(var v in x.Value)
                    {
                        dict[v.ID] = v.Name;
                    }
                    if(x.Key.ID == 70)
                    {
                        dict[TaskAetheryteAethernetTeleport.FirmamentAethernetId] = "Firmament";
                    }
                    if(x.Key.ID == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId)
                    {
                        // 175 沒有 AethernetName(master 名稱是空字串),改用地名;並比照蒼天街掛上渴望灣
                        if(dict[x.Key.ID] == "")
                        {
                            dict[x.Key.ID] = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(x.Key.ID)?.PlaceName.ValueNullable?.Name.GetText() ?? x.Key.ID.ToString();
                        }
                        dict[TaskAetheryteAethernetTeleport.SinusArdorumAethernetId] = "Sinus Ardorum";
                        dict[TaskAetheryteAethernetTeleport.PhaennaAethernetId] = "Phaenna";
                    }
                }
                foreach(var x in S.Data.ResidentialAethernet.ZoneInfo)
                {
                    var dict = new Dictionary<uint, string>();
                    field[ExcelTerritoryHelper.GetName(x.Key)] = dict;
                    foreach(var v in x.Value.Aetherytes)
                    {
                        dict[v.ID] = v.Name;
                    }
                }
                foreach(var x in S.Data.CustomAethernet.ZoneInfo)
                {
                    var dict = new Dictionary<uint, string>();
                    field[ExcelTerritoryHelper.GetName(x.Key)] = dict;
                    foreach(var v in x.Value.Aetherytes)
                    {
                        dict[v.ID] = v.Name;
                    }
                }
            }
            return field;
        }
    } = null;

    public static bool ApproachConditionIsMet()
    {
        return (P.ActiveAetheryte == null || !P.ActiveAetheryte.Value.IsAetheryte) && Utils.GetReachableAetheryte(x => x.IsAetheryte()) != null;
    }

    public static bool WotsitInstalled()
    {
        return Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "Dalamud.FindAnything" && x.IsLoaded);
    }

    public static string GetMountName(int id)
    {
        return Svc.Data.GetExcelSheet<Mount>().GetRow((uint)id).Singular.ExtractText();
    }
    public static string GetWorldFromCID(ulong cid)
    {
        return Utils.GetCharaName(cid)?.Split("@").SafeSelect(1);
    }

    public static string GetCharaName(ulong cid)
    {
        if(C.CharaMap.TryGetValue(cid, out var name)) return name;
        return $"#{cid:X16}";
    }

    public static void ReadClipboardFiles()
    {
        try
        {
            var fType = AppDomain.CurrentDomain.GetAssemblies().First(x => x.GetName().Name == "System.Windows.Forms");
            var clipboard = fType.GetType("System.Windows.Forms.Clipboard");
            if((bool)clipboard.GetMethod("ContainsFileDropList").Invoke(null, []))
            {
                var cb = (StringCollection)clipboard.GetMethod("GetFileDropList").Invoke(null, []);
                foreach(var f in cb)
                {
                    DuoLog.Information($"{f}");
                }
            }
        }
        catch(Exception e)
        {
            e.Log();
        }
    }

    public static bool IsMountedEx()
    {
        if(Svc.Condition[ConditionFlag.Mounted]) return true;
        if(IsPlayerFalling()) return true;
        return false;
    }

    public static bool DismountIfNeeded()
    {
        if(Utils.IsMountedEx())
        {
            EzThrottler.Throttle("PlayerMounted", 200, true);
            if(EzThrottler.Throttle("DismountPlayer", 1000))
            {
                Chat.ExecuteGeneralAction(23);
            }
            return false;
        }
        if(!EzThrottler.Check("PlayerMounted")) return false;
        return true;
    }

    public static bool IsPlayerFalling()
    {
        var p = Svc.Objects.LocalPlayer;
        if(p == null)
            return true;

        // 0 if grounded
        // 1 = "jumpsquat"
        // 3 = going up
        // 4 = stopped
        // 5 = going down
        var isJumping = *(byte*)(p.Address + 496 + 208) > 0;
        // 1 iff dismounting and haven't hit the ground yet
        var isAirDismount = **(byte**)(p.Address + 496 + 904) == 1;

        return isJumping || isAirDismount;
    }

    public static bool IsTravelBlocked(string charaName, Number charaWorld, Number sourceWorld, string targetWorld)
    {
        return IsTravelBlocked(charaName, charaWorld, sourceWorld, ExcelWorldHelper.Get(targetWorld).Value.RowId);
    }

    public static bool IsTravelBlocked(string charaName, Number charaWorld, Number sourceWorld, Number targetWorld)
    {
        foreach(var x in C.TravelBans)
        {
            if(x.IsEnabled && x.CharaName == charaName && x.CharaHomeWorld == charaWorld)
            {
                if(x.BannedFrom.Contains(sourceWorld) && x.BannedTo.Contains(targetWorld))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void AssertCanTravel(string charaName, Number charaWorld, Number sourceWorld, string targetWorld)
    {
        AssertCanTravel(charaName, charaWorld, sourceWorld, ExcelWorldHelper.Get(targetWorld).Value.RowId);
    }

    public static void AssertCanTravel(string charaName, Number charaWorld, Number sourceWorld, Number targetWorld)
    {
        if(IsTravelBlocked(charaName, charaWorld, sourceWorld, targetWorld))
        {
            var err = $"Character {charaName}@{ExcelWorldHelper.GetName(charaWorld)} can not travel from {ExcelWorldHelper.GetName(sourceWorld)} to {ExcelWorldHelper.GetName(targetWorld)}. Access Lifestream - Travel Block to change it.";
            ChatQueue.ErrorToast(err);
            Notify.Error(err);
            DuoLog.Error(err);
            throw new InvalidOperationException(err);
        }
    }

    public static bool GenericThrottle => FrameThrottler.Throttle("LifestreamGenericThrottle", 10);
    public static void RethrottleGeneric(int num = 10)
    {
        FrameThrottler.Throttle("LifestreamGenericThrottle", num, true);
    }

    public static WorldChangeAetheryte AdjustGateway(this WorldChangeAetheryte gateway)
    {
        if(!Svc.AetheryteList.Any(x => x.AetheryteId == (int)gateway))
        {
            foreach(var c in Enum.GetValues<WorldChangeAetheryte>())
            {
                if(Svc.AetheryteList.Any(x => x.AetheryteId == (int)c))
                {
                    DuoLog.Warning($"{gateway} is not unlocked, but {c} is, adjusting.");
                    gateway = c;
                    break;
                }
            }
        }
        return gateway;
    }

    public static List<World> GetVisitableWorldsFrom(World source)
    {
        var ret = new List<World>();
        foreach(var x in PublicWorlds.Travelable(PublicWorlds.Get(source.GetRegion())))
        {
            ret.Add(x);
        }
        // 台服不能跨區旅行到大洋洲（國際服才有「任何地區都能去大洋洲」的規則）。
        if(PublicWorlds.IsTaiwanWorld(source)) return ret;
        foreach(var x in PublicWorlds.Travelable(PublicWorlds.Get(ExcelWorldHelper.Region.OC)))
        {
            if(!ret.Contains(x)) ret.Add(x);
        }
        return ret;
    }

    public static bool IsAetheryteEligibleForCustomAlias(IGameObject go)
    {
        if(go.ObjectKind == ObjectKind.Aetheryte && TryGetTinyAetheryteFromIGameObject(go, out var tiny))
        {
            return tiny.Value.ID == S.Data.DataStore.GetMaster(tiny.Value).ID;
        }
        return true;
    }

    public static uint[] AethernetShards = [2000151, 2000153, 2000154, 2000155, 2000156, 2000157, 2003395, 2003396, 2003397, 2003398, 2003399, 2003400, 2003401, 2003402, 2003403, 2003404, 2003405, 2003406, 2003407, 2003408, 2003409, 2003995, 2003996, 2003997, 2003998, 2003999, 2004000, 2004968, 2004969, 2004970, 2004971, 2004972, 2004973, 2004974, 2004976, 2004977, 2004978, 2004979, 2004980, 2004981, 2004982, 2004983, 2004984, 2004985, 2004986, 2004987, 2004988, 2004989, 2007434, 2007435, 2007436, 2007437, 2007438, 2007439, 2007855, 2007856, 2007857, 2007858, 2007859, 2007860, 2007861, 2007862, 2007863, 2007864, 2007865, 2007866, 2007867, 2007868, 2007869, 2007870, 2009421, 2009432, 2009433, 2009562, 2009563, 2009564, 2009565, 2009615, 2009616, 2009617, 2009618, 2009713, 2009714, 2009715, 2009981, 2010135, 2011142, 2011162, 2011163, 2011241, 2011243, 2011373, 2011374, 2011384, 2011385, 2011386, 2011387, 2011388, 2011389, 2011573, 2011574, 2011575, 2011677, 2011678, 2011679, 2011680, 2011681, 2011682, 2011683, 2011684, 2011685, 2011686, 2011687, 2011688, 2011689, 2011690, 2011691, 2011692, 2012252, 2012253, 2011160, 2011572, 2014664, 2014744, 2014665, 2014666, 2014667,];

    public static uint[] HousingAethernet = [MainCities.Limsa_Lominsa_Lower_Decks, MainCities.Uldah_Steps_of_Nald, MainCities.New_Gridania, MainCities.Foundation, MainCities.Kugane];

    public static void DisplayInfo(string s, bool? displayChat = null, bool? displayPopup = null)
    {
        if(displayChat ?? C.DisplayChatTeleport) ChatQueue.Green($"[Lifestream] {s}");
        if(displayPopup ?? C.DisplayPopupNotifications) Notify.Info(s);
    }

    public static bool IsBusy()
    {
        return P.TaskManager.IsBusy || P.followPath?.waypointsInternal.Count > 0;
    }

    /// <summary>判斷不了（特徵碼沒解到）時回 false＝當成不能飛。</summary>
    public static bool CanFly() => S.Memory.CanCheckFlight && S.Memory.IsFlightProhibited(S.Memory.FlightAddr) == 0;

    public static bool TryGetWorldFromDataCenter(string s, out string world, out uint dataCenter)
    {
        foreach(var x in Svc.Data.GetExcelSheet<WorldDCGroupType>())
        {
            if(x.RowId == 0 || x.Name == "") continue;
            if(x.Name.GetText().StartsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                var worlds = PublicWorlds.Travelable(PublicWorlds.Get(x.RowId));
                if(worlds.Length > 0)
                {
                    world = worlds[Random.Shared.Next(worlds.Length)].Name.ToString();
                    dataCenter = x.RowId;
                    if(S.Data.DataStore.Worlds.Contains(world) || S.Data.DataStore.DCWorlds.Contains(world))
                    {
                        return true;
                    }
                }
            }
        }
        dataCenter = default;
        world = default;
        return false;
    }

    public static bool IsTeleporterInstalled()
    {
        return Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "TeleporterPlugin" && x.IsLoaded);
    }

    private static bool ReportedIsHereNullEntry = false;
    private static bool ReportedIsHereNullPlayer = false;

    public static string FancyDigits(this int n)
    {
        return n.ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
    }

    public static bool? WaitForScreen() => IsScreenReady();

    public static bool? WaitForScreenFalse() => !IsScreenReady();

    public static bool RenderIcon(this ResidentialAetheryteKind residentialAetheryte, float? size = null)
    {
        return NuiTools.RenderResidentialIcon(residentialAetheryte.GetResidentialTerritory(), size);
    }

    internal static void TryNotify(string s)
    {
        if(C.EnableNotifications)
        {
            P.NotificationMasterApi.DisplayTrayNotification(P.Name, s);
        }
    }

    internal static string GetDataCenterName(string world)
    {
        return GetDataCenter(world).Name.ToString();
    }

    internal static WorldDCGroupType GetDataCenter(string world)
    {
        // 同名世界（「巴哈姆特」＝退役 row 1160 與台服 4033）先取現役世界；查不到才退回整張表第一筆。
        var match = PublicWorlds.Get().FirstOrNull(x => x.Name == world)
            ?? Svc.Data.GetExcelSheet<World>().First(x => x.Name == world);
        return match.DataCenter.Value;
    }

    internal static int Minutes(this int min)
    {
        return min * 60 * 1000;
    }
    internal static int Seconds(this int sec)
    {
        return sec * 1000;
    }

    internal static bool CanAutoLogin()
    {
        if(Svc.ClientState.IsLoggedIn || Svc.Condition.Any()) return false;
        if(!TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title) || !IsAddonReady(title)) return false;
        if(title->UldManager.NodeListCount <= 3) return false;

        // 🔴 上界與元素判空是兩件事（見 AtkNodeSafety 的說明）：索引在範圍內時 NodeList[3]
        //    仍然可能是 null，->Color 是對 null 加偏移後讀取，AVE 在 .NET Core 是
        //    corrupted-state exception，try/catch 攔不到。
        //    GetNodeSafe 的上界條件與上面那行等價（index 3 需要 NodeListCount > 3），上界維持不變。
        var titleNode = GetNodeSafe(&title->UldManager, 3);
        if(titleNode == null || titleNode->Color.A != 0xFF) return false;

        return !TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out _)
            && !TryGetAddonByName<AtkUnitBase>("TitleConnect", out _);
    }

    internal static Dictionary<WorldChangeAetheryte, uint> WCATerritories = new()
    {
        [WorldChangeAetheryte.Uldah] = 130,
        [WorldChangeAetheryte.Limsa] = 129,
        [WorldChangeAetheryte.Gridania] = 132
    };

    internal static bool TryGetCharacterIndex(string name, uint world, out int index)
    {
        index = GetCharacterNames().IndexOf((name, (ushort)world));
        return index >= 0;
    }

    internal static List<CharaData> GetCharacterNames()
    {
        List<CharaData> ret = [];
        /*var data = CSFramework.Instance()->UIModule->GetRaptureAtkModule()->AtkModule.GetStringArrayData(1);
        if (data != null)
        {
            for (int i = 60; i < data->AtkArrayData.Size; i++)
            {
                if (data->StringArray[i] == null) break;
                var item = data->StringArray[i];
                if (item != null)
                {
                    var str = MemoryHelper.ReadSeStringNullTerminated((nint)item).GetText();
                    if (str == "") break;
                    ret.Add(str);
                }
            }
        }*/
        var agent = AgentLobby.Instance();
        // AgentLobby 取得器合法回 null(角色選擇畫面之外、或 UIModule 尚未建立)。
        // 拿不到就回空清單:呼叫端 TryGetCharacterIndex 會得到 index < 0 → 判定「找不到角色」,
        // 流程停在原地重試,不會拿 null 去解參考 LobbyData。
        if(agent == null) return ret;
        //if (agent->AgentInterface.IsAgentActive())
        {
            var charaSpan = agent->LobbyData.CharaSelectEntries.AsSpan();
            for(var i = 0; i < charaSpan.Length; i++)
            {
                var s = charaSpan[i];
                // 🔴 元素本身是可為 null 的指標(項目還沒填完就是 null),直接 s.Value->Name 是 AccessViolation。
                // 🔴 但這裡不能用 continue 跳過:本清單的「索引」會被 DCChange.SelectCharacter 原封不動
                // 當成 _CharaSelectListMenu 的角色索引送出去(Callback.Fire(addon, false, 29, 0, index)),
                // 少一項就讓後面每個角色的索引往前位移一格 ⇒ 失敗形式是「登入到錯的角色」,比崩潰更糟。
                // 填一個永遠不會被 IndexOf 命中的佔位項(空名字＋世界 0)來保住位置對齊。
                if(s.Value == null)
                {
                    ret.Add(("", (ushort)0));
                    continue;
                }
                ret.Add(($"{s.Value->Name.Read()}", s.Value->HomeWorldId));
            }
        }
        return ret;
    }

    internal static string GetInnNameFromTerritory(uint tt)
    {
        if(tt == 0) return "Autodetect";
        if(Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(tt, out var t))
        {
            var inn = Svc.Data.GetExcelSheet<TerritoryType>().FirstOrNull(x => x.PlaceNameRegion.ValueNullable?.RowId == t.PlaceNameRegion.Value.RowId && x.TerritoryIntendedUse.RowId == (int)TerritoryIntendedUseEnum.Inn);
            if(inn != null)
            {
                return inn.Value.PlaceNameZone.Value.Name.ToString();
            }
        }
        return "???";
    }

    /// <summary>
    /// 「值得嘗試鎖定+自動移動走過去用」的距離上限。刻意比 <see cref="GetReachableAetheryte"/>
    /// 預設的 30y 寬:那個門檻是給「已經在互動範圍附近,頂多再修正幾步」的情境用的,
    /// 而這裡是「要不要為了省一次傳送,花最多 20 秒鎖定+自動移動走過去」,30y 對這個用途太保守。
    /// 50y 是一般可鎖定目標的距離量級,而走位本身有 20 秒時限兜底(<c>TaskAethernetRoute</c> 的
    /// <c>ApproachTimeLimitMS</c>),逾時就退回傳送。
    /// </summary>
    private const float AethernetNetworkNodeApproachRange = 50f;

    internal static bool IsDisallowedToChangeWorld()
    {
        return Svc.Condition[ConditionFlag.WaitingToVisitOtherWorld]
            || Svc.Condition[ConditionFlag.ReadyingVisitOtherWorld]
            || Svc.Condition[ConditionFlag.InDutyQueue]
            || Svc.Condition[ConditionFlag.BoundByDuty95]
            || Svc.Party.Length > 0
            ;
    }

    internal static bool IsDisallowedToUseAethernet()
    {
        return Svc.Condition[ConditionFlag.WaitingToVisitOtherWorld] || Svc.Condition[ConditionFlag.Jumping];
    }

    internal static string[] DefaultAddons = [
        "Inventory",
        "InventoryLarge",
        "AreaMap",
        "InventoryRetainerLarge",
        "Currency",
        "Bank",
        "RetainerTask",
        "RetainerList",
        "SelectYesNo",
        "SelectString",
        "SystemMenu",
        "MountNoteBook",
        "FriendList",
        "BlackList",
        "Character",
        "ItemSearch",
        "MonsterNote",
        "GatheringNote",
        "RecipeNote",
        "ContentsFinder",
        "LookingForGroup",
        "Journal",
        "ContentsInfo",
        "SocialList",
        "Macro",
        "ConfigKeybind",
        "GSInfoGeneral",
        "ContentsInfo",
        "PartyMemberList",
        "ContentsFinderConfirm",
        "SelectString"
    ];

    private static Dictionary<ResidentialAetheryteKind, uint> TerritoryForResidentialAetheryte = new()
    {
        [ResidentialAetheryteKind.Uldah] = MainCities.Uldah_Steps_of_Nald,
        [ResidentialAetheryteKind.Gridania] = MainCities.New_Gridania,
        [ResidentialAetheryteKind.Limsa] = MainCities.Limsa_Lominsa_Lower_Decks,
        [ResidentialAetheryteKind.Kugane] = MainCities.Kugane,
        [ResidentialAetheryteKind.Foundation] = MainCities.Foundation,
    };
    private static Dictionary<ResidentialAetheryteKind, uint> ResidentialTerritoryForResidentialAetheryte = new()
    {
        [ResidentialAetheryteKind.Uldah] = ResidentalAreas.The_Goblet,
        [ResidentialAetheryteKind.Gridania] = ResidentalAreas.The_Lavender_Beds,
        [ResidentialAetheryteKind.Limsa] = ResidentalAreas.Mist,
        [ResidentialAetheryteKind.Kugane] = ResidentalAreas.Shirogane,
        [ResidentialAetheryteKind.Foundation] = ResidentalAreas.Empyreum,
    };
    private static Dictionary<WorldChangeAetheryte, uint> TerritoryForWorldChangeAetheryte = new()
    {
        [WorldChangeAetheryte.Uldah] = MainCities.Uldah_Steps_of_Nald,
        [WorldChangeAetheryte.Gridania] = MainCities.New_Gridania,
        [WorldChangeAetheryte.Limsa] = MainCities.Limsa_Lominsa_Lower_Decks,
    };

    internal static float ConvertMapMarkerToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        var rawPosition = (int)((float)(pos - 1024.0) / num * 1000f);
        return ConvertRawPositionToMapCoordinate(rawPosition, scale);
    }

    internal static float ConvertMapMarkerToRawPosition(int pos, float scale)
    {
        var num = scale / 100f;
        var rawPosition = ((float)(pos - 1024.0) / num);
        return rawPosition;
    }

    internal static float ConvertRawPositionToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        return (float)((pos / 1000f * num + 1024.0) / 2048.0 * 41.0 / num + 1.0);
    }

    // 登出確認視窗有兩種文字:一般情況是 Addon#115,正在排隊等待副本時遊戲會換成 Addon#17531。
    // 台服的 Addon 表沒有 17531 這一列,而 Lumina 的 GetRow(uint) 對不存在的列會擲 ArgumentOutOfRangeException,
    // 每次輪詢都會走到這裡,所以一律用 TryGetRow:取不到就只留 115 那一組。
    // 台服拿不到 17531 是常態不是錯誤,因此結果快取起來,診斷只在第一次解析時寫一行 Information。
    private const uint LogOutYesnoAddonRow = 115;
    private const uint LogOutYesnoQueuedForDutyAddonRow = 17531;
    private static string[] LogOutYesnoTextsCache;

    internal static bool IsVPosValid(this IGameObject x)
    {
        /*if(x.Name.ToString() == Lang.AethernetShard)
        {
            return MathF.Abs(Player.Object.Position.Y - x.Position.Y) < 0.965;
        }*/
        return true;
    }

    internal static int GetServiceAccount(string name, uint world) => GetServiceAccount($"{name}@{ExcelWorldHelper.GetName(world)}");

    internal static int GetServiceAccount(string nameWithWorld)
    {
        if(P.AutoRetainerApi?.Ready == true && C.UseAutoRetainerAccounts)
        {
            var chars = P.AutoRetainerApi.GetRegisteredCharacters();
            foreach(var c in chars)
            {
                var data = P.AutoRetainerApi.GetOfflineCharacterData(c);
                if(data != null)
                {
                    var name = $"{data.Name}@{data.World}";
                    if(nameWithWorld == name && data.ServiceAccount > -1)
                    {
                        return data.ServiceAccount;
                    }
                }
            }
        }
        if(C.ServiceAccounts.TryGetValue(nameWithWorld, out var ret))
        {
            if(ret > -1) return ret;
        }
        return 0;
    }

    internal static void CheckConfigMigration()
    {
        // int ButtonWidth -> int[3] ButtonWidthArray
        if(C.ButtonWidthArray is null) MigrateConfigButtonWidthToButtonWidthArray();
        EzConfig.Save();
    }

    internal static void MigrateConfigButtonWidthToButtonWidthArray()
    {
        C.ButtonWidthArray = [C.ButtonWidth, C.ButtonWidth, C.ButtonWidth];
    }

    public static bool IsLoggingOutInstant(uint territoryType)
    {
        if(Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryType, out var sheet))
        {
            return sheet.TerritoryIntendedUse.Value.DisableLogoutTimer;
        }

        return false;
    }
}
