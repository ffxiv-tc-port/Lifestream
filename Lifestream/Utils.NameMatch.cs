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
    public static bool TryFindEqualsOrContains<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, string needle, out T result, bool partialRequiresUnique = false)
    {
        return TryFindEqualsOrContains(haystack, haystackConverterToString, [needle], out result, partialRequiresUnique);
    }

    /// <param name="partialRequiresUnique">
    /// true＝相等比不到時，前綴／包含兩個退路只在恰好命中一項時採用；命中多項回 false（呼叫端不按）並記一行 Information。
    /// false＝沿用原行為（取第一個命中）。
    /// </param>
    public static bool TryFindEqualsOrContains<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, IEnumerable<string> needles, out T result, bool partialRequiresUnique = false)
    {
        if(TryFindEquals(haystack, haystackConverterToString, needles, out result)) return true;
        if(partialRequiresUnique)
        {
            return TryFindUniquePartial(haystack, haystackConverterToString, needles, out result, out _);
        }
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).StartsWith(n, StringComparison.OrdinalIgnoreCase))
                {
                    result = x;
                    return true;
                }
            }
        }
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).Contains(n, StringComparison.OrdinalIgnoreCase))
                {
                    result = x;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    /// <summary>相等優先；前綴／包含只在恰好命中一項時採用（同 <c>partialRequiresUnique: true</c>）。</summary>
    /// <param name="ambiguous">部分比對命中多項時＝那些項目的文字（已記 Information、回 false）；其他情況為 null。</param>
    public static bool TryFindEqualsOrUniquePartial<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, IEnumerable<string> needles, out T result, out string[] ambiguous)
    {
        ambiguous = null;
        if(TryFindEquals(haystack, haystackConverterToString, needles, out result)) return true;
        return TryFindUniquePartial(haystack, haystackConverterToString, needles, out result, out ambiguous);
    }

    private static bool TryFindEquals<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, IEnumerable<string> needles, out T result)
    {
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).EqualsIgnoreCase(n))
                {
                    result = x;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    // 只在框架執行緒呼叫（記 log 的去重欄位沒有鎖）。包含命中集合必定涵蓋前綴命中集合，所以前綴多筆就不必再試包含。
    private static bool TryFindUniquePartial<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, IEnumerable<string> needles, out T result, out string[] ambiguous)
    {
        ambiguous = null;
        foreach(var (kind, match) in PartialMatchers)
        {
            var hits = haystack.Where(x => needles.Any(n => match(haystackConverterToString(x), n))).ToList();
            if(hits.Count == 1)
            {
                result = hits[0];
                return true;
            }
            if(hits.Count > 1)
            {
                ambiguous = [.. hits.Select(haystackConverterToString)];
                var texts = string.Join("／", ambiguous);
                var key = $"{kind}|{string.Join("／", needles)}|{texts}";
                var now = Environment.TickCount64;
                // 呼叫端每幀重試：同一組結果 10 秒內只記一次。
                if(key != LastAmbiguousPartialKey || now - LastAmbiguousPartialAt > 10_000)
                {
                    LastAmbiguousPartialKey = key;
                    LastAmbiguousPartialAt = now;
                    PluginLog.Information($"[Lifestream] 選單比對：「{string.Join("／", needles)}」沒有完全相同的選項，以{kind}比對命中 {hits.Count} 項（{texts}），無法確定是哪一項，這次不按。");
                }
                result = default;
                return false;
            }
        }
        result = default;
        return false;
    }
}
