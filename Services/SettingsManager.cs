using System.IO;
using System.Text.Json;
using Serilog;

namespace MicShift;

public class AppSettings
{
    public string DeskMicrophoneName { get; set; } = string.Empty;
    public string HeadsetMicrophoneName { get; set; } = string.Empty;
    public bool AutoSwitchEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool IsDarkMode { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MicShift",
        "settings.json"
    );

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaultSettings = new AppSettings();
                Save(defaultSettings);
                return defaultSettings;
            }

            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load application settings. Using defaults.");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsPath, json);
            Log.Debug("Application settings saved successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save application settings to {Path}", SettingsPath);
        }
    }
}
