using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.LazyDataHelpers;

namespace Lifestream.Services;
public static class TerritoryWatcher
{
    public static uint LastHousingOutdoorTerritory = 0;
    public static void Initialize()
    {
        Svc.ClientState.TerritoryChanged += ClientState_TerritoryChanged;
        Svc.ClientState.Logout += ClientState_Logout;
        Svc.ClientState.Login += ClientState_Login;
        if(Player.Available)
        {
            ClientState_TerritoryChanged(Svc.ClientState.TerritoryType);
            if(Utils.IsInsideHouse() || Utils.IsInsideWorkshop() || Utils.IsInsidePrivateChambers())
            {
                DuoLog.Warning($"Lifestream was loaded or updated while being inside house. Please re-enter house to ensure data reliability.");
            }
        }
        Purgatory.Add(() =>
        {
            Svc.ClientState.TerritoryChanged -= ClientState_TerritoryChanged;
            Svc.ClientState.Logout -= ClientState_Logout;
            Svc.ClientState.Login -= ClientState_Login;
        });
    }

    // 換角＝登出再登入、插件不重載：不清的話，下一個角色在屋內登入會沿用上一個角色的住宅區。
    private static void ClientState_Logout(int type, int code)
    {
        if(LastHousingOutdoorTerritory != 0) PluginLog.Debug($"Logout: forgetting last residential territory {LastHousingOutdoorTerritory}");
        LastHousingOutdoorTerritory = 0;
    }

    // TerritoryChanged 只在區域 ID 變了才發：登入落在與登出前同一個住宅區時要自己補一次。
    // 不可在這裡歸零：TerritoryChanged 先於 Login 觸發，歸零會蓋掉剛記下的住宅區。
    private static void ClientState_Login()
    {
        ClientState_TerritoryChanged(Svc.ClientState.TerritoryType);
        if(LastHousingOutdoorTerritory == 0 && Utils.IsInsideHouse())
        {
            PluginLog.Information($"[TerritoryWatcher] 登入時人在屋內（區域 {Svc.ClientState.TerritoryType}），不知道是哪個住宅區：到工房的登錄路徑、房屋路徑編輯要等出屋再進來才可用。");
        }
    }

    public static bool IsDataReliable() => LastHousingOutdoorTerritory != 0;

    private static void ClientState_TerritoryChanged(ushort obj)
    {
        if(Utils.IsTerritoryResidentialDistrict(obj))
        {
            LastHousingOutdoorTerritory = obj;
            PluginLog.Debug($"Last residential territory: {ExcelTerritoryHelper.GetName(obj)}");
        }
    }

    // P.Territory 的取值、每幀讀很多次：用 is 樣式比對，EqualsAny 每次都會配置 params 陣列與委派。
    public static ushort GetRealTerritoryType()
    {
        if(Svc.ClientState.TerritoryType is Houses.Private_Cottage_Empyreum or Houses.Private_Cottage_Mist or Houses.Private_Cottage_Shirogane or Houses.Private_Cottage_The_Goblet or Houses.Private_Cottage_The_Lavender_Beds or 1249)
        {
            return LastHousingOutdoorTerritory switch
            {
                ResidentalAreas.Mist => Houses.Private_Cottage_Mist,
                ResidentalAreas.The_Lavender_Beds => Houses.Private_Cottage_The_Lavender_Beds,
                ResidentalAreas.The_Goblet => Houses.Private_Cottage_The_Goblet,
                ResidentalAreas.Shirogane => Houses.Private_Cottage_Shirogane,
                ResidentalAreas.Empyreum => Houses.Private_Cottage_Empyreum,
                _ => Svc.ClientState.TerritoryType
            };
        }
        if(Svc.ClientState.TerritoryType is Houses.Private_House_Empyreum or Houses.Private_House_Mist or Houses.Private_House_Shirogane or Houses.Private_House_The_Goblet or Houses.Private_House_The_Lavender_Beds or 1250)
        {
            return LastHousingOutdoorTerritory switch
            {
                ResidentalAreas.Mist => Houses.Private_House_Mist,
                ResidentalAreas.The_Lavender_Beds => Houses.Private_House_The_Lavender_Beds,
                ResidentalAreas.The_Goblet => Houses.Private_House_The_Goblet,
                ResidentalAreas.Shirogane => Houses.Private_House_Shirogane,
                ResidentalAreas.Empyreum => Houses.Private_House_Empyreum,
                _ => Svc.ClientState.TerritoryType
            };
        }
        if(Svc.ClientState.TerritoryType is Houses.Private_Mansion_Empyreum or Houses.Private_Mansion_Mist or Houses.Private_Mansion_Shirogane or Houses.Private_Mansion_The_Goblet or Houses.Private_Mansion_The_Lavender_Beds or 1251)
        {
            return LastHousingOutdoorTerritory switch
            {
                ResidentalAreas.Mist => Houses.Private_Mansion_Mist,
                ResidentalAreas.The_Lavender_Beds => Houses.Private_Mansion_The_Lavender_Beds,
                ResidentalAreas.The_Goblet => Houses.Private_Mansion_The_Goblet,
                ResidentalAreas.Shirogane => Houses.Private_Mansion_Shirogane,
                ResidentalAreas.Empyreum => Houses.Private_Mansion_Empyreum,
                _ => Svc.ClientState.TerritoryType
            };
        }
        return Svc.ClientState.TerritoryType;
    }
}
