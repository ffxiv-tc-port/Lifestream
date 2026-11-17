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
    public static bool TryParseAddressBookEntry(string s, out AddressBookEntry entry, bool retry = false)
    {
        entry = null;
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(P|plot)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, false, false);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%optDelimiter(s|sub|subdivision)%delimiter(A|apartment)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[7].Value, true, true);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(A|apartment)%shortDelimiter%numeric%optDelimiter(s|sub|subdivision)");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, true, true);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(A|apartment)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, true, false);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward|)%shortDelimiter%numeric%delimiter(P|plot|)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, false, false);
                if(entry != null) return true;
            }
        }
        if(!retry)
        {
            if(TryParseAddressBookEntry(Player.CurrentWorld + ", " + s, out entry, true))
            {
                return entry != null;
            }
        }
        return entry != null;
    }

    public static string ReplaceAddressBookRegex(string str)
    {
        var cities = "goblet|the goblet|lavender beds|the lavender beds|lavender|lb|empy|empyreum|shiro|shirogane|mist";
        var worlds = PublicWorlds.Get().Select(x => x.Name.ToString()).Join("|") + "|[a-z]{3,30}";
        return str.Replace("%worlds", worlds)
            .Replace("%delimiter", @"[\s\.\,\-\(\)\t]{1,10}")
            .Replace("%optDelimiter", @"[\s\.\,\-\(\)\t]{0,10}")
            .Replace("%city", cities)
            .Replace("%shortDelimiter", @"[\s\.\-\t]{0,3}")
            .Replace("%numeric", "([0-9]{1,2})");
    }

    public static AddressBookEntry BuildAddressBookEntry(string worldStr, string cityStr, string wardNum, string plotApartmentNum, bool isApartment, bool isSubdivision, string name = null)
    {
        var world = ExcelWorldHelper.Get(worldStr, true);
        if(world == null)
        {
            foreach(var x in PublicWorlds.Get())
            {
                if(x.Name.ToString().StartsWith(worldStr, StringComparison.OrdinalIgnoreCase))
                {
                    world = x;
                    break;
                }
            }
        }
        if(world == null) return null;
        var city = ParseResidentialAetheryteKind(cityStr);
        if(city == null) return null;
        if(int.TryParse(wardNum, out var ward) && int.TryParse(plotApartmentNum, out var plot))
        {
            var entry = new AddressBookEntry()
            {
                City = city.Value,
                World = (int)world?.RowId,
                PropertyType = isApartment ? PropertyType.Apartment : PropertyType.House,
                Ward = ward,
                Apartment = plot,
                Plot = plot,
                ApartmentSubdivision = isSubdivision,
            };
            if(name != null) entry.Name = name;
            return entry;
        }
        return null;
    }

    public static ResidentialAetheryteKind? ParseResidentialAetheryteKind(string s)
    {
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "mist"))
        {
            return ResidentialAetheryteKind.Limsa;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "goblet"))
        {
            return ResidentialAetheryteKind.Uldah;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "empy"))
        {
            return ResidentialAetheryteKind.Foundation;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "shiro"))
        {
            return ResidentialAetheryteKind.Kugane;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "lavender", "beds", "lb"))
        {
            return ResidentialAetheryteKind.Gridania;
        }
        return null;
    }

    public static bool IsHere(this AddressBookEntry entry)
    {
        // 絕不從這裡擲例外：本方法在通訊錄分頁的每幀繪製路徑上。
        // ECommons 的 ConfigWindow.Draw 用 GenericHelpers.Safe 包住整個主視窗，
        // 例外會被吞下並每幀寫一行 Error（不會觸發 Dalamud 的視窗錯誤畫面），
        // 但堆疊會從這裡一路展開到 Safe()，ImGui 的 BeginTabBar / BeginTabItem /
        // BeginChild 都不會收尾，分頁內容每幀繪製到一半就斷掉。
        if(entry == null)
        {
            if(!ReportedIsHereNullEntry)
            {
                ReportedIsHereNullEntry = true;
                PluginLog.Information("[AddressBook] IsHere 提前結束：通訊錄項目(entry) 為 null，視為「不在此處」。此訊息每次載入插件只顯示一次。");
            }
            return false;
        }
        var h = HousingManager.Instance();
        if(h == null) return false;
        var player = Player.Object;
        if(player == null)
        {
            if(!ReportedIsHereNullPlayer)
            {
                ReportedIsHereNullPlayer = true;
                PluginLog.Information("[AddressBook] IsHere 提前結束：Player.Object(本機角色) 為 null，通常是登入畫面、角色選擇畫面或正在切換區域。視為「不在此處」。此訊息每次載入插件只顯示一次。");
            }
            return false;
        }
        if(entry.World != player.CurrentWorld.RowId) return false;
        if(h->GetCurrentWard() != entry.Ward - 1) return false;
        if(Utils.GetResidentialAetheryteByTerritoryType(P.Territory) != entry.City) return false;
        if(entry.PropertyType == PropertyType.House)
        {
            return h->GetCurrentPlot() == entry.Plot - 1;
        }
        if(entry.PropertyType == PropertyType.Apartment)
        {
            if(entry.ApartmentSubdivision && h->GetCurrentDivision() != 2) return false;
            return entry.Apartment == h->GetCurrentRoom();
        }
        return false;
    }

    public static bool IsQuickTravelAvailable(this AddressBookEntry entry)
    {
        if(S.Data.ResidentialAethernet.HousingData.Data.SafeSelect(entry.City.GetResidentialTerritory())?.SafeSelect(entry.Ward - 1)?.AethernetID.EqualsAny(ResidentialAethernet.StartingAetherytes) != false) return false;
        var h = HousingManager.Instance();
        return h != null && entry.City.GetResidentialTerritory() == P.Territory && Player.Available && h->GetCurrentWard() == entry.Ward - 1 && S.Data.ResidentialAethernet.ActiveAetheryte != null && entry.World == Player.Object.CurrentWorld.RowId;
    }

    public static void GoTo(this AddressBookEntry entry)
    {
        if(!Player.Available)
        {
            Notify.Error($"Can not travel while character is not available");
            return;
        }
        if(!S.Data.DataStore.DCWorlds.Contains(ExcelWorldHelper.GetName(entry.World)) && !S.Data.DataStore.Worlds.Contains(ExcelWorldHelper.GetName(entry.World)))
        {
            Notify.Error($"Can not travel to {ExcelWorldHelper.GetName(entry.World)}");
            return;
        }
        if(entry.IsQuickTravelAvailable())
        {
            if(entry.PropertyType == PropertyType.House)
            {
                TaskTpAndGoToWard.EnqueueFromResidentialAetheryte(entry.City, entry.Plot - 1, false, default, false);
            }
            else if(entry.PropertyType == PropertyType.Apartment)
            {
                TaskTpAndGoToWard.EnqueueFromResidentialAetheryte(entry.City, entry.Apartment - 1, true, entry.ApartmentSubdivision, false);
            }
        }
        else
        {
            if(entry.PropertyType == PropertyType.House)
            {
                TaskTpAndGoToWard.Enqueue(ExcelWorldHelper.GetName(entry.World), entry.City, entry.Ward, entry.Plot - 1, false, default);
            }
            else if(entry.PropertyType == PropertyType.Apartment)
            {
                TaskTpAndGoToWard.Enqueue(ExcelWorldHelper.GetName(entry.World), entry.City, entry.Ward, entry.Apartment - 1, true, entry.ApartmentSubdivision);
            }
        }
    }

    public static string GetAutoName(this AddressBookEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(ExcelWorldHelper.GetName(entry.World));
        builder.Append(", ");
        builder.Append(TabAddressBook.ResidentialNames.SafeSelect(entry.City) ?? "???");
        builder.Append(", Ward ");
        builder.Append(entry.Ward);
        if(entry.PropertyType == PropertyType.House)
        {
            builder.Append(", Plot ");
            builder.Append(entry.Plot);
        }
        if(entry.PropertyType == PropertyType.Apartment)
        {
            builder.Append(", Apartment ");
            builder.Append(entry.Apartment);
            if(entry.ApartmentSubdivision)
            {
                builder.Append(" (subdivision)");
            }
        }
        return builder.ToString();
    }
}
