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

    public event Action<AuraStatus>? StatusChanged;
    public event Action<WorldView?>? WorldChanged;
    public event Action<string>?     LoginStep;

    // Latest login step reported by the login endpoint (shown on the Main tab).
    public string? LastLoginStep { get; private set; }

    public AuraService()
    {
        Proxy = new AuraProxy(Log);
        Proxy.StatusChanged += s => StatusChanged?.Invoke(s);
        Proxy.WorldChanged  += w => WorldChanged?.Invoke(w);
    }

    // True when a previous session ended without restoring the hosts file.
    public static bool HasLeftoverRedirect() => HostsFile.HasAuraBlock();

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
            _relay.StepChanged += step =>
            {
                LastLoginStep = step;
                LoginStep?.Invoke(step);
            };
            _relay.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or System.Security.Cryptography.CryptographicException)
        {
            StopRelays();
            Log.Error(ex.Message);
            return new StartResult(ex.Message, null);
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
        if (!local.Ok)
        {
            Log.Error($"Check 2/2 failed: {local.Message}.");
            StopRelays();
            return new StartResult(
                "[AURA] stopped before changing anything.\n\nThe local secure login endpoint does not work on this PC:\n" + local.Message, null);
        }
        Log.Info($"Check 2/2 ok: {local.Message}.");

        var result = await Task.Run(() => HostsFile.Change(enable: true));
        if (result != HostsResult.Ok)
        {
            StopRelays();
            Log.Warn("The hosts file was not changed; [AURA] stopped.");
            return new StartResult(result == HostsResult.Cancelled
                ? "[AURA] needs administrator permission to route Growtopia through it. Nothing was changed."
                : "The hosts file could not be changed. Nothing was changed.", null);
        }

        HostsActive = true;
        Log.Info("Growtopia is routed through [AURA]. Start Growtopia now (restart it if it is already open).");
        return new StartResult(null, server.Ok ? null :
            "[AURA] is on, but the check of the real login server failed:\n" + server.Message +
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
        LastLoginStep = null;
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
