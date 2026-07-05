using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace MicShift;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int SW_HIDE = 0;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        string[] args = e.Args;

        // Initialize Logger
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logPath = Path.Combine(appDataPath, "MicShift", "logs", "log.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("MicShift WPF App started.");

        var switcher = new WindowsAudioDeviceSwitcher();
        var appSettings = SettingsManager.Load();

        // Apply saved theme
        ApplyTheme(appSettings.IsDarkMode);

        if (args.Length > 0)
        {
            string command = args[0].ToLowerInvariant();
            
            if (command is "--tray" or "-t")
            {
                Log.Information("App started directly in System Tray.");
                
                // Hide console window (just in case it was opened from cmd)
                var consoleHandle = GetConsoleWindow();
                if (consoleHandle != IntPtr.Zero)
                {
                    ShowWindow(consoleHandle, SW_HIDE);
                }

                var autoSwitch = new AutoSwitchService(switcher, appSettings.DeskMicrophoneName, appSettings.HeadsetMicrophoneName);

                NotificationManager.Initialize(switcher, autoSwitch);
                HotkeyManager.Start(switcher, autoSwitch);

                if (appSettings.AutoSwitchEnabled && !string.IsNullOrEmpty(appSettings.DeskMicrophoneName) && !string.IsNullOrEmpty(appSettings.HeadsetMicrophoneName))
                {
                    autoSwitch.Start();
                }
                else if (string.IsNullOrEmpty(appSettings.DeskMicrophoneName) || string.IsNullOrEmpty(appSettings.HeadsetMicrophoneName))
                {
                    NotificationManager.Show("MicShift Config", "Calibration not completed. Please open settings from tray or run in UI mode.");
                }

                return;
            }
            
            // CLI command execution (attach parent console to print output)
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine();

            if (command is "--list" or "-l")
            {
                RunListCommand(switcher);
            }
            else if (command is "--default" or "-d")
            {
                RunDefaultCommand(switcher);
            }
            else if (command is "--switch" or "-s")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: Please specify microphone name or ID.");
                    Shutdown(1);
                    return;
                }
                string identifier = string.Join(" ", args.Skip(1));
                RunSwitchCommand(switcher, identifier);
            }
            else if (command is "--help" or "-h" or "/?")
            {
                PrintHelp();
            }
            else
            {
                Console.WriteLine($"Error: Unknown argument: {args[0]}");
                PrintHelp();
            }

            Log.Information("MicShift CLI execution completed. Exiting.");
            Log.CloseAndFlush();
            
            Shutdown(0);
            return;
        }

        // GUI mode (launch MainWindow in Views/MainWindow)
        var mainAutoSwitch = new AutoSwitchService(switcher, appSettings.DeskMicrophoneName, appSettings.HeadsetMicrophoneName);

        NotificationManager.Initialize(switcher, mainAutoSwitch);
        HotkeyManager.Start(switcher, mainAutoSwitch);

        if (appSettings.AutoSwitchEnabled && !string.IsNullOrEmpty(appSettings.DeskMicrophoneName) && !string.IsNullOrEmpty(appSettings.HeadsetMicrophoneName))
        {
            mainAutoSwitch.Start();
        }

        var mainWindow = new MainWindow(switcher, mainAutoSwitch);
        mainWindow.Show();
    }

    public static void ApplyTheme(bool isDark)
    {
        var themeUri = new Uri(isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative);
        try
        {
            var dict = new ResourceDictionary { Source = themeUri };
            var resources = Current.Resources;
            resources.MergedDictionaries.Clear();
            resources.MergedDictionaries.Add(dict);
            Log.Debug("Theme applied: {Theme}", isDark ? "Dark" : "Light");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply theme {Theme}", isDark ? "Dark" : "Light");
        }
    }

    // ── CLI Command Handlers ───────────────────────────────────────────────────

    private static void RunListCommand(WindowsAudioDeviceSwitcher switcher)
    {
        try
        {
            var mics = switcher.GetActiveMicrophones();
            if (mics.Count == 0)
            {
                Console.WriteLine("No active microphones found.");
                return;
            }
            foreach (var mic in mics)
            {
                string defaultMarker = mic.IsDefaultCommunications ? " (Default)" : "";
                Console.WriteLine($"{mic.Id} | {mic.Name}{defaultMarker}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to run CLI List command.");
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void RunDefaultCommand(WindowsAudioDeviceSwitcher switcher)
    {
        try
        {
            var mics = switcher.GetActiveMicrophones();
            var def = mics.FirstOrDefault(m => m.IsDefaultCommunications);
            if (def != null)
            {
                Console.WriteLine($"{def.Id} | {def.Name}");
            }
            else
            {
                Console.WriteLine("No default communications microphone found.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void RunSwitchCommand(WindowsAudioDeviceSwitcher switcher, string identifier)
    {
        try
        {
            var mics = switcher.GetActiveMicrophones();
            AudioDeviceInfo? selected = null;

            if (Guid.TryParse(identifier, out Guid id))
            {
                selected = mics.FirstOrDefault(m => m.Id == id);
            }

            if (selected == null)
            {
                selected = mics.FirstOrDefault(m => m.Name.Contains(identifier, StringComparison.OrdinalIgnoreCase));
            }

            if (selected == null)
            {
                Console.WriteLine($"Error: Microphone '{identifier}' not found.");
                return;
            }

            if (selected.IsDefaultCommunications)
            {
                Console.WriteLine($"\"{selected.Name}\" is already the default.");
                return;
            }

            bool success = switcher.SetDefaultCommunicationsMicrophoneAsync(selected.Id).GetAwaiter().GetResult();
            if (success)
            {
                Console.WriteLine($"Successfully switched to '{selected.Name}'.");
            }
            else
            {
                Console.WriteLine($"Error: Could not switch to '{selected.Name}'.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MicShift CLI Arguments Help:");
        Console.WriteLine("  --list, -l                     List all active microphones.");
        Console.WriteLine("  --default, -d                  Show the default communications microphone.");
        Console.WriteLine("  --switch, -s <Name or ID>       Switch the default microphone to the specified one.");
        Console.WriteLine("  --tray, -t                     Run hidden in Windows System Tray (starts hotkeys & auto-distance).");
        Console.WriteLine("  --help, -h                     Show this help message.");
        Console.WriteLine();
        Console.WriteLine("If run without arguments, MicShift starts in graphical settings mode.");
    }
}
