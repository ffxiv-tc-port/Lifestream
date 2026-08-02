using ECommons.DalamudServices;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;

namespace Lifestream;

/// <summary>
/// Provides the live-world view used by Lifestream.
/// </summary>
internal static class PublicWorlds
{
    // Taiwan 7.3 ships its eight production worlds with World.IsPublic = false.
    // Keep the compatibility exception narrow so lobby and development worlds
    // outside the Taiwan production data center remain filtered out.
    internal const uint TaiwanDataCenterId = 151;
    internal const uint TaiwanFirstWorldId = 4028;
    internal const uint TaiwanLastWorldId = 4035;

    private static readonly Dictionary<string, uint> TaiwanWorldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["伊弗利特"] = 4028,
        ["火神"] = 4028,
        ["Ifrit"] = 4028,
        ["迦樓羅"] = 4029,
        ["風神"] = 4029,
        ["Garuda"] = 4029,
        ["利維坦"] = 4030,
        ["Leviathan"] = 4030,
        ["鳳凰"] = 4031,
        ["Phoenix"] = 4031,
        ["奧汀"] = 4032,
        ["Odin"] = 4032,
        ["巴哈姆特"] = 4033,
        ["巴哈"] = 4033,
        ["Bahamut"] = 4033,
        ["拉姆"] = 4034,
        ["Ramuh"] = 4034,
        ["泰坦"] = 4035,
        ["Titan"] = 4035,
    };

    internal static bool IsTaiwanWorld(uint worldId)
        => worldId is >= TaiwanFirstWorldId and <= TaiwanLastWorldId;

    internal static bool IsTaiwanWorld(World world)
        => IsTaiwanWorld(world.RowId);

    internal static bool IsPublic(World world)
        => world.IsPublic
            || IsTaiwanWorld(world);

    // ECommons 的 ExcelWorldHelper.Region 列舉只有 JP=1/NA=2/EU=3/OC=4，沒有台服的 8
    // （陸行鳥 WorldDCGroupType(151).Region == 8）。GetRegion() 是把該欄位直接轉型回來的，
    // 所以 (Region)8 在執行期拿得到、只是沒有名字 —— 任何用 Enum.GetValues<Region>()
    // 迭代地區的 UI 都會完全漏掉台服（見 Utils.DrawWorldSelector）。
    internal const byte TaiwanRegionByte = 8;

    internal static ExcelWorldHelper.Region TaiwanRegion => (ExcelWorldHelper.Region)TaiwanRegionByte;

    /// <summary>所有實際存在世界的地區，含 ECommons 列舉裡沒有的台服。</summary>
    internal static ExcelWorldHelper.Region[] AllRegions()
        => [.. Enum.GetValues<ExcelWorldHelper.Region>(), TaiwanRegion];

    /// <summary>地區的顯示名稱；未命名的台服地區 ToString() 只會印出裸數字 "8"，這裡補上名稱。</summary>
    internal static string RegionDisplayName(ExcelWorldHelper.Region region)
        => (byte)region == TaiwanRegionByte ? "Taiwan" : region.ToString();

    internal static World[] Get(ExcelWorldHelper.Region? region = null)
        => [.. Svc.Data.GetExcelSheet<World>()
            .Where(world => IsPublic(world)
                && (region == null || world.GetRegion() == region.Value))];

    internal static World[] Get(uint dataCenter)
        => dataCenter == TaiwanDataCenterId
            ? GetTaiwanWorlds()
            : [.. Svc.Data.GetExcelSheet<World>()
                .Where(world => IsPublic(world) && world.DataCenter.RowId == dataCenter)];

    internal static World[] GetTaiwanWorlds()
        => [.. Svc.Data.GetExcelSheet<World>()
            .Where(IsTaiwanWorld)
            .OrderBy(world => world.RowId)];

    internal static string NormalizeTaiwanWorldName(string input)
    {
        var trimmed = input.Trim();
        if(!TaiwanWorldAliases.TryGetValue(trimmed, out var worldId))
            return trimmed;

        var world = Svc.Data.GetExcelSheet<World>().GetRowOrDefault(worldId);
        return world?.Name.ToString() ?? trimmed;
    }
}
