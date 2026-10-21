using System.Net;
using System.Net.Sockets;

namespace GrowTool77.Aura;

// Starts and stops [AURA]->proxy in a safe order:
//   1. look up the real login server (before anything is redirected)
//   2. start the relay and the local login endpoint
//   3. only then point the Growtopia domains at 127.0.0.1
// Stopping always removes the hosts lines again.
public sealed class AuraService : IDisposable
{
    private LoginRelay? _relay;

    public AuraLog   Log   { get; } = new();
    public AuraProxy Proxy { get; }

    public bool IsRunning   => Proxy.IsRunning;
    public bool HostsActive { get; private set; }

    public event Action<AuraStatus>? StatusChanged;
    public event Action<WorldView?>? WorldChanged;

    public AuraService()
    {
        Proxy = new AuraProxy(Log);
        Proxy.StatusChanged += s => StatusChanged?.Invoke(s);
        Proxy.WorldChanged  += w => WorldChanged?.Invoke(w);
    }

    // True when a previous session ended without restoring the hosts file.
    public static bool HasLeftoverRedirect() => HostsFile.HasAuraBlock();

    // Returns null on success, otherwise a message for the user.
    public async Task<string?> StartAsync(string? gameExePath)
    {
        if (IsRunning) return null;
        Log.Info("Starting [AURA]->proxy…");

        try
        {
            AuraNative.EnsureInitialized();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                      or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Error($"aura_enet.dll could not be loaded: {ex.Message}");
            return "aura_enet.dll could not be loaded. Make sure it sits next to GrowTool77.exe.";
        }

        if (HostsFile.HasAuraBlock())
        {
            Log.Warn("Old [AURA] lines found in the hosts file; removing them first.");
            if (HostsFile.Change(enable: false) != HostsResult.Ok)
                return "Old [AURA] lines in the hosts file could not be removed.";
        }

        var foreign = HostsFile.ForeignRedirect();
        if (foreign != null)
        {
            Log.Error($"The hosts file already redirects Growtopia: {foreign}");
            return $"Your hosts file already redirects Growtopia (\"{foreign}\"), probably from another proxy. Remove that line and try again.";
        }

        var upstream = await ResolveLoginServersAsync();
        if (upstream.Count == 0)
            return "The Growtopia login server could not be found. Check your internet connection.";

        var items = await Task.Run(() => ItemDatabase.TryLoadFromGame(gameExePath));
        if (items != null)
        {
            Proxy.SetItemDatabase(items);
            Log.Info($"Item names loaded from the game's cache ({items.Count} items, v{items.Version}).");
        }
        else
        {
            Log.Warn("items.dat was not found in the game's cache; item names appear when the server sends them.");
        }

        try
        {
            Proxy.Start();
            _relay = new LoginRelay(Log, upstream, () => Proxy.Port, Proxy.SetServerTarget);
            _relay.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException)
        {
            StopRelays();
            Log.Error(ex.Message);
            return ex.Message;
        }

        var result = await Task.Run(() => HostsFile.Change(enable: true));
        if (result != HostsResult.Ok)
        {
            StopRelays();
            Log.Warn("The hosts file was not changed; [AURA] stopped.");
            return result == HostsResult.Cancelled
                ? "[AURA] needs administrator permission to route Growtopia through it. Nothing was changed."
                : "The hosts file could not be changed. Nothing was changed.";
        }

        HostsActive = true;
        Log.Info("Growtopia is routed through [AURA]. Start Growtopia now (restart it if it is already open).");
        return null;
    }

    // Returns null on success, otherwise a message for the user.
    public string? Stop()
    {
        bool wasRunning = IsRunning;
        StopRelays();

        if (HostsActive || HostsFile.HasAuraBlock())
        {
            if (HostsFile.Change(enable: false) != HostsResult.Ok)
            {
                Log.Error("The hosts file still points Growtopia at [AURA].");
                return "Growtopia is still routed to [AURA]. Press \"Restore\" to fix it, or Growtopia can't connect.";
            }
            HostsActive = false;
        }

        if (wasRunning) Log.Info("[AURA] stopped. Growtopia connects normally again.");
        return null;
    }

    // Removes leftover hosts lines without starting anything.
    public string? Restore()
    {
        if (HostsFile.Change(enable: false) != HostsResult.Ok)
            return "The hosts file could not be restored.";
        HostsActive = false;
        Log.Info("Hosts file restored. Growtopia connects normally again.");
        return null;
    }

    public void Dispose() => Stop();

    private void StopRelays()
    {
        _relay?.Dispose();
        _relay = null;
        Proxy.Stop();
    }

    private async Task<Dictionary<string, IPAddress>> ResolveLoginServersAsync()
    {
        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in HostsFile.Domains)
        {
            try
            {
                var ip = (await Dns.GetHostAddressesAsync(domain))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (ip != null)
                {
                    map[domain] = ip;
                    Log.Info($"{domain} = {ip}");
                }
            }
            catch (SocketException)
            {
                Log.Warn($"{domain} could not be looked up.");
            }
        }
        return map;
    }
}
