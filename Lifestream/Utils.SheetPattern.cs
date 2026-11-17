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
    public static string ParseSheetPattern(string s)
    {
        try
        {
            {
                var match = ParseSheetValueRegex().Match(s);
                if(match.Success)
                {
                    var type = typeof(Addon).Assembly.GetType($"Lumina.Excel.Sheets.{match.Groups[1].Value}", true);
                    var rowId = uint.Parse(match.Groups[2].Value);
                    var col = match.Groups[3].Value;
                    var result = Svc.Data.GetType().GetMethod("GetExcelSheet", ReflectionHelper.AllFlags).MakeGenericMethod([type]).Invoke(Svc.Data, [null, null]).Call("GetRow", [rowId]).GetFoP(col);
                    if(result is ReadOnlySeString ross)
                    {
                        return ross.GetText();
                    }
                    return result.ToString();
                }
            }
            {
                var match = ParseQuestDialogueTextSheetRegex().Match(s);
                if(match.Success)
                {
                    var sheet = Svc.Data.GetExcelSheet<QuestDialogueText>(name: match.Groups[1].Value);
                    var rowId = uint.Parse(match.Groups[2].Value);
                    var col = match.Groups[3].Value;
                    var result = sheet.Call("GetRow", [rowId]).GetFoP(col);
                    if(result is ReadOnlySeString ross)
                    {
                        if(match.Groups[4].Success)
                        {
                            ross = ResolveSheetMacros(ross, [.. match.Groups[4].Value.Split(',').Select(uint.Parse)]);
                        }
                        return ross.GetText();
                    }
                    return result.ToString();
                }
            }
            return s;
        }
        catch(Exception e)
        {
            e.Log();
            return s;
        }
    }

    [GeneratedRegex(@"^<([a-z]+):([0-9]+):([a-z]+)>$", RegexOptions.IgnoreCase)]
    private static partial Regex ParseSheetValueRegex();

    // 選用的第 4 段是逗號分隔的 lnum1、lnum2…，只用來代換表字串裡的 <sheet(表,列,欄)> 巨集；
    // 不給時走原本的 GetText（巨集整段剝掉）。
    [GeneratedRegex(@"^<QuestDialogueText:([a-z_/0-9]+):([0-9]+):([a-z]+)(?::([0-9]+(?:,[0-9]+)*))?>$", RegexOptions.IgnoreCase)]
    private static partial Regex ParseQuestDialogueTextSheetRegex();

    /// <summary>
    /// 把 <c>&lt;sheet(表,列,欄)&gt;</c> 巨集換成該表該欄的字串（列、欄可為常數或 lnumN）。
    /// 其他巨集與解不出來的 sheet 巨集原樣保留，之後照舊由 GetText 剝掉。
    /// </summary>
    private static ReadOnlySeString ResolveSheetMacros(ReadOnlySeString str, uint[] localNumbers)
    {
        var sb = new Lumina.Text.SeStringBuilder();
        foreach(var payload in str.AsSpan())
        {
            if(payload.Type == ReadOnlySePayloadType.Macro && payload.MacroCode == Lumina.Text.Payloads.MacroCode.Sheet && TryReadSheetMacro(payload, localNumbers, out var text))
            {
                sb.Append(text);
            }
            else
            {
                sb.Append(payload);
            }
        }
        return sb.ToReadOnlySeString();
    }

    private static bool TryReadSheetMacro(ReadOnlySePayloadSpan payload, uint[] localNumbers, out ReadOnlySeString text)
    {
        text = default;
        var args = payload.GetEnumerator();
        if(!args.MoveNext() || !args.Current.TryGetString(out var sheetNameStr)) return false;
        if(!args.MoveNext() || !TryResolveMacroNumber(args.Current, localNumbers, out var rowId)) return false;
        if(!args.MoveNext() || !TryResolveMacroNumber(args.Current, localNumbers, out var colIndex)) return false;
        var sheetName = sheetNameStr.ExtractText();
        if(!Svc.Data.Excel.SheetNames.Contains(sheetName)) return false;
        if(!Svc.Data.GetExcelSheet<Lumina.Excel.RawRow>(name: sheetName).TryGetRow(rowId, out var row)) return false;
        if(colIndex >= row.Columns.Count || row.Columns[(int)colIndex].Type != Lumina.Data.Structs.Excel.ExcelColumnDataType.String) return false;
        text = row.ReadStringColumn((int)colIndex);
        return true;
    }

    private static bool TryResolveMacroNumber(ReadOnlySeExpressionSpan expr, uint[] localNumbers, out uint value)
    {
        if(expr.TryGetUInt(out value)) return true;
        if(expr.TryGetParameterExpression(out var type, out var operand)
            && (Lumina.Text.Expressions.ExpressionType)type == Lumina.Text.Expressions.ExpressionType.LocalNumber
            && operand.TryGetUInt(out var index) && index >= 1 && index <= localNumbers.Length)
        {
            value = localNumbers[index - 1];
            return true;
        }
        value = 0;
        return false;
    }
}
