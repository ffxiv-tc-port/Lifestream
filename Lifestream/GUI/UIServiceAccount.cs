namespace Lifestream.GUI;

internal static class UIServiceAccount
{
    internal static void Draw()
    {
        ImGuiEx.TextWrapped($"若你擁有超過 1 個服務帳號，必須為每個角色指定正確的服務帳號。\n要讓角色出現在此清單中，請先登入該角色。");
        ImGui.Checkbox($"從 AutoRetainer 取得服務帳號資料", ref C.UseAutoRetainerAccounts);
        List<string> ManagedByAR = [];
        if(P.AutoRetainerApi?.Ready == true && C.UseAutoRetainerAccounts)
        {
            var chars = P.AutoRetainerApi.GetRegisteredCharacters();
            foreach(var c in chars)
            {
                var data = P.AutoRetainerApi.GetOfflineCharacterData(c);
                if(data != null)
                {
                    var name = $"{data.Name}@{data.World}";
                    ManagedByAR.Add(name);
                    ImGui.SetNextItemWidth(150f.Scale());
                    if(ImGui.BeginCombo($"{name}", data.ServiceAccount == -1 ? "尚未選擇" : $"服務帳號 {data.ServiceAccount + 1}"))
                    {
                        for(var i = 0; i < 10; i++)
                        {
                            if(ImGui.Selectable($"服務帳號 {i + 1}"))
                            {
                                C.ServiceAccounts[name] = i;
                                data.ServiceAccount = i;
                                P.AutoRetainerApi.WriteOfflineCharacterData(data);
                                Notify.Info($"設定已儲存至 AutoRetainer");
                            }
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.SameLine();
                    ImGuiEx.Text(ImGuiColors.DalamudRed, $"由 AutoRetainer 管理");
                }
            }
        }
        foreach(var x in C.ServiceAccounts)
        {
            if(ManagedByAR.Contains(x.Key)) continue;
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo($"{x.Key}", x.Value == -1 ? "尚未選擇" : $"服務帳號 {x.Value + 1}"))
            {
                for(var i = 0; i < 10; i++)
                {
                    if(ImGui.Selectable($"服務帳號 {i + 1}")) C.ServiceAccounts[x.Key] = i;
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if(ImGui.Button("刪除"))
            {
                new TickScheduler(() => C.ServiceAccounts.Remove(x.Key));
            }
        }
    }
}
