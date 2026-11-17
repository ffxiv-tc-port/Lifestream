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
    public static bool IsInsideHouse()
    {
        return P.Territory.EqualsAny<uint>(
            Houses.Private_Cottage_Mist, Houses.Private_House_Mist, Houses.Private_Mansion_Mist,
            Houses.Private_Cottage_Empyreum, Houses.Private_House_Empyreum, Houses.Private_Mansion_Empyreum,
            Houses.Private_Cottage_Shirogane, Houses.Private_House_Shirogane, Houses.Private_Mansion_Shirogane,
            Houses.Private_Cottage_The_Goblet, Houses.Private_House_The_Goblet, Houses.Private_Mansion_The_Goblet,
            Houses.Private_Cottage_The_Lavender_Beds, Houses.Private_House_The_Lavender_Beds, Houses.Private_Mansion_The_Lavender_Beds,
            1249, 1250, 1251
            );
    }

    public static bool IsInsideWorkshop()
    {
        return P.Territory.EqualsAny(Houses.Company_Workshop_Empyreum, Houses.Company_Workshop_Mist, Houses.Company_Workshop_Shirogane, Houses.Company_Workshop_The_Goblet, Houses.Company_Workshop_The_Lavender_Beds);
    }

    public static bool IsInsidePrivateChambers()
    {
        return P.Territory.EqualsAny(Houses.Private_Chambers_Empyreum, Houses.Private_Chambers_Mist, Houses.Private_Chambers_Shirogane, Houses.Private_Chambers_The_Goblet, Houses.Private_Chambers_The_Lavender_Beds);
    }

    public static bool IsTerritoryResidentialDistrict(ushort obj)
    {
        return obj.EqualsAny(ResidentalAreas.Mist, ResidentalAreas.Shirogane, ResidentalAreas.Empyreum, ResidentalAreas.The_Goblet, ResidentalAreas.The_Lavender_Beds);
    }

    public static List<HousePathData> GetHousePathDatas()
    {
        if(P.DisableHousePathData) return [];
        return C.HousePathDatas;
    }

    public static HousePathData GetFCPathData() => Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == Player.CID && !x.IsPrivate);

    public static HousePathData GetPrivatePathData() => Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == Player.CID && x.IsPrivate);

    public static HousePathData GetCustomPathData(ResidentialAetheryteKind kind, int ward, int plot)
    {
        return C.HousePathDatas.FirstOrDefault(x => x.ResidentialDistrict == kind && x.Ward == ward && x.Plot == plot) ?? C.CustomHousePathDatas.FirstOrDefault(x => x.ResidentialDistrict == kind && x.Ward == ward && x.Plot == plot);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="territory"></param>
    /// <param name="plot">Start with 0</param>
    /// <returns></returns>
    public static Vector3? GetPlotEntrance(uint territory, int plot)
    {
        if(S.Data.ResidentialAethernet.HousingData.Data.TryGetValue(territory, out var data) && data.Count > plot && data[plot].Path.Count > 0) return data[plot].Path[^1];
        return null;
    }

    public static IGameObject GetNearestEntrance(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;

        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && Lang.Entrance.Matches(x.Name.ToString(), LegacyMatch.EqualsIgnoreCaseAny))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    public static HouseEnterMode GetHouseEnterMode(this HousePathData data)
    {
        if(data != null && data.EnableHouseEnterModeOverride) return data.EnterModeOverride;
        return C.HouseEnterMode;
    }

    public static void SaveGeneratedHousingData()
    {
        EzConfig.SaveConfiguration(S.Data.ResidentialAethernet.HousingData, "GeneratedHousingData.json", false);
    }

    public static float CalculatePathDistance(Vector3[] vectors)
    {
        var distance = 0f;
        for(var i = 0; i < vectors.Length - 1; i++)
        {
            distance += Vector3.Distance(vectors[i], vectors[i + 1]);
        }
        return distance;
    }
}
