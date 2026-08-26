using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Lifestream.CSExtensions;

/// <summary>
/// 讀取原生節點文字前的指標守衛（<see cref="AtkButtonSafety"/> 的文字節點版本）。
/// <para>
/// 🔴 這裡擋的是<b>三種不同的爆法</b>，三種原本都攔不住，其中兩種是靜默的：
/// </para>
/// <list type="number">
/// <item>
/// <b>毒指標交接（最陰的一種）</b>：<c>&amp;node-&gt;NodeText</c> 對 <c>null</c> 節點
/// <b>不會當場崩</b>。<c>NodeText</c> 位在 <c>AtkTextNode</c> 偏移 0xC0，
/// 算出來的毒指標 0xC0 連 <c>GenericHelpers.ReadSeString</c> 內部的 <c>utf8String != null</c>
/// 都騙得過去，一路到 <c>AsSpan()</c> 去讀位址 0xC0 才炸 —— 崩潰現場完全指不到真因。
/// </item>
/// <item>
/// <b><c>[MemberFunction]</c> 對 null this</b>：<c>GetAsAtkTextNode()</c>／
/// <c>GetAsAtkComponentNode()</c> 都是原生 member function（<c>this</c> 走 RCX），
/// 對 <c>null</c> 節點呼叫等於把 <c>this = 0</c> 交給遊戲原生碼＝當場 AccessViolation。
/// AVE 在 .NET Core 是 corrupted-state exception，<c>try/catch</c> 完全攔不到。
/// </item>
/// <item>
/// <b>半套邊界檢查</b>：<c>NodeList</c> 的<b>上界</b>與<b>元素判空</b>是兩件事。
/// 版面還在建（或已開始拆）的時候 <c>NodeListCount</c> 可能小於索引，
/// 越界讀到的是<b>相鄰記憶體而不是 <c>null</c></b> —— 元素判空完全擋不住。
/// </item>
/// </list>
/// <para>
/// ⚠️ <c>node-&gt;NodeText.GetText()</c> 這種「先取值再處理」的寫法是第 1 條的另一面：
/// <c>GetText(this Utf8String)</c> 的參數是<b>傳值</b>的，複製動作發生在<b>呼叫端</b>，
/// 等於直接去讀位址 0xC0 起頭的 0x68 位元組，炸在那一行 —— 不是在 <c>GetText</c> 裡面。
/// </para>
/// </summary>
public static unsafe class AtkNodeSafety
{
    /// <summary>
    /// 從 <paramref name="uld"/> 的 <c>NodeList</c> 取第 <paramref name="index"/> 個節點；
    /// 上界與元素判空都做，任何一層取不到就回 <see langword="null"/>。
    /// </summary>
    public static AtkResNode* GetNodeSafe(AtkUldManager* uld, int index)
    {
        if(uld == null || uld->NodeList == null) return null;
        if(index < 0 || index >= uld->NodeListCount) return null;
        return uld->NodeList[index];
    }

    /// <summary>
    /// 取 <paramref name="node"/> 底下元件的 <c>NodeList</c> 第 <paramref name="index"/> 個節點；
    /// 節點、元件、上界、元素任何一層取不到就回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <c>AtkComponentNode.Component</c> 是偏移 0xB0 的<b>指標欄位</b>，
    /// 元件尚未 setup 完成或已開始拆解時是 <c>null</c>，所以它自己也要判。
    /// </remarks>
    public static AtkResNode* GetComponentNodeSafe(AtkResNode* node, int index)
    {
        if(node == null) return null;
        var componentNode = node->GetAsAtkComponentNode();
        if(componentNode == null || componentNode->Component == null) return null;
        return GetNodeSafe(&componentNode->Component->UldManager, index);
    }

    /// <inheritdoc cref="GetComponentNodeSafe(AtkResNode*, int)"/>
    public static AtkResNode* GetComponentNodeSafe(AtkUnitBase* addon, int nodeIndex, int index)
        => GetComponentNodeSafe(GetNodeSafe(addon == null ? null : &addon->UldManager, nodeIndex), index);

    /// <summary>
    /// 安全地取得文字節點本身；取不到回 <see langword="null"/>。
    /// </summary>
    public static AtkTextNode* GetTextNodeSafe(AtkResNode* node)
        => node == null ? null : node->GetAsAtkTextNode();

    /// <summary>
    /// 安全地讀取一個節點的文字內容。取得到回 <see langword="true"/>；
    /// 鏈上任何一節取不到就回 <see langword="false"/>，<paramref name="text"/> 為空字串。
    /// </summary>
    /// <remarks>
    /// 🔑 回傳 <c>bool</c> 而不是「取不到就回空字串」，是為了讓呼叫端分得出
    /// 「讀到空文字」與「根本沒讀到」—— 這兩者的意義完全不同：
    /// 這個 repo 裡好幾個迴圈把「空字串」當成「清單到底了」的結束條件，
    /// 混在一起會讓一次讀取失敗被誤判成「後面沒有目的地了」。
    /// </remarks>
    public static bool TryGetNodeText(AtkResNode* node, out string text)
    {
        text = "";
        var textNode = GetTextNodeSafe(node);
        if(textNode == null) return false;
        text = GenericHelpers.ReadSeString(&textNode->NodeText).GetText();
        return true;
    }

    /// <inheritdoc cref="TryGetNodeText(AtkResNode*, out string)"/>
    public static bool TryGetNodeText(AtkUnitBase* addon, int index, out string text)
        => TryGetNodeText(GetNodeSafe(addon == null ? null : &addon->UldManager, index), out text);
}
