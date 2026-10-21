using System.Diagnostics;

namespace GrowTool77.Modules;

public record GameStatus(bool Running, int? Pid);
public record LaunchResult(bool Success, string Message);

public static class GrowtopiaManager
{
    private static readonly string[] KnownPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Growtopia", "Growtopia.exe"),
        @"C:\Program Files (x86)\Growtopia\Growtopia.exe",
        @"C:\Program Files\Growtopia\Growtopia.exe",
    };

    public static GameStatus GetStatus()
    {
        var procs = Process.GetProcessesByName("Growtopia");
        try
        {
            return procs.Length > 0 ? new GameStatus(true, procs[0].Id) : new GameStatus(false, null);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static string? FindExePath() => KnownPaths.FirstOrDefault(File.Exists);

    public static LaunchResult Launch(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? FindExePath() : path.Trim();
        if (path == null || !File.Exists(path))
            return new LaunchResult(false, "Growtopia.exe was not found. Use Browse to select it.");

        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            Process.Start(info)?.Dispose();
            return new LaunchResult(true, "Growtopia launched.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ex.Message);
        }
    }
}
