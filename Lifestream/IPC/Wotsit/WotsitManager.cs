using Dalamud.Plugin.Ipc;
using ECommons.Configuration;
using ECommons.Events;
using ECommons.GameHelpers;

namespace Lifestream.IPC;

public class WotsitManager : IDisposable
{
    // Map from registered Guid string to the entry.
    private readonly Dictionary<string, WotsitEntry> _registered = [];
    private HashSet<WotsitEntry> _lastEntries = [];

    private readonly ICallGateSubscriber<bool> _faAvailable;
    private readonly ICallGateSubscriber<string, bool> _faInvoke;
    private ICallGateSubscriber<string, string, string, uint, string>? _faRegisterWithSearch;

    private WotsitManager()
    {
        _faAvailable = Svc.PluginInterface.GetIpcSubscriber<bool>("FA.Available");
        _faAvailable.Subscribe(OnAvailable);
        _faInvoke = Svc.PluginInterface.GetIpcSubscriber<string, bool>("FA.Invoke");
        _faInvoke.Subscribe(HandleInvoke);
        // To handle (re)logins (and plugin reloads).
        ProperOnLogin.RegisterAvailable(OnLogin, true);
        // To handle logouts and clear all entries.
        Svc.ClientState.Logout += OnLogout;
        // To catch new config options, address book entries, aliases, etc.
        EzConfig.OnSave += ConfigSaved;
        // To periodically reload generated entries.
        Svc.ClientState.TerritoryChanged += TerritoryChanged;
    }

    public void Dispose()
    {
        // 退訂放在 ClearWotsit 之前：它呼叫 Wotsit 的 IPC 可能擲例外，不能讓訂閱因此留在 CallGate 上。
        _faAvailable?.Unsubscribe(OnAvailable);
        _faInvoke?.Unsubscribe(HandleInvoke);
        ProperOnLogin.Unregister(OnLogin);
        Svc.ClientState.Logout -= OnLogout;
        EzConfig.OnSave -= ConfigSaved;
        Svc.ClientState.TerritoryChanged -= TerritoryChanged;
        ClearWotsit();
        GC.SuppressFinalize(this);
    }

    private void HandleInvoke(string id)
    {
        if(!_registered.TryGetValue(id, out var entry))
        {
            return;
        }

        PluginLog.Debug($"WotsitManager: Received FA.Invoke(\"{id}\") => {entry.DisplayName}");
        try
        {
            entry.Callback.DynamicInvoke();
        }
        catch(Exception e)
        {
            DuoLog.Error($"WotsitManager: Could not handle FA.Invoke(\"{id}\") ({entry.DisplayName}): {e}");
        }
    }

    public void TryClearWotsit()
    {
        try
        {
            ClearWotsit();
        }
        catch(Exception e)
        {
            PluginLog.Warning($"WotsitManager: Failed to clear wotsit: {e}");
        }
    }

    private void ClearWotsit()
    {
        _lastEntries = [];
        if(Utils.WotsitInstalled())
        {
            var faUnregisterAll = Svc.PluginInterface.GetIpcSubscriber<string, bool>("FA.UnregisterAll");
            faUnregisterAll!.InvokeFunc(P.Name);
            PluginLog.Debug($"WotsitManager: Invoked FA.UnregisterAll(\"{P.Name}\")");
        }
        _registered.Clear();
    }

    private void OnAvailable()
    {
        MaybeTryInit(true, "FA.Available triggered, forcing re-registration");
    }

    private void OnLogin()
    {
        MaybeTryInit(true, "ProperOnLogin.Available triggered, forcing re-registration");
    }

    private void OnLogout(int type, int code)
    {
        // 沒裝 Wotsit 時登出照樣觸發:這一行要跟著 ClearWotsit 裡的閘門走,否則每次登出都寫一行。
        if(Utils.WotsitInstalled())
        {
            PluginLog.Debug("WotsitManager: ClientState.Logout triggered, clearing wotsit");
        }
        TryClearWotsit();
    }

    private void ConfigSaved()
    {
        if(!Player.Available)
        {
            return;
        }
        MaybeTryInit(false, "Config saved, attempting re-registration");
    }

    private void TerritoryChanged(ushort territory)
    {
        MaybeTryInit(false, $"Territory changed to {territory}, attempting re-registration");
    }

    /// <summary>沒裝 Wotsit、或整合關著，就什麼都不做。</summary>
    /// <remarks>
    /// <paramref name="reason"/> 非空時在<b>閘門之後</b>寫一行 Debug。換區／登入／存設定這些
    /// 事件在沒裝 Wotsit 的機器上照樣每次觸發,寫在閘門之前等於每次換區都留一行。
    /// </remarks>
    public void MaybeTryInit(bool force = false, string reason = "")
    {
        if(!C.WotsitIntegrationEnabled || !Utils.WotsitInstalled())
        {
            return;
        }

        if(!string.IsNullOrEmpty(reason))
        {
            PluginLog.Debug($"WotsitManager: {reason}");
        }

        try
        {
            Init(force);
        }
        catch(Exception e)
        {
            PluginLog.Verbose($"WotsitManager: Failed to initialize: {e.ToStringFull()}");
        }
    }

    private void Init(bool force = false)
    {
        var newEntries = WotsitEntryGenerator.Generate().ToHashSet();
        if(!force && _lastEntries.Count != 0 && newEntries.SetEquals(_lastEntries))
        {
            PluginLog.Debug("WotsitManager: Entries have not changed, skipping re-registration");
            return;
        }
#if DEBUG
        // Log the actual differences for debugging.
        if (_lastEntries.Count > 0)
        {
            foreach (var added in newEntries.Except(_lastEntries).ToArray())
            {
                PluginLog.Debug($"WotsitManager: New entry: {added}");
            }
            foreach (var removed in _lastEntries.Except(newEntries).ToArray())
            {
                PluginLog.Debug($"WotsitManager: Removed entry: {removed}");
            }
        }
#endif
        ClearWotsit();
        _lastEntries = newEntries;

        _faRegisterWithSearch = Svc.PluginInterface.GetIpcSubscriber<string, string, string, uint, string>("FA.RegisterWithSearch");

        foreach(var entry in newEntries)
        {
            var id = _faRegisterWithSearch!.InvokeFunc(P.Name, entry.DisplayName, $"{P.Name} {entry.SearchString}", entry.IconId);
            _registered.Add(id, entry);
            PluginLog.Debug($"WotsitManager: Invoked FA.RegisterWithSearch(\"{P.Name}\", \"{entry.DisplayName}\", \"{entry.SearchString}\", {entry.IconId}) => {id}");
        }
    }
}
