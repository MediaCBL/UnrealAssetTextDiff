using System.IO;
using System.Text.Json;

namespace UnrealAssetTextDiff;

public sealed class AppSettings
{
    public string? ProjectPath { get; set; }
    public string? EngineCmdPath { get; set; }
    public string? TortoiseMergePath { get; set; }

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnrealAssetTextDiff");

    public static string SessionsRoot => Path.Combine(SettingsDir, "Sessions");

    private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (s != null) return s;
            }
        }
        catch { /* corrupted settings are non-fatal */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings persistence is best-effort */ }
    }
}
