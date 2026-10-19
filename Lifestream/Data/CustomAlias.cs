using NightmareUI.OtterGuiWrapper.FileSystems.Generic;

namespace Lifestream.Data;
[Serializable]
public class CustomAlias : IFileSystemStorage
{
    public string ExportedName;
    public Guid GUID { get; set; } = Guid.NewGuid();
    public string Alias = "";
    public bool Enabled = true;
    public List<CustomAliasCommand> Commands = [];

    public bool ShouldSerializeAlias() => Alias.Length > 0;
    public bool ShouldSerializeEnabled() => Enabled != true;
    public bool ShouldSerializeGUID() => GUID != Guid.Empty;

    public string GetCustomName() => null;
    public void SetCustomName(string s) { }

    /// <param name="force">true＝Lifestream 忙碌中也照樣排入。</param>
    /// <param name="inclusiveStart">第一個要執行的指令索引（0 起算）；null＝從頭。</param>
    /// <param name="exclusiveEnd">執行到這個索引之前為止；null＝到最後。超出範圍一律夾回清單內。</param>
    public void Enqueue(bool force = false, int? inclusiveStart = null, int? exclusiveEnd = null)
    {
        if(force || !Utils.IsBusy())
        {
            var start = Math.Clamp(inclusiveStart ?? 0, 0, Commands.Count);
            var end = Math.Clamp(exclusiveEnd ?? Commands.Count, start, Commands.Count);
            var cmds = Commands.GetRange(start, end - start);
            // 只警告不擋：做不到的步驟照舊排入，由它自己失敗或略過（與沒有這項檢查時相同）。
            for(var i = 0; i < cmds.Count; i++)
            {
                if(!cmds[i].CanExecute(out var problem))
                {
                    DuoLog.Warning("Custom alias ??, command ??: ??".Loc(GetDisplayName(), start + i + 1, problem));
                }
            }
            var displayName = GetDisplayName();
            for(var i = 0; i < cmds.Count; i++)
            {
                List<Vector3> append = [];
                var cmd = cmds[i];
                if(cmd.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point, CustomAliasKind.Circular_movement) == true)
                {
                    // 鏈結判斷要用在完整清單裡的索引（start + i），不是截出來的範圍內索引。
                    while(cmds.SafeSelect(i + 1)?.Kind == CustomAliasKind.Move_to_point && this.IsChainedWithNext(start + i))
                    {
                        var c = cmds[i + 1];
                        append.Add(c.PickPoint().Scatter(c.Scatter));
                        i++;
                    }
                }
                cmd.Enqueue(append, displayName);
            }
        }
        else
        {
            Notify.Error("Lifestream is busy!");
        }
    }

    // 使用者在別名清單裡看到的名稱；內建捷徑不在清單裡，退回 /li 別名或匯出名。
    private string GetDisplayName()
    {
        var fs = S.CustomAliasFileSystemManager?.FileSystem;
        if(fs != null && fs.FindLeaf(this, out var leaf)) return leaf.Name;
        return !string.IsNullOrEmpty(Alias) ? Alias : ExportedName ?? "";
    }
}
