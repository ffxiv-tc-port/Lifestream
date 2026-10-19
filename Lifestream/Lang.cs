using Dalamud.Utility;
using Lifestream.Enums;
using Lumina.Excel.Sheets;
using System.Text.RegularExpressions;

namespace Lifestream;

internal static partial class Lang
{
    public const string SymbolWard = "";
    public const string SymbolPlot = "";
    public const string SymbolApartment = "";
    public const string SymbolSubdivision = "";
    public static readonly (string Normal, string GameFont) Digits = ("0123456789", "");

    static Lang()
    {
        GameTextMatcher.Excel = static () => Svc.Data.Excel;
        GameTextMatcher.Log = static message => PluginLog.Information(message);
    }

    // Localized line by line rather than as one giant block, so each line stays a clean
    // single-line ini key. SearchHelperOverlay.ParseCommandDescriptions() parses this text
    // with regexes, so every translation MUST keep the literal "/li <command>" prefix, the
    // "→" separator and any "alias: /li a|b|c" fragment intact.
    public static string Help => string.Join("\n", new string[]
    {
        "-- Main Travel --".Loc(),
        "",
        "/li → go to your home world".Loc(),
        "/li <world> → go to specified world".Loc(),
        "/li <datacenter> → go to a random world of specified data center".Loc(),
        "/li <aethernet> → go to specified aethernet destination".Loc(),
        "/li <world>, tp <location> → go to specified aetheryte destination of specified world".Loc(),
        "/li <world>, tp <aethernet> → go to specified aethernet destination of specified world".Loc(),
        "",
        "-- Market Board --".Loc(),
        "",
        "/li mb → go to market board".Loc(),
        "/li <world> mb → go to market board of specified world".Loc(),
        "",
        "-- Estates --".Loc(),
        "",
        "/li auto → go to your private estate, shared estate, FC estate or apartment, based on configured preference".Loc(),
        "/li shared → go to your shared estate, based on configured preference".Loc(),
        "/li home → go to your private estate, alias: /li home|house|private".Loc(),
        "/li fc → go to your FC estate, alias: /li fc|free|company|free company".Loc(),
        "/li apt → go to your apartment, alias: /li apt|apartment".Loc(),
        "/li ws → go to your FC's workshop, alias: /li ws|workshop".Loc(),
        "",
        "/li <district> <ward> <plot> → go to specified plot in current world".Loc(),
        "/li <world> <district> <ward> <plot> → go to specified plot of specified world".Loc(),
        "Examples: /li lavender 1 30, /li goblet 1 30, /li mist 1 30".Loc(),
        "",
        "-- Grand Company --".Loc(),
        "",
        "/li gc → go to your grand company, alias: /li gc|hcc".Loc(),
        "/li gc <grandcompany> → go to specified grand company, alias: /li gc|hcc <grandcompany>".Loc(),
        "/li gcc → go to your grand company city's FC chest, alias: /li gc|hcc".Loc(),
        "/li gcc <grandcompany> → go to specified grand company city's FC chest, alias: /li gc|hcc <grandcompany>".Loc(),
        "Using \"hc\" or \"hcc\" instead of \"gc\" or \"gcc\" moves to you to your home world first".Loc(),
        "",
        "-- Others --".Loc(),
        "",
        "/li cosmic → go to Sinus Ardorum, alias: /li cosmic|moon|ardorum".Loc(),
        "/li island → go to Island Sanctuary".Loc(),
        "/li goto <name> → go to a saved custom destination (teleport + navigation)".Loc(),
        "/li panel → open the teleport panel (search, favorites, remarks, map preview)".Loc(),
        "/li fav → open the favorites window (custom order and categories), alias: /li fav|favorites".Loc(),
        "/li w → open world travel window, alias: /li w|world|open|select".Loc(),
        "/lifestream → open plugin configuration".Loc(),
    });
    internal static string[] AdditionalChambersEntrance =>
    [
        Svc.Data.GetExcelSheet<EObjName>().GetRow(2004353).Singular.GetText(),
        Regex.Replace(Svc.Data.GetExcelSheet<EObjName>().GetRow(2004353).Singular.GetText(), @"\[.*?\]", "")
    ];

    internal static Dictionary<WorldChangeAetheryte, string> WorldChangeAetherytes = new()
    {
        [WorldChangeAetheryte.Gridania] = "New Gridania",
        [WorldChangeAetheryte.Uldah] = "Ul'Dah - Steps of Thal",
        [WorldChangeAetheryte.Limsa] = "Limsa Lominsa Lower Decks (not recommended)"
    };

    internal static class Symbols
    {
        internal const string HomeWorld = "";
        internal const string HighQuality = "";
    }

    //2000151	Aethernet shard	0	Aethernet shards	0	1	1	0	0
    internal static string AethernetShard => Svc.Data.GetExcelSheet<EObjName>().GetRow(2000151).Singular.ToDalamudString().GetText();

    // transport/AetheryteBestwaysBurrow 1 TEXT_AETHERYTEBESTWAYSBURROW_SYSTEM_C1W1 <Gui(69)/> 前往渴望灣
    //比照蒼天街:嘆息海「最佳威兔洞」乙太之光(175)的選單有直達渴望灣的選項。
    //顯示文字執行期從表解析(語言無關);寫死的只是表讀不到時的最後防線。
    internal static string[] TravelToSinusArdorum
    {
        get
        {
            field ??= BuildSheetMatchers(
                () => Svc.Data.GetExcelSheet<ECommons.ExcelServices.Sheets.QuestDialogueText>(name: "transport/AetheryteBestwaysBurrow").GetRowOrDefault(1)?.Value.ExtractText(),
                "前往渴望灣");
            return field;
        }
    }

    //Addon#2091	隨意（自動選擇） — 「前往渴望灣」之後可能出現的副本區選擇選單的自動選項
    internal static string[] AnyInstance
    {
        get
        {
            field ??= BuildSheetMatchers(
                () => Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(2091)?.Text.ExtractText(),
                "隨意（自動選擇）");
            return field;
        }
    }

    private static string[] BuildSheetMatchers(Func<string> resolver, params string[] fallbacks)
    {
        try
        {
            var resolved = resolver()?.Trim();
            if(resolved != null && resolved != "")
            {
                return [resolved, .. fallbacks.Where(x => x != resolved)];
            }
        }
        catch(Exception e)
        {
            e.Log();
        }
        return fallbacks;
    }

    public static readonly string AethernetShardTooltipPlaceholder = Svc.Data.GetExcelSheet<PlaceName>().GetRow(1300).Name.GetText();
}
