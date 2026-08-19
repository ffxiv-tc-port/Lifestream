using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Configuration;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Services;
public unsafe class InstanceHandler : IDisposable
{
    private InstanceHandler()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "SelectString", OnPostUpdate);
        // Framework 是 isPointer: true 的靜態位址,合法回 null。
        // 建構子在外掛載入時就跑,拿不到就跳過版本比對(下次載入還會再比一次)。
        var framework = CSFramework.Instance();
        var gv = framework == null ? "" : framework->GameVersionString;
        if(!gv.IsNullOrEmpty() && gv != C.GameVersion)
        {
            PluginLog.Information($"New game version detected, new {gv}, old {C.GameVersion}");
            C.GameVersion = gv;
            C.PublicInstances = [];
        }
    }

    public bool CanChangeInstance()
    {
        // 開啟「先傳送至本區以太之光」時,即使附近沒有水晶、只要本區有已解鎖的以太之光也允許切線
        return C.ShowInstanceSwitcher && !Utils.IsDisallowedToUseAethernet() && !P.TaskManager.IsBusy && !IsOccupied() && S.InstanceHandler.GetInstance() != 0
            && (TaskChangeInstance.GetAetheryte() != null || (C.InstanceTpToAetheryte && TaskChangeInstance.GetZoneAetheryteId() != 0));
    }

    private void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        if(
            UIState.Instance()->PublicInstance.IsInstancedArea()
            && Svc.Targets.Target?.ObjectKind == ObjectKind.Aetheryte
            && Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
            && TryGetAddonMaster<AddonMaster.SelectString>(out var m)
            && m.IsAddonReady
            && (m.Entries.Any(x => x.Text.ContainsAny(Lang.TravelToInstancedArea)) || m.Text == Lang.ToReduceCongestion)
            )
        {
            // 分線數量優先「數選單裡的分線項目」——這是遊戲實際列出來的清單,
            // 比上游只讀靜態位址 (*S.Memory.MaxInstances) 可靠(上游會偶發 "Instance count is wrong")。
            var inst = CountInstanceEntries(m);
            if(inst < 2 || inst > 9)
            {
                var fallback = *S.Memory.MaxInstances;
                if(fallback >= 2 && fallback <= 9)
                {
                    inst = fallback;
                }
                else
                {
                    if(EzThrottler.Throttle("InstanceWarning", 5000)) PluginLog.Warning($"Instance count is wrong, entries {inst} / static {fallback}, please report to developer");
                    return;
                }
            }

            if(!(C.PublicInstances.TryGetValue(P.Territory, out var value) && value == inst))
            {
                C.PublicInstances[P.Territory] = inst;
                EzConfig.Save();
                PluginLog.Information($"Instance count for territory {P.Territory} initialized: {inst}");
            }
        }
    }

    /// <summary>
    /// 數選單中帶有分線編號字形( ~ )的項目數 = 該區分線總數。
    /// </summary>
    private static int CountInstanceEntries(AddonMaster.SelectString m)
    {
        var count = 0;
        foreach(var e in m.Entries)
        {
            var text = e.Text;
            for(var i = 1; i <= 9; i++)
            {
                if(text.Contains(TaskChangeInstance.InstanceNumbers[i]))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    public int GetInstance()
    {
        return (int)UIState.Instance()->PublicInstance.InstanceId;
    }

    public bool InstancesInitizliaed(out int maxInstances)
    {
        return C.PublicInstances.TryGetValue(P.Territory, out maxInstances);
    }

    /// <summary>
    /// 目前「可安全得知」的分線數:已記錄值,或至少等於自己所在的分線編號
    /// (身處第 N 分線即證明至少存在 N 個,不需要任何額外記憶體讀取)。
    /// 回傳 0 表示連自己的分線都讀不到(不在分線區)。
    /// </summary>
    public int GetKnownInstanceCount()
    {
        var current = GetInstance();
        C.PublicInstances.TryGetValue(P.Territory, out var known);
        return Math.Max(known, current);
    }

    /// <summary>
    /// 分線數是否已由選單清單確認過(而非僅由所在分線推得的下限)。
    /// </summary>
    public bool IsInstanceCountConfirmed() => C.PublicInstances.ContainsKey(P.Territory);

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "SelectString", OnPostUpdate);
    }
}
