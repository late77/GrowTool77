using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace GrowTool77.Aura;

// Starts and stops [AURA]->proxy in a safe order:
//   1. look up the real login server (before anything is redirected)
//   2. start the relay and the local login endpoint
//   3. only then point the Growtopia domains at 127.0.0.1
// Stopping always removes the hosts lines again.
public sealed record StartResult(string? Error, string? Warning)
{
    public static readonly StartResult Ok = new(null, null);
}

public sealed class AuraService : IDisposable
{
    private LoginRelay? _relay;

    public AuraLog   Log   { get; } = new();
    public AuraProxy Proxy { get; }

    public bool IsRunning   => Proxy.IsRunning;
    public bool HostsActive { get; private set; }
    public bool CertActive  { get; private set; }

    public event Action<AuraStatus>? StatusChanged;
    public event Action<WorldView?>? WorldChanged;
    public event Action<string>?     LoginStep;
    public event Action<IReadOnlyList<WorldVends>>? VendsChanged;

    private ItemDatabase? _items;

    // The game's own items.dat (read-only), loaded once and shared by the UI.
    public ItemDatabase? EnsureItems(string? gameExePath)
    {
        _items ??= ItemDatabase.TryLoadFromGame(gameExePath);
        return _items;
    }

    // Latest login step reported by the login endpoint (shown on the Main tab).
    public string? LastLoginStep { get; private set; }

    public AuraService()
    {
        Proxy = new AuraProxy(Log);
        Proxy.StatusChanged += s => StatusChanged?.Invoke(s);
        Proxy.WorldChanged  += w => WorldChanged?.Invoke(w);
        Proxy.VendsChanged  += v => VendsChanged?.Invoke(v);
    }

    // True when a previous session ended without restoring the hosts file.
    public static bool HasLeftoverRedirect() => HostsFile.HasAuraBlock() || CertTrust.HasLeftover();

    public async Task<StartResult> StartAsync(string? gameExePath)
    {
        if (IsRunning) return StartResult.Ok;
        LastLoginStep = null;
        Log.Info("Starting [AURA]->proxy…");

        try
        {
            AuraNative.EnsureInitialized();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                      or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Error($"aura_enet.dll could not be loaded: {ex.Message}");
            return new StartResult("aura_enet.dll could not be loaded. Make sure it sits next to GrowTool77.exe.", null);
        }

        if (HostsFile.HasAuraBlock())
        {
            Log.Warn("Old [AURA] lines found in the hosts file; removing them first.");
            if (HostsFile.Change(enable: false) != HostsResult.Ok)
                return new StartResult("Old [AURA] lines in the hosts file could not be removed.", null);
        }

        var foreign = HostsFile.ForeignRedirect();
        if (foreign != null)
        {
            Log.Error($"The hosts file already redirects Growtopia: {foreign}");
            return new StartResult($"Your hosts file already redirects Growtopia (\"{foreign}\"), probably from another proxy. Remove that line and try again.", null);
        }

        var upstream = await ResolveLoginServersAsync();
        if (upstream.Count == 0)
            return new StartResult("The Growtopia login server could not be found. Check your internet connection.", null);

        var items = await Task.Run(() => EnsureItems(gameExePath));
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
            _relay.StepChanged += step =>
            {
                LastLoginStep = step;
                LoginStep?.Invoke(step);
            };
            _relay.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or System.Security.Cryptography.CryptographicException
                                      or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            StopRelays();
            var message = $"[AURA] could not start its login server: {ex.Message} ({ex.GetType().Name})";
            Log.Error(message);
            return new StartResult(message, null);
        }

        // Check 1: does the real login server answer this PC?
        var version = DetectGameVersion(gameExePath);
        Log.Info($"Check 1/2: asking the real login server (game version {version})…");
        var server = await _relay!.PrefetchAsync(version, "225");
        if (server.Ok) Log.Info($"Check 1/2 ok: {server.Message}.");
        else           Log.Warn($"Check 1/2 failed: {server.Message}.");

        // Check 2: does the local secure endpoint work on this PC?
        Log.Info("Check 2/2: testing the local secure login endpoint…");
        var local = await _relay!.SelfTestAsync();
        if (local.Ok) Log.Info($"Check 2/2 ok: {local.Message}.");
        else          Log.Warn($"Check 2/2 failed: {local.Message}. Continuing; the game's own connection decides.");

        // Make the game trust [AURA]'s login certificate (otherwise it rejects
        // it with "unknown ca" and never sends its login request).
        var pem = _relay!.CertificatePem;
        if (!string.IsNullOrEmpty(pem))
        {
            var (certResult, thumb) = await Task.Run(() => CertTrust.Install(pem));
            if (certResult == CertResult.Ok)
            {
                CertActive = true;
                Log.Info($"Installed [AURA]'s login certificate as trusted ({thumb}).");
            }
            else
            {
                StopRelays();
                Log.Error("The login certificate could not be trusted; Growtopia would reject it.");
                return new StartResult(certResult == CertResult.Cancelled
                    ? "[AURA] needs you to allow its security certificate (Windows shows a one-time prompt). Nothing was changed."
                    : "[AURA]'s security certificate could not be installed. Nothing was changed.", null);
            }
        }

