using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.EzHookManager;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Enums;
using Lifestream.Tasks.SameWorld;
using System.Windows.Forms;

namespace Lifestream.Game;

public unsafe class Memory : IDisposable
{
    internal delegate void AddonDKTWorldList_ReceiveEventDelegate(nint a1, short a2, nint a3, AtkEvent* a4, InputData* a5);
    [Signature("48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? F6 81", DetourName = nameof(AddonDKTWorldList_ReceiveEventDetour), Fallibility = Fallibility.Fallible)]
    internal Hook<AddonDKTWorldList_ReceiveEventDelegate> AddonDKTWorldList_ReceiveEventHook;

    internal delegate void AtkComponentTreeList_vf31(nint a1, uint a2, byte a3);
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B DA 41 0F B6 F0", DetourName = nameof(AtkComponentTreeList_vf31Detour), Fallibility = Fallibility.Fallible)]
    internal Hook<AtkComponentTreeList_vf31> AtkComponentTreeList_vf31Hook;

    [Signature("4C 8D 0D ?? ?? ?? ?? 4C 8B 11 48 8B D9", ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Fallible)]
    internal int* MaxInstances;

    internal delegate byte OpenPartyFinderInfoDelegate(void* agentLfg, ulong contentId);
    [EzHook("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53", false)]
    internal EzHook<OpenPartyFinderInfoDelegate> OpenPartyFinderInfoHook;

    internal delegate nint IsFlightProhibitedDelegate(nint a1);
    internal IsFlightProhibitedDelegate IsFlightProhibited = EzDelegate.Get<IsFlightProhibitedDelegate>("40 53 48 83 EC 20 48 8B 1D ?? ?? ?? ?? 48 85 DB 0F 84 ?? ?? ?? ?? 80 3D");
    internal nint FlightAddr = Svc.SigScanner.TryScanText("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 11", out var result) ? result : default;

