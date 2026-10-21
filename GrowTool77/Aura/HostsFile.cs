using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace GrowTool77.Aura;

public enum HostsResult { Ok, Cancelled, Failed }

// While [AURA] runs, www.growtopia1.com and www.growtopia2.com point at
// 127.0.0.1 so the game asks [AURA] where to connect. Only the lines between
// the [AURA] markers are ever added or removed; the rest of the file is kept
// exactly as it was.
public static class HostsFile
{
    public const string ElevatedArg = "--aura-hosts";

    private const string BeginMarker = "# [AURA] begin";
    private const string EndMarker   = "# [AURA] end";
    private const string BeginLine   = BeginMarker + " - added by GrowTool77, removed when [AURA] stops";

    // Growtopia asks for server_data.php on several name forms. The login
    // flow uses growtopia1.com / growtopia2.com WITHOUT "www" on port 80
    // (see GrowtopiaNoobs/GrowDocs), so every form must point at 127.0.0.1 —
    // redirecting only the "www" forms makes the game bypass [AURA] entirely.
    public static readonly string[] Domains =
    {
        "growtopia1.com", "www.growtopia1.com",
        "growtopia2.com", "www.growtopia2.com",
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool HasAuraBlock()
    {
        try { return File.Exists(FilePath) && File.ReadAllText(FilePath).Contains(BeginMarker, StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Another line (outside our block) that already redirects a Growtopia
    // domain, for example left behind by a different proxy tool.
    public static string? ForeignRedirect()
    {
        string[] lines;
        try { lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        bool inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal)) { inBlock = true; continue; }
            if (line.StartsWith(EndMarker, StringComparison.Ordinal))   { inBlock = false; continue; }
            if (inBlock || line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length && parts[i][0] != '#'; i++)
                if (Domains.Any(d => parts[i].Equals(d, StringComparison.OrdinalIgnoreCase)))
                    return line;
        }
        return null;
    }

    // Adds or removes the [AURA] lines, asking Windows for permission (UAC)
    // unless GrowTool77 already runs as administrator.
    public static HostsResult Change(bool enable)
    {
        if (IsElevated()) return Apply(enable ? "enable" : "disable") == 0 ? HostsResult.Ok : HostsResult.Failed;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return HostsResult.Failed;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"{ElevatedArg} {(enable ? "enable" : "disable")}")
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
            if (p == null || !p.WaitForExit(30_000)) return HostsResult.Failed;
            return p.ExitCode == 0 ? HostsResult.Ok : HostsResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // user pressed "No"
        {
            return HostsResult.Cancelled;
        }
        catch (Win32Exception)
        {
            return HostsResult.Failed;
        }
    }

    // Entry point of the short-lived elevated helper ("GrowTool77.exe --aura-hosts enable").
    public static int Apply(string mode)
    {
        if (mode != "enable" && mode != "disable") return 2;
        try
        {
            var path  = FilePath;
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            bool bom  = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text  = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));

            var result = mode == "enable" ? AddBlock(text) : RemoveBlock(text);

            var output = new List<byte>();
            if (bom) output.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
            output.AddRange(Encoding.UTF8.GetBytes(result));
            File.WriteAllBytes(path, output.ToArray());

            FlushDns();
            return 0;
        }
        catch (UnauthorizedAccessException) { return 5; }
        catch (IOException)                 { return 3; }
    }

    internal static string AddBlock(string text)
    {
        var sb = new StringBuilder(RemoveBlock(text).TrimEnd('\r', '\n'));
        if (sb.Length > 0) sb.Append("\r\n");
        sb.Append(BeginLine).Append("\r\n");
        foreach (var d in Domains) sb.Append("127.0.0.1 ").Append(d).Append("\r\n");
        sb.Append(EndMarker).Append("\r\n");
        return sb.ToString();
    }

    internal static string RemoveBlock(string text)
    {
        if (!text.Contains(BeginMarker, StringComparison.Ordinal)) return text;

        var keep    = new List<string>();
        bool inside = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (!inside && t.StartsWith(BeginMarker, StringComparison.Ordinal)) { inside = true; continue; }
            if (inside)
            {
                if (t.StartsWith(EndMarker, StringComparison.Ordinal)) inside = false;
                continue;
            }
            keep.Add(line);
        }
        var joined = string.Join("\r\n", keep).TrimEnd('\r', '\n');
        return joined.Length == 0 ? "" : joined + "\r\n";
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
            });
            p?.WaitForExit(5000);
        }
        catch (Win32Exception)
        {
            // Windows picks up hosts changes on its own as well.
        }
    }
}
