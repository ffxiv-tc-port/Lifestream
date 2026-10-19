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
    // 已印過的 (territory, MaxInstances 讀值) 組合,避免每次開分線選單重複洗版(每個新觀察只印一次)。
    private readonly HashSet<long> _loggedMaxInstances = [];
    // 本次啟動內已從分線清單數出分線數的區域(僅框架執行緒存取);之後主選單那側的特徵碼備援不再覆寫它們。
    private readonly HashSet<uint> _countedFromList = [];

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
            && (m.Entries.Any(x => Lang.TravelToInstancedArea.Matches(x.Text, LegacyMatch.ContainsAny)) || Lang.ToReduceCongestion.MatchesSheet(m.Text))
            )
        {
            // 分線數量優先「數選單裡的分線項目」——這是遊戲實際列出來的清單,
            // 比上游只讀靜態位址 (*S.Memory.MaxInstances) 可靠(上游會偶發 "Instance count is wrong")。
            var inst = CountInstanceEntries(m);

            // [MaxInstances 語意驗證診斷] 候選 sig 的執行期語意存疑,靠實機自證:欄位非 null 時讀同一個 sig 解析位址,
            // 與可靠的選單計數 inst 對照後印一行 Information(要使用者回報的診斷):落在 1~9 且與選單計數相符 => 語意正確;
            // 天文數字 / 0 / 與選單計數明顯不符 => 語意錯,保留判空 fallback。只讀 sig 解析出的那一個位址、不碰任何相鄰位址。
            // fail-closed:欄位為 null(sig 未命中)時整段跳過。節流:每個 (territory, 讀到值) 組合只印一次。
            if(S.Memory.MaxInstances != null)
            {
                var maxRaw = *S.Memory.MaxInstances;
                var key = ((long)P.Territory << 32) | (uint)maxRaw;
                if(_loggedMaxInstances.Add(key))
                    PluginLog.Information($"[MaxInstances驗證] territory={P.Territory} sig命中 讀到值={maxRaw} 選單計數={inst} (預期兩者相符且落 1~9)");
            }

            if(inst >= 2 && inst <= 9)
            {
                if(_countedFromList.Add(P.Territory))
                {
                    var sig = S.Memory.MaxInstances == null ? "未解析" : (*S.Memory.MaxInstances).ToString();
                    PluginLog.Information($"[分線數] territory={P.Territory} 由分線清單數出 {inst} 條,本次啟動以清單為準 (特徵碼讀值={sig})");
                }
            }
            else if(_countedFromList.Contains(P.Territory))
            {
                // 主選單沒有分線項目(數到 0);這一區已由清單數過,不拿特徵碼覆寫,也不報「數量不對」。
                return;
            }

            if(inst < 2 || inst > 9)
            {
                // MaxInstances 是 Fallibility.Fallible 的 StaticAddress sig,掃不到時欄位停在 null,裸解參考會擲例外。
                // 沒有可用的靜態 fallback 時,走與「fallback 落在合理值域外」完全相同的路徑:節流警告後放棄本輪。
                // 🔑 這個守衛只作用在「主來源 CountInstanceEntries(選單計數)已經數不出合理值」的 fallback 分支。
                if(S.Memory.MaxInstances == null)
                {
                    if(EzThrottler.Throttle("InstanceWarning", 5000)) PluginLog.Warning($"Instance count is wrong, entries {inst} / static unavailable (MaxInstances 特徵碼未解析), please report to developer");
                    return;
                }
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
