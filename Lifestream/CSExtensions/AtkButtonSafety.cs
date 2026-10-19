using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Lifestream.CSExtensions;

/// <summary>
/// 讀取原生按鈕狀態前的指標守衛。
/// <para>
/// 🔴 <c>AtkComponentButton.IsEnabled</c> 的實作是
/// <c>AtkComponentBase.OwnerNode-&gt;AtkResNode.NodeFlags.HasFlag(...)</c>，
/// 對 <c>OwnerNode</c> 沒有任何 null 檢查 —— <c>OwnerNode</c> 為 null 時會丟出
/// AccessViolationException，而 AVE 在 .NET Core 是 corrupted-state exception，
/// <c>try/catch</c> 完全攔不到。所有讀取 <c>IsEnabled</c> 的地方都必須改走這裡。
/// </para>
/// <para>
/// ⚠️ <c>AtkComponentBase</c> 有兩個指標欄位：<c>AtkResNode</c>(0xA0) 與 <c>OwnerNode</c>(0xA8)。
/// 檢查 <c>AtkResNode</c> 擋不到 <c>IsEnabled</c> 的解參考 —— 那是不同的欄位。
/// </para>
/// </summary>
public static unsafe class AtkButtonSafety
{
    /// <summary>
    /// 按鈕存在、<c>OwnerNode</c> 有效、且處於啟用狀態時回 true；任何一層取不到都回 false
    /// （＝視為「不可按」，由上層照既有邏輯處理）。
    /// </summary>
    public static bool IsButtonEnabled(AtkComponentButton* button)
    {
        return button != null
            && button->AtkComponentBase.OwnerNode != null
            && button->IsEnabled;
    }

    /// <summary>
    /// 從 addon 的 <c>UldManager.NodeList</c> 取出指定索引的按鈕元件，任何一層取不到就回 null。
    /// <para>
    /// 🔴 <c>GetAsAtkComponentButton()</c> 是原生 member function（<c>this</c> 走 RCX），
    /// 對 null 節點呼叫一樣會 AVE，所以節點必須在呼叫前就先驗過。
    /// </para>
    /// </summary>
    public static AtkComponentButton* GetNodeListButton(AtkUnitBase* addon, int index)
    {
        if(addon == null) return null;
        if(addon->UldManager.NodeList == null) return null;
        if(index < 0 || index >= addon->UldManager.NodeListCount) return null;
        var node = addon->UldManager.NodeList[index];
        if(node == null) return null;
        return node->GetAsAtkComponentButton();
    }
}
