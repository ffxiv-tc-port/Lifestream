using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Lifestream.Game;

/// <summary>好友房屋傳送清單的格序；數值＝代理人格序＝視窗 callback 值。</summary>
public enum FriendEstateKind
{
    FreeCompany = 0,
    Private = 1,
    Apartment = 2,
}

/// <summary>清單上的一格；<c>CanEnter</c>＝好友開放進門（遊戲清單上的旗標）。</summary>
public readonly record struct FriendEstateEntry(FriendEstateKind Kind, HouseId House, uint AetheryteId, uint Cost, bool CanEnter);

/// <summary>
/// 「好友房屋傳送」視窗（TeleportHousingFriend）與同名代理人的唯讀快照；只在主執行緒、每次現讀，不保存任何指標。
/// </summary>
/// <remarks>
/// 本檔契約（台服 7.3 執行檔離線讀出）：代理人 +0x28 起 3 格、每格 0x18＝HouseId(+0)、以太之光(+8，0＝這格沒房屋)、
/// 費用(+0xC)、可進門旗標(+0x10)；+0x78＝最後一次請求的好友 ContentId。
/// 視窗 AtkValues：[0]＝筆數（Int，開窗時 -1＝讀取中）；第 k 筆 [1+7k]＝旗標、[2+7k]＝格序（皆 UInt）。
/// 兩邊對不上一律回 <see cref="FriendEstateReadState.Mismatch"/>：布局一變就停手，不猜。
/// </remarks>
public static unsafe class FriendEstateList
{
    public const string AddonName = "TeleportHousingFriend";

    private const int EntriesOffset = 0x28;
    private const int EntryStride = 0x18;
    private const int EntryCount = 3;
    private const int RequestedContentIdOffset = 0x78;
    private const int ValuesPerRow = 7;

    public static FriendEstateReadState TryRead(AtkUnitBase* addon, ulong expectedContentId, out List<FriendEstateEntry> entries, out string diag)
    {
        entries = [];
        diag = null;
        if(addon == null || addon->AtkValues == null || addon->AtkValuesCount < 1) return FriendEstateReadState.Loading;
        var module = AgentModule.Instance();
        var agent = module == null || AgentModule.MemberFunctionPointers.GetAgentByInternalId == null
            ? null
            : (byte*)module->GetAgentByInternalId(AgentId.TeleportHousingFriend);
        return Evaluate(addon->AtkValues, addon->AtkValuesCount, agent, expectedContentId, out entries, out diag);
    }

    /// <summary>純判讀（不碰任何全域）；<paramref name="agent"/> 可為 null。</summary>
    internal static FriendEstateReadState Evaluate(AtkValue* values, int valueCount, byte* agent, ulong expectedContentId, out List<FriendEstateEntry> entries, out string diag)
    {
        entries = [];
        diag = null;
        if(values == null || valueCount < 1) return FriendEstateReadState.Loading;
        if(values[0].Type != AtkValueType.Int)
        {
            diag = $"values[0].Type={values[0].Type}";
            return FriendEstateReadState.Mismatch;
        }
        var count = values[0].Int;
        if(count < 0) return FriendEstateReadState.Loading;
        if(count > EntryCount || valueCount < 1 + ValuesPerRow * count)
        {
            diag = $"count={count}, valueCount={valueCount}";
            return FriendEstateReadState.Mismatch;
        }
        if(agent == null)
        {
            diag = "agent=null";
            return FriendEstateReadState.Mismatch;
        }
        if(*(ulong*)(agent + RequestedContentIdOffset) != expectedContentId)
        {
            diag = "agent ContentId 不是這次請求的好友";
            return FriendEstateReadState.Mismatch;
        }

        var fromAgent = new List<FriendEstateEntry>(EntryCount);
        for(var i = 0; i < EntryCount; i++)
        {
            var p = agent + EntriesOffset + EntryStride * i;
            var aetheryte = *(uint*)(p + 0x8);
            if(aetheryte == 0) continue;
            fromAgent.Add(new((FriendEstateKind)i, *(HouseId*)p, aetheryte, *(uint*)(p + 0xC), *(p + 0x10) != 0));
        }
        if(fromAgent.Count != count)
        {
            diag = $"視窗 {count} 筆、代理人 {fromAgent.Count} 筆";
            return FriendEstateReadState.Mismatch;
        }
        var seen = 0u;
        for(var k = 0; k < count; k++)
        {
            var flag = values[1 + ValuesPerRow * k];
            var kind = values[2 + ValuesPerRow * k];
            if(flag.Type != AtkValueType.UInt || kind.Type != AtkValueType.UInt)
            {
                diag = $"row {k}: types {flag.Type}/{kind.Type}";
                return FriendEstateReadState.Mismatch;
            }
            if(kind.UInt >= EntryCount || (seen & (1u << (int)kind.UInt)) != 0
                || !fromAgent.TryGetFirst(x => (uint)x.Kind == kind.UInt, out var match) || match.CanEnter != (flag.UInt != 0))
            {
                diag = $"row {k}: kind={kind.UInt}, flag={flag.UInt}";
                return FriendEstateReadState.Mismatch;
            }
            seen |= 1u << (int)kind.UInt;
        }
        entries = fromAgent;
        return FriendEstateReadState.Ready;
    }
}

public enum FriendEstateReadState
{
    Loading,
    Ready,
    Mismatch,
}