        var result = await Task.Run(() => HostsFile.Change(enable: true));
        if (result != HostsResult.Ok)
        {
            StopRelays();
            if (CertActive) { CertTrust.Remove(); CertActive = false; }
            Log.Warn("The hosts file was not changed; [AURA] stopped.");
            return new StartResult(result == HostsResult.Cancelled
                ? "[AURA] needs administrator permission to route Growtopia through it. Nothing was changed."
                : "The hosts file could not be changed. Nothing was changed.", null);
        }

        HostsActive = true;
        await VerifyHostsEffectiveAsync();
        Log.Info("Growtopia is routed through [AURA]. Start Growtopia now (restart it if it is already open).");
        var problems = new List<string>();
        if (!server.Ok) problems.Add("Real login server: " + server.Message);
        if (!local.Ok)  problems.Add("Local secure endpoint: " + local.Message);
        return new StartResult(null, problems.Count == 0 ? null :
            "[AURA] is on, but a check failed:\n\n" + string.Join("\n", problems) +
            "\n\nGrowtopia may not be able to log in. The [AURA] tab shows the details.");
    }

    // Growtopia's version as the game reports it ("5.39"), from Growtopia.exe.
    private static string DetectGameVersion(string? gameExePath)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(gameExePath) && File.Exists(gameExePath)
                ? gameExePath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Growtopia", "Growtopia.exe");
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var m = Regex.Match(info.ProductVersion ?? "", @"^\s*(\d+)\.(\d{2,})");
                if (m.Success) return $"{m.Groups[1].Value}.{m.Groups[2].Value}";
                if (info.FileMajorPart > 0)
                    return info.FileMinorPart < 10 && info.FileBuildPart > 0
                        ? $"{info.FileMajorPart}.{info.FileMinorPart}{info.FileBuildPart}"
                        : $"{info.FileMajorPart}.{info.FileMinorPart:00}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // fall through to the default
        }
        return "5.39";
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

        if (CertActive || CertTrust.HasLeftover())
        {
            CertTrust.Remove();
            CertActive = false;
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
        CertTrust.Remove();
        CertActive = false;
        Log.Info("Hosts file and certificate restored. Growtopia connects normally again.");
        return null;
    }

    public void Dispose() => Stop();

    private void StopRelays()
    {
        LastLoginStep = null;
        _relay?.Dispose();
        _relay = null;
        Proxy.Stop();
    }

    // Looks up Growtopia's login servers. DNS-over-HTTPS first (so a provider
    // that answers these names with a wrong local address can't misdirect us,
    // which is the usual cause of the "Check your internet / VPN" error), then
    // the machine's own resolver only as a last resort.
    // Confirms the OS now sends the login names to 127.0.0.1. The game uses the
    // system resolver (not DoH), so if this still shows a public address the
    // hosts redirect isn't taking effect and the game will bypass [AURA].
    private async Task VerifyHostsEffectiveAsync()
    {
        foreach (var name in new[] { "growtopia1.com", "growtopia2.com" })
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync(name);
                var v4  = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null)
                {
                    Log.Info($"Redirect check: {name} has no IPv4 result.");
                }
                else if (IPAddress.IsLoopback(v4))
                {
                    Log.Info($"Redirect check ok: {name} now points to {v4} (this PC).");
                }
                else
                {
                    Log.Warn($"Redirect check: {name} still resolves to {v4}, not 127.0.0.1. " +
                             "Windows may be caching DNS or using a private resolver, so Growtopia could bypass [AURA]. " +
                             "Try: close Growtopia, run 'ipconfig /flushdns' as admin, then start Growtopia again.");
                }
            }
            catch (SocketException)
            {
                Log.Info($"Redirect check: {name} could not be resolved locally (this can be normal).");
            }
        }
    }

    private async Task<Dictionary<string, IPAddress>> ResolveLoginServersAsync()
    {
        var doh = new DohResolver(Log);
        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        foreach (var domain in HostsFile.Domains)
        {
            IPAddress? ip = null;
            string how = "";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                ip = await doh.ResolveAsync(domain, cts.Token);
                if (ip != null) how = "via secure DNS";
            }
            catch (OperationCanceledException)
            {
                // fall through to the system resolver
            }

            if (ip == null)
            {
                try
                {
                    ip = (await Dns.GetHostAddressesAsync(domain))
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                    if (ip != null) how = "via this PC's DNS";
                }
                catch (SocketException) { }
            }

            if (ip != null)
            {
                map[domain] = ip;
                Log.Info($"{domain} = {ip} ({how})");
            }
            else
            {
                Log.Warn($"{domain} could not be looked up.");
            }
        }

        if (LooksLikePrivateOrLocal(map.Values))
            Log.Warn("Some login addresses look local. Secure DNS should have avoided this; check the [AURA] tab if login fails.");

        return map;
    }

    // Growtopia's real servers are public AWS addresses; a private/local range
    // here means a resolver returned something wrong.
    private static bool LooksLikePrivateOrLocal(IEnumerable<IPAddress> ips)
    {
        foreach (var ip in ips)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) continue;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
        }
        return false;
    }
}