    // fail-closed:detour 是「原生程式碼直接呼叫的受管理函式」,受管理例外從這裡逸出會穿過
    // 沒有 SEH handler 的原生框架,行程直接被終止。所以這三支的自訂邏輯(全部只是記錄)一律進 try,
    // 而 **Original 一律留在 try 外照樣呼叫** —— 我們的記錄失敗絕不能改變遊戲原本的行為。
    // ⚠️ try 攔不到 AccessViolationException(在 .NET Core 是 corrupted-state exception)。
    //    針對裸指標的防護是下面那些判空,不是 try。
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(string site, Exception ex)
    {
        ++_detourErrors;
        // 節流:這些 detour 可能連續觸發,不節流會把 log 灌爆反而讓使用者回報不出東西。
        // Information 而不是 Debug —— 回報問題的使用者跑 LogLevel 2。
        var now = DateTime.UtcNow;
        if(now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        PluginLog.Information($"[Memory] {site} 的診斷記錄擲出受管理例外,已吞下、Original 照常執行(累計 {_detourErrors}): {ex}");
    }

    internal byte OpenPartyFinderInfoDetour(void* agentLfg, ulong contentId)
    {
        try
        {
            PluginLog.Information($"{((nint)agentLfg):X16}, {contentId:X16}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(OpenPartyFinderInfoDetour), ex);
        }
        return OpenPartyFinderInfoHook.Original(agentLfg, contentId);
    }

    private void AtkComponentTreeList_vf31Detour(nint a1, uint a2, byte a3)
    {
        try
        {
            PluginLog.Debug($"AtkComponentTreeList_vf31Detour: {a1:X16}, {a2}, {a3}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(AtkComponentTreeList_vf31Detour), ex);
        }
        AtkComponentTreeList_vf31Hook.Original(a1, a2, a3);
    }

    private void AddonDKTWorldList_ReceiveEventDetour(nint a1, short a2, nint a3, AtkEvent* a4, InputData* a5)
    {
        try
        {
            PluginLog.Debug($"AddonDKTWorldCheck_ReceiveEventDetour: {a1:X16}, {a2}, {a3:X16}, {(nint)a4:X16}, {(nint)a5:X16}");
            // 🔴 a4/a5 是遊戲(或 ConstructEvent)交進來的裸指標,解參考失敗是攔不到的 AVE ⇒ 只能靠判空。
            if(a4 != null)
                PluginLog.Debug($"  Event: {(nint)a4->Node:X16}, {(nint)a4->Target:X16}, {(nint)a4->Listener:X16}, {a4->Param}, {(nint)a4->NextEvent:X16}, {a4->State.EventType}, {a4->State.ReturnFlags}, {a4->State.StateFlags}");
            if(a5 != null)
            {
                // InputData.unk_8s 是 (UnknownStruct*)*unk_8 —— 兩層解參考,兩層都要驗
                var inner = a5->unk_8 != null ? *a5->unk_8 : (nint)0;
                if(inner != 0)
                    PluginLog.Debug($"  Data: {(nint)a5->unk_8:X16}({inner:X16}/{inner:X16}), [{a5->unk_8s->unk_4}/{a5->unk_8s->SelectedItem}] {a5->unk_16}, {a5->unk_24} | "); //{a5->RawDumpSpan.ToArray().Print()}
                else
                    PluginLog.Debug($"  Data: {(nint)a5->unk_8:X16}(null), {a5->unk_16}, {a5->unk_24} | ");
            }
            //var span = new Span<byte>((void*)*a5->unk_8, 0x40).ToArray().Select(x => $"{x:X2}");
            //PluginLog.Debug($"  Data 2, {a5->unk_8s->unk_4}, {MemoryHelper.ReadRaw((nint)a5->unk_8s->CategorySelection, 4).Print(",")},  :{string.Join(" ", span)}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(AddonDKTWorldList_ReceiveEventDetour), ex);
        }
        AddonDKTWorldList_ReceiveEventHook.Original(a1, a2, a3, a4, a5);
    }

    internal void ConstructEvent(AtkUnitBase* addon, int category, int which, int nodeIndex, int itemToSelect, int itemToHighlight)
    {
        if(itemToSelect == 0) throw new Exception("Enumeration starts with 1");
        var Event = stackalloc AtkEvent[1]
        {
            new AtkEvent()
            {
                Node = null,
                Target = (AtkEventTarget*)addon->UldManager.NodeList[nodeIndex],
                Listener = &addon->UldManager.NodeList[nodeIndex]->GetAsAtkComponentNode()->Component->AtkEventListener,
                Param = 1,
                NextEvent = null,
                State = new()
                {
                    EventType = AtkEventType.ListItemClick,
                    ReturnFlags = 0,
                    StateFlags = 0,
                    UnkFlags3 = 0,
                }
            }
        };
        var Unk = stackalloc UnknownStruct[1]
        {
            new()
            {
                unk_4 = 1,
                SelectedItem = itemToSelect - 1 + (category << 8)
            }
        };
        var ptr = stackalloc nint[1]
        {
            (nint)Unk
        };
        var Data = stackalloc InputData[1]
        {
            new InputData()
            {
                unk_8 = ptr,
                unk_16 = itemToSelect,
                unk_24 = 0,
            }
        };
        AddonDKTWorldList_ReceiveEventDetour((nint)addon, 35, which, Event, Data);
        AtkComponentTreeList_vf31Detour((nint)addon->UldManager.NodeList[nodeIndex]->GetAsAtkComponentList(), (uint)itemToHighlight, 0);
    }

    internal Memory()
    {
        SignatureHelper.Initialise(this);
        EzSignatureHelper.Initialize(this);
        //AddonDKTWorldList_ReceiveEventHook.Enable();
    }

    public void Dispose()
    {
        AddonDKTWorldList_ReceiveEventHook?.Disable();
        AddonDKTWorldList_ReceiveEventHook?.Dispose();
        AtkComponentTreeList_vf31Hook?.Disable();
        AtkComponentTreeList_vf31Hook?.Dispose();
    }
}
