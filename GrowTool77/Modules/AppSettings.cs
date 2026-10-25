// lang: C#, file: AppSettings.cs, target: .NET 8 Windows, proj: GrowTool77
// Persistent settings stored in %APPDATA%\GrowTool77\settings.json
using System.Text.Json;

namespace GrowTool77.Modules;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrowTool77", "settings.json");

    // ── Fields saved ─────────────────────────────────────────────────
    public bool   SkipGazette  { get; set; } = true;
    public bool   GhostFinder  { get; set; } = false;
    public string GamePath     { get; set; } = "";

    // ── Load / Save ───────────────────────────────────────────────────
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* save failure is non-critical */ }
    }
}
