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
    public static bool IsAddonVisible(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible) return true;
        return false;
    }

    // 參數用具體型別：foreach 走 HashSet 的結構列舉器，疊加介面每幀判斷時不再裝箱。
    internal static bool IsAddonsVisible(HashSet<string> addons)
    {
        foreach(var x in addons)
        {
            if(TryGetAddonByName<AtkUnitBase>(x, out var a) && a->IsVisible) return true;
        }
        return false;
    }

    internal static bool IsMapActive()
    {
        if(TryGetAddonByName<AtkUnitBase>("AreaMap", out var map) && IsAddonReady(map) && map->IsVisible)
        {
            return true;
        }
        return false;
    }

    internal static AtkUnitBase* GetSpecificYesno(params string[] s) => GetSpecificYesno(false, s);

    internal static AtkUnitBase* GetSpecificYesno(bool contains, params string[] s) => GetSpecificYesno(rawText =>
    {
        var text = rawText.Replace(" ", "");
        return contains ?
            text.ContainsAny(s.Select(x => x.Replace(" ", "")))
            : text.EqualsAny(s.Select(x => x.Replace(" ", "")));
    }, () => s.Print());

    internal static AtkUnitBase* GetSpecificYesno(GameTextMatcher text, Func<string, string[], bool> legacy)
        => GetSpecificYesno(rawText => text.Matches(rawText, legacy), text.Describe);

    private static AtkUnitBase* GetSpecificYesno(Func<string, bool> matches, Func<string> describe)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    // 讀不到就跳過這個 SelectYesno 繼續往下掃(fail-closed):
                    // 不能拿空字串去比對,否則「讀不到」會被誤判成「內容為空的相符」而按下確認。
                    if(!TryGetNodeText(addon, 15, out var rawText)) continue;
                    // 讀到 U+FFFD ＝ 這扇窗的記憶體正在變動(多半是關閉中),這一幀當作沒比中,不要按它。
                    if(AddonPressGuard.IsTextUnstable("SelectYesno", rawText)) continue;
                    if(matches(rawText))
                    {
                        PluginLog.Verbose($"SelectYesno {describe()} addon {i}");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    internal static AtkUnitBase* GetLogOutYesno()
    {
        var texts = GetLogOutYesnoTexts();
        // 兩列都取不到才會是空的;此時不要拿空陣列去比對,否則等於「任何視窗都不相符」以外還可能誤按。
        if(texts.Length == 0) return null;
        return GetSpecificYesno(texts);
    }

    private static string[] GetLogOutYesnoTexts()
    {
        if(LogOutYesnoTextsCache != null) return LogOutYesnoTextsCache;
        var sheet = Svc.Data.GetExcelSheet<Addon>();
        var texts = new List<string>();
        var hasNormal = sheet.TryGetRow(LogOutYesnoAddonRow, out var normal);
        if(hasNormal) texts.Add(normal.Text.GetText());
        var hasQueued = sheet.TryGetRow(LogOutYesnoQueuedForDutyAddonRow, out var queued);
        if(hasQueued) texts.Add(queued.Text.GetText());
        LogOutYesnoTextsCache = [.. texts];
        PluginLog.Information($"[Lifestream] 登出確認視窗文字解析完成:Addon#{LogOutYesnoAddonRow}={(hasNormal ? "有" : "無")}、" +
            $"Addon#{LogOutYesnoQueuedForDutyAddonRow}={(hasQueued ? "有" : "無(台服常態,排隊等副本時的登出沿用一般判定)")};" +
            $"共 {LogOutYesnoTextsCache.Length} 組候選文字。");
        return LogOutYesnoTextsCache;
    }

    internal static bool TrySelectSpecificEntry(string text, Func<bool> Throttle)
    {
        return TrySelectSpecificEntry(new string[] { text }, Throttle);
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text, Func<bool> Throttle)
        => TrySelectSpecificEntry(entries => entries.FindIndex(x => x.EqualsAny(text)), () => text.Print(), Throttle);

    internal static bool TrySelectSpecificEntry(GameTextMatcher text, Func<bool> Throttle)
        => TrySelectSpecificEntry(entries => text.FindIndex(entries, LegacyMatch.EqualsAny), text.Describe, Throttle);

    private static bool TrySelectSpecificEntry(Func<List<string>, int> pick, Func<string> describe, Func<bool> Throttle)
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entries = GetEntries(addon);
            // 讀到 U+FFFD ＝ 選單記憶體正在變動(多半是上一層選單關閉中),這一幀不碰。
            if(AddonPressGuard.AnyTextUnstable("SelectString", entries)) return false;
            var index = pick(entries);
            if(index >= 0)
            {
                var entry = entries[index];
                // SelectString 全外掛唯一的 choke point。粒度=(窗,位址,索引):同一扇仍開著的選單
                // 選不同項目照常放行,只擋「同位址同索引在窗走完前再選」。被擋回 false 與「還沒找到」走同一條路。
                if(index >= 0 && Throttle() && AddonPressGuard.TryPressOnce("SelectString", addon, "TrySelectSpecificEntry", paramKey: index.ToString()))
                {
                    new AddonMaster.SelectString(addon).Entries[index].Select();
                    PluginLog.Debug($"TrySelectSpecificEntry: selecting {entry}/{index} as requested by {describe()}");
                    return true;
                }
            }
        }
        return false;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for(var i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText().Trim());
        }
        //PluginLog.Debug($"{list.Print()}");
        return list;
    }
}
