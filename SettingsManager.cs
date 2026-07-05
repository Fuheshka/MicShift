using System.IO;
using System.Text.Json;
using Serilog;

namespace MicShift;

public class AppSettings
{
    public string DeskMicrophoneName { get; set; } = string.Empty;
    public string HeadsetMicrophoneName { get; set; } = string.Empty;
    public bool AutoSwitchEnabled { get; set; } = true;
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
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load app settings.");
        }
        return new AppSettings();
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
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            Log.Information("App settings saved successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save app settings.");
        }
    }
}
