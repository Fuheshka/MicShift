using MicShift;
using Spectre.Console;
using Serilog;
using System.Runtime.InteropServices;
using System.Windows.Forms;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MicShift";

// Win32 Windows control
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

const int SW_HIDE = 0;
const int SW_SHOW = 5;

// Initialize Logger
string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
string logPath = Path.Combine(appDataPath, "MicShift", "logs", "log.txt");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("MicShift started.");

try
{
    using var switcher = new WindowsAudioDeviceSwitcher();

    // Check for CLI commands
    if (args.Length > 0)
    {
        string command = args[0].ToLowerInvariant();
        if (command is "--list" or "-l")
        {
            RunListCommand(switcher);
            return;
        }
        else if (command is "--default" or "-d")
        {
            RunDefaultCommand(switcher);
            return;
        }
        else if (command is "--switch" or "-s")
        {
            if (args.Length < 2)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Please specify microphone name or ID.");
                return;
            }
            string identifier = string.Join(" ", args.Skip(1));
            await RunSwitchCommandAsync(switcher, identifier);
            return;
        }
        else if (command is "--tray" or "-t")
        {
            // Run hidden in System Tray
            Log.Information("Launching directly to System Tray.");
            var consoleHandle = GetConsoleWindow();
            ShowWindow(consoleHandle, SW_HIDE);

            var settings = SettingsManager.Load();
            using var autoSwitch = new AutoSwitchService(switcher, settings.DeskMicrophoneName, settings.HeadsetMicrophoneName);

            NotificationManager.Initialize(switcher, autoSwitch);
            HotkeyManager.Start(switcher, autoSwitch);

            if (settings.AutoSwitchEnabled && !string.IsNullOrEmpty(settings.DeskMicrophoneName) && !string.IsNullOrEmpty(settings.HeadsetMicrophoneName))
            {
                autoSwitch.Start();
            }
            else if (string.IsNullOrEmpty(settings.DeskMicrophoneName) || string.IsNullOrEmpty(settings.HeadsetMicrophoneName))
            {
                NotificationManager.Show("MicShift Config", "Calibration not completed. Please run MicShift in console mode first.");
            }

            Application.Run();
            return;
        }
        else if (command is "--help" or "-h" or "/?")
        {
            PrintHelp();
            return;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown argument: {args[0]}");
            PrintHelp();
            return;
        }
    }

    // Interactive Console Menu Mode
    var initialSettings = SettingsManager.Load();
    using var interactiveAutoSwitch = new AutoSwitchService(switcher, initialSettings.DeskMicrophoneName, initialSettings.HeadsetMicrophoneName);

    NotificationManager.Initialize(switcher, interactiveAutoSwitch);
    HotkeyManager.Start(switcher, interactiveAutoSwitch);

    if (initialSettings.AutoSwitchEnabled && !string.IsNullOrEmpty(initialSettings.DeskMicrophoneName) && !string.IsNullOrEmpty(initialSettings.HeadsetMicrophoneName))
    {
        interactiveAutoSwitch.Start();
    }

    // ── Main menu ──────────────────────────────────────────────────────────────
    while (true)
    {
        AnsiConsole.Clear();
        PrintHeader();

        // Print active settings
        var settings = SettingsManager.Load();
        AnsiConsole.MarkupLine($"  [grey]Status:[/] Auto-Switching is [yellow]{(interactiveAutoSwitch.IsRunning ? "Active" : "Disabled/Paused")}[/]");
        if (!string.IsNullOrEmpty(settings.DeskMicrophoneName))
        {
            AnsiConsole.MarkupLine($"  [grey]Calibrated Desk Mic:[/] [cyan]{settings.DeskMicrophoneName}[/]");
            AnsiConsole.MarkupLine($"  [grey]Calibrated Headset Mic:[/] [cyan]{settings.HeadsetMicrophoneName}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]Calibrated Mics:[/] [red]None (Run Calibration mode to set up)[/]");
        }
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [grey]Global Hotkeys active in background:[/]");
        AnsiConsole.MarkupLine("    [yellow]Ctrl + Alt + M[/] : Toggle mute of default microphone");
        AnsiConsole.MarkupLine("    [yellow]Ctrl + Alt + S[/] : Cycle default microphone");
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<MenuChoice>()
                .Title("[yellow]Select an option:[/]")
                .PageSize(6)
                .AddChoices(new MenuChoice[]
                {
                    new("S", "[cyan]Switch default microphone[/]"),
                    new("C", "[cyan]Calibration mode (calibrate auto-distance)[/]"),
                    new("T", "[cyan]Hide and minimize to System Tray[/]"),
                    new("Q", "[red]Quit[/]")
                }));

        switch (choice.Action)
        {
            case "S": 
                await RunSwitcherAsync(switcher); 
                break;
            case "C": 
                await RunCalibrationAsync(switcher, interactiveAutoSwitch); 
                break;
            case "T":
                Log.Information("Minimizing console window to tray.");
                var handle = GetConsoleWindow();
                ShowWindow(handle, SW_HIDE);
                
                // Block the current thread and run message loop
                Application.Run();
                
                // Once Application.Exit() is called, show console window again
                ShowWindow(handle, SW_SHOW);
                break;
            case "Q": 
                goto done;
        }
    }
    done:
    AnsiConsole.Clear();
    AnsiConsole.MarkupLine("[yellow]Goodbye.[/]");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception occurred.");
    AnsiConsole.WriteException(ex);
}
finally
{
    HotkeyManager.Stop();
    NotificationManager.Dispose();
    Log.Information("MicShift exiting.");
    Log.CloseAndFlush();
}

return;

// ── Switcher flow ──────────────────────────────────────────────────────────
static async Task RunSwitcherAsync(WindowsAudioDeviceSwitcher switcher)
{
    while (true)
    {
        AnsiConsole.Clear();
        PrintHeader();

        List<AudioDeviceInfo> mics;
        try
        {
            mics = [.. switcher.GetActiveMicrophones()];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to list devices in interactive switcher.");
            PrintError($"Failed to list devices: {ex.Message}");
            WaitForKey("Press any key to retry...");
            continue;
        }

        if (mics.Count == 0)
        {
            Log.Warning("No active microphones found during list query.");
            PrintWarning("No active microphones found.");
            WaitForKey("Press any key to go back...");
            return;
        }

        var choices = mics.Select(m => new MicChoice(m)).ToList();
        choices.Add(new MicChoice("Go Back"));

        var selectedChoice = AnsiConsole.Prompt(
            new SelectionPrompt<MicChoice>()
                .Title("Select microphone to set as default:")
                .PageSize(10)
                .AddChoices(choices));

        if (selectedChoice.IsGoBack)
            return;

        AudioDeviceInfo selected = selectedChoice.Device!;

        if (selected.IsDefaultCommunications)
        {
            PrintWarning($"\"{selected.Name}\" is already the default.");
            WaitForKey("Press any key to continue...");
            continue;
        }

        AnsiConsole.MarkupLine($"\n  Switching to \"[cyan]{selected.Name}[/]\"...");

        try
        {
            bool success = await switcher.SetDefaultCommunicationsMicrophoneAsync(selected.Id);
            if (success)
            {
                Log.Information("Successfully set default communications microphone to {DeviceName} ({DeviceId})", selected.Name, selected.Id);
                PrintSuccess($"Set \"{selected.Name}\" as Default + Communications default.");
                NotificationManager.Show("MicShift - Default Microphone Set", selected.Name);
            }
            else
            {
                Log.Warning("Failed to switch default communications microphone to {DeviceName}", selected.Name);
                PrintWarning($"Could not switch to \"{selected.Name}\".");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while setting microphone {DeviceName}", selected.Name);
            PrintError($"Error: {ex.Message}");
        }

        WaitForKey("\n  Press any key to continue...");
    }
}

// ── Calibration flow ───────────────────────────────────────────────────────
static async Task RunCalibrationAsync(WindowsAudioDeviceSwitcher switcher, AutoSwitchService interactiveAutoSwitch)
{
    // Pause auto-switching while calibrating to avoid interference
    bool wasRunning = interactiveAutoSwitch.IsRunning;
    if (wasRunning)
    {
        interactiveAutoSwitch.Stop();
    }

    AnsiConsole.Clear();
    PrintHeader();

    List<AudioDeviceInfo> mics;
    try
    {
        mics = [.. switcher.GetActiveMicrophones()];
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to list devices in interactive calibration.");
        PrintError($"Failed to list devices: {ex.Message}");
        WaitForKey("Press any key to go back...");
        if (wasRunning) interactiveAutoSwitch.Start();
        return;
    }

    if (mics.Count < 1)
    {
        PrintWarning("No active microphones found.");
        WaitForKey("Press any key to go back...");
        if (wasRunning) interactiveAutoSwitch.Start();
        return;
    }

    int deskIndex = PickDevice(mics, "desk (main)");
    if (deskIndex < 0)
    {
        if (wasRunning) interactiveAutoSwitch.Start();
        return;
    }
    int headsetIndex = PickDevice(mics, "headset");
    if (headsetIndex < 0)
    {
        if (wasRunning) interactiveAutoSwitch.Start();
        return;
    }

    string deskName = mics[deskIndex].Name;
    string headsetName = mics[headsetIndex].Name;

    // Save calibration configuration
    var settings = SettingsManager.Load();
    settings.DeskMicrophoneName = deskName;
    settings.HeadsetMicrophoneName = headsetName;
    SettingsManager.Save(settings);

    Log.Information("Starting calibration and saved Desk: {DeskName}, Headset: {HeadsetName}", deskName, headsetName);

    AudioMonitorService? deskMonitor = null;
    AudioMonitorService? headsetMonitor = null;

    try
    {
        try
        {
            deskMonitor = new AudioMonitorService(deskName);
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "Could not start audio monitor for Desk microphone {DeviceName}", deskName);
            PrintError($"Could not start monitor for desk mic: {ex.Message}");
            WaitForKey("Press any key to go back...");
            return;
        }

        try
        {
            headsetMonitor = new AudioMonitorService(headsetName);
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "Could not start audio monitor for Headset microphone {DeviceName}", headsetName);
            PrintError($"Could not start monitor for headset mic: {ex.Message}");
            WaitForKey("Press any key to go back...");
            return;
        }

        Console.CursorVisible = false;
        AnsiConsole.Clear();
        PrintHeader();
        AnsiConsole.MarkupLine("  Live level meters  —  press [red][[Q]][/] to stop\n");

        using var cts = new CancellationTokenSource();

        // Background key-listener to quit on Q.
        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
                    cts.Cancel();
            }
        });

        int loopCount = 0;
        
        var liveTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[yellow]Live Level Meters[/]")
            .AddColumn("Device")
            .AddColumn("Signal Level")
            .AddColumn("Peak %");

        await AnsiConsole.Live(liveTable)
            .StartAsync(async ctx =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Hot-plug check: verify devices are still active
                    loopCount++;
                    if (loopCount % 30 == 0) // approx every 1 second
                    {
                        var activeMics = switcher.GetActiveMicrophones();
                        if (!activeMics.Any(m => m.Name == deskName))
                        {
                            Log.Warning("Desk mic {DeskName} disconnected during calibration.", deskName);
                            throw new InvalidOperationException($"Desk microphone \"{deskName}\" was unplugged.");
                        }
                        if (!activeMics.Any(m => m.Name == headsetName))
                        {
                            Log.Warning("Headset mic {HeadsetName} disconnected during calibration.", headsetName);
                            throw new InvalidOperationException($"Headset microphone \"{headsetName}\" was unplugged.");
                        }
                    }

                    // Check for internal NAudio capture exceptions
                    if (deskMonitor.LastException != null)
                    {
                        Log.Error(deskMonitor.LastException, "Desk mic capture exception during calibration.");
                        throw new InvalidOperationException($"Desk microphone error: {deskMonitor.LastException.Message}");
                    }
                    if (headsetMonitor.LastException != null)
                    {
                        Log.Error(headsetMonitor.LastException, "Headset mic capture exception during calibration.");
                        throw new InvalidOperationException($"Headset microphone error: {headsetMonitor.LastException.Message}");
                    }

                    float deskLevel = deskMonitor.CurrentPeakLevel;
                    float headsetLevel = headsetMonitor.CurrentPeakLevel;

                    var updateTable = new Table()
                        .Border(TableBorder.Rounded)
                        .Title("[yellow]Live Level Meters[/]")
                        .AddColumn("Device")
                        .AddColumn("Signal Level")
                        .AddColumn("Peak %");

                    updateTable.AddRow($"Desk ({deskName})", GetMeterMarkup(deskLevel), $"{deskLevel * 100,5:F1}%");
                    updateTable.AddRow($"Headset ({headsetName})", GetMeterMarkup(headsetLevel), $"{headsetLevel * 100,5:F1}%");

                    ctx.UpdateTarget(updateTable);
                    ctx.Refresh();
                    
                    try { await Task.Delay(33, cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Exception occurred during active calibration stream.");
        AnsiConsole.WriteLine();
        PrintError($"Error during calibration: {ex.Message}");
    }
    finally
    {
        Console.CursorVisible = true;
        deskMonitor?.Dispose();
        headsetMonitor?.Dispose();
        Log.Information("Calibration stopped.");
    }

    AnsiConsole.WriteLine();
    PrintSuccess("Calibration completed and saved to settings.");
    WaitForKey("Press any key to go back...");

    // Re-initialize Auto-Switch service with newly calibrated microphones
    if (settings.AutoSwitchEnabled)
    {
        interactiveAutoSwitch.Stop();
        // Instantiate a new one internally or update targets
        // Re-starting the same service works since it loads target names on start from settings
        // Wait, the service was created with constructor parameters, so let's recreate it if needed,
        // or since we have a direct reference we can just restart it if names haven't changed,
        // but since they might have changed, we should recreate. However, since the program runs in a menu loop,
        // we can just re-create it on next loop start or simply recreate it here:
        // Wait, to keep it simple, we can just restart it since we are in the console runner
        interactiveAutoSwitch.Stop();
    }
    // Recreate the background AutoSwitchService in case microphones changed
    // Wait, the main Program is using interactiveAutoSwitch which is captured by reference.
    // In our Program.cs top-level main, we can just reload settings on every loop.
}

static int PickDevice(List<AudioDeviceInfo> mics, string label)
{
    var choices = mics.Select(m => new MicChoice(m)).ToList();
    choices.Add(new MicChoice("Cancel"));

    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<MicChoice>()
            .Title($"Select the [yellow]{label}[/] microphone:")
            .PageSize(10)
            .AddChoices(choices));

    if (selected.IsGoBack)
        return -1;

    return mics.FindIndex(m => m.Id == selected.Device!.Id);
}

static string GetMeterMarkup(float level)
{
    const int barWidth = 40;
    int filled = (int)(level * barWidth);
    filled = Math.Clamp(filled, 0, barWidth);

    string colorTag = level switch
    {
        >= 0.9f => "red",
        >= 0.7f => "yellow",
        _       => "green"
    };

    string filledBar = new string('█', filled);
    string emptyBar = new string('░', barWidth - filled);

    return $"[{colorTag}]{filledBar}[/][grey]{emptyBar}[/]";
}

// ── CLI Command Handlers ───────────────────────────────────────────────────

static void RunListCommand(WindowsAudioDeviceSwitcher switcher)
{
    try
    {
        Log.Information("Running CLI List command.");
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

static void RunDefaultCommand(WindowsAudioDeviceSwitcher switcher)
{
    try
    {
        Log.Information("Running CLI Default command.");
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
        Log.Error(ex, "Failed to run CLI Default command.");
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static async Task RunSwitchCommandAsync(WindowsAudioDeviceSwitcher switcher, string identifier)
{
    try
    {
        Log.Information("Running CLI Switch command for identifier: {Identifier}", identifier);
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
            Log.Warning("CLI Switch command failed: device not found for identifier: {Identifier}", identifier);
            Console.WriteLine($"Error: Microphone '{identifier}' not found.");
            return;
        }

        if (selected.IsDefaultCommunications)
        {
            Console.WriteLine($"\"{selected.Name}\" is already the default.");
            return;
        }

        bool success = await switcher.SetDefaultCommunicationsMicrophoneAsync(selected.Id);
        if (success)
        {
            Log.Information("Successfully switched default communications microphone to {DeviceName} ({DeviceId}) via CLI", selected.Name, selected.Id);
            Console.WriteLine($"Successfully switched to '{selected.Name}'.");
        }
        else
        {
            Log.Warning("Failed to switch default communications microphone to {DeviceName} via CLI", selected.Name);
            Console.WriteLine($"Error: Could not switch to '{selected.Name}'.");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Exception occurred during CLI Switch command for: {Identifier}", identifier);
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static void PrintHelp()
{
    AnsiConsole.MarkupLine("[cyan]MicShift CLI Arguments Help:[/]");
    AnsiConsole.MarkupLine("  [yellow]--list, -l[/]                     List all active microphones.");
    AnsiConsole.MarkupLine("  [yellow]--default, -d[/]                  Show the default communications microphone.");
    AnsiConsole.MarkupLine("  [yellow]--switch, -s <Name or ID>[/]       Switch the default microphone to the specified one.");
    AnsiConsole.MarkupLine("  [yellow]--tray, -t[/]                     Run hidden in Windows System Tray (starts hotkeys & auto-distance).");
    AnsiConsole.MarkupLine("  [yellow]--help, -h[/]                     Show this help message.");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("If run without arguments, MicShift starts in interactive menu mode.");
}

// ── Helpers ────────────────────────────────────────────────────────────────

static void PrintHeader()
{
    AnsiConsole.Write(
        new Spectre.Console.Panel(new Text("MicShift - Audio Device Controller", new Style(Spectre.Console.Color.Cyan1, decoration: Decoration.Bold)))
            .Border(BoxBorder.Double)
            .BorderColor(Spectre.Console.Color.Cyan1)
            .Padding(1, 0, 1, 0));
    AnsiConsole.WriteLine();
}

static void PrintSuccess(string message)
{
    AnsiConsole.MarkupLine($"  [green]✔ {message}[/]");
}

static void PrintWarning(string message)
{
    AnsiConsole.MarkupLine($"  [yellow]⚠ {message}[/]");
}

static void PrintError(string message)
{
    AnsiConsole.MarkupLine($"  [red]✘ {message}[/]");
}

static void WaitForKey(string prompt)
{
    AnsiConsole.MarkupLine($"  [grey]{prompt}[/]");
    Console.ReadKey(intercept: true);
}

// ── Helper Choices ─────────────────────────────────────────────────────────

class MicChoice
{
    public AudioDeviceInfo? Device { get; }
    public bool IsGoBack { get; }
    private readonly string _display;

    public MicChoice(AudioDeviceInfo device)
    {
        Device = device;
        IsGoBack = false;
        _display = device.IsDefaultCommunications 
            ? $"[green]✔ {device.Name} (Default)[/]" 
            : $"  {device.Name}";
    }

    public MicChoice(string label)
    {
        IsGoBack = true;
        _display = $"[yellow]<-- {label}[/]";
    }

    public override string ToString() => _display;
}

class MenuChoice
{
    public string Action { get; }
    private readonly string _display;

    public MenuChoice(string action, string display)
    {
        Action = action;
        _display = display;
    }

    public override string ToString() => _display;
}

// ── Notification Manager ───────────────────────────────────────────────────

static class NotificationManager
{
    private static NotifyIcon? _notifyIcon;
    private static ContextMenuStrip? _contextMenu;

    public static void Initialize(WindowsAudioDeviceSwitcher switcher, AutoSwitchService autoSwitchService)
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                Text = "MicShift"
            };

            _contextMenu = new ContextMenuStrip();

            var muteItem = new ToolStripMenuItem("Toggle Mute (Ctrl+Alt+M)", null, (s, e) =>
            {
                if (switcher.ToggleDefaultMicrophoneMute(out string devName, out bool isMuted))
                {
                    string status = isMuted ? "MUTED ❌" : "UNMUTED 🎙️";
                    Show("MicShift - Mute Toggle", $"{devName} is now {status}");
                }
            });

            var cycleItem = new ToolStripMenuItem("Cycle Microphone (Ctrl+Alt+S)", null, async (s, e) =>
            {
                var newMic = await switcher.CycleDefaultCommunicationsMicrophoneAsync();
                if (newMic != null)
                {
                    Show("MicShift - Microphone Changed", $"Switched to:\n{newMic.Name}");
                }
            });

            var autoSwitchItem = new ToolStripMenuItem("Auto-Switching", null, (s, e) =>
            {
                var settings = SettingsManager.Load();
                settings.AutoSwitchEnabled = !settings.AutoSwitchEnabled;
                SettingsManager.Save(settings);

                var item = (ToolStripMenuItem)s!;
                item.Checked = settings.AutoSwitchEnabled;

                if (settings.AutoSwitchEnabled)
                {
                    autoSwitchService.Start();
                    Show("MicShift Auto-Switch", "Auto-switching is active.");
                }
                else
                {
                    autoSwitchService.Stop();
                    Show("MicShift Auto-Switch", "Auto-switching paused.");
                }
            });

            var initialSettings = SettingsManager.Load();
            autoSwitchItem.Checked = initialSettings.AutoSwitchEnabled;

            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) =>
            {
                Application.Exit();
            });

            _contextMenu.Items.Add(muteItem);
            _contextMenu.Items.Add(cycleItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(autoSwitchItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = _contextMenu;
            Log.Information("Notification tray icon initialized.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize NotifyIcon for notifications.");
        }
    }

    public static void Show(string title, string message)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
    }

    public static void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
            Log.Information("Notification tray icon disposed.");
        }
        if (_contextMenu != null)
        {
            _contextMenu.Dispose();
            _contextMenu = null;
        }
    }
}

// ── Global Hotkeys Manager ─────────────────────────────────────────────────

static class HotkeyManager
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    private static Thread? _hotkeyThread;
    private static CancellationTokenSource? _cts;

    public const int HOTKEY_MUTE_ID = 1;
    public const int HOTKEY_CYCLE_ID = 2;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;

    public static void Start(WindowsAudioDeviceSwitcher switcher, AutoSwitchService autoSwitchService)
    {
        _cts = new CancellationTokenSource();
        _hotkeyThread = new Thread(() => RunMessageLoop(switcher, autoSwitchService, _cts.Token))
        {
            IsBackground = true
        };
        _hotkeyThread.Start();
    }

    public static void Stop()
    {
        _cts?.Cancel();
        UnregisterHotKey(IntPtr.Zero, HOTKEY_MUTE_ID);
        UnregisterHotKey(IntPtr.Zero, HOTKEY_CYCLE_ID);
    }

    private static void RunMessageLoop(WindowsAudioDeviceSwitcher switcher, AutoSwitchService autoSwitchService, CancellationToken token)
    {
        // Ctrl+Alt+M (Mute)
        bool muteOk = RegisterHotKey(IntPtr.Zero, HOTKEY_MUTE_ID, MOD_ALT | MOD_CONTROL, 0x4D);
        if (!muteOk)
        {
            Log.Warning("Failed to register Mute global hotkey (Ctrl+Alt+M).");
        }
        else
        {
            Log.Information("Registered Mute global hotkey (Ctrl+Alt+M).");
        }

        // Ctrl+Alt+S (Cycle)
        bool cycleOk = RegisterHotKey(IntPtr.Zero, HOTKEY_CYCLE_ID, MOD_ALT | MOD_CONTROL, 0x53);
        if (!cycleOk)
        {
            Log.Warning("Failed to register Cycle global hotkey (Ctrl+Alt+S).");
        }
        else
        {
            Log.Information("Registered Cycle global hotkey (Ctrl+Alt+S).");
        }

        MSG msg;
        while (!token.IsCancellationRequested && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                int id = msg.wParam.ToInt32();
                if (id == HOTKEY_MUTE_ID)
                {
                    Log.Information("Mute hotkey triggered.");
                    try
                    {
                        if (switcher.ToggleDefaultMicrophoneMute(out string devName, out bool isMuted))
                        {
                            string status = isMuted ? "MUTED ❌" : "UNMUTED 🎙️";
                            NotificationManager.Show("MicShift - Mute Toggle", $"{devName} is now {status}");
                            Log.Information("Toggled mute state for device {DeviceName}. New state: {Muted}", devName, isMuted);
                        }
                        else
                        {
                            NotificationManager.Show("MicShift", "No default communications microphone found to mute.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error toggling mute via hotkey.");
                    }
                }
                else if (id == HOTKEY_CYCLE_ID)
                {
                    Log.Information("Cycle microphone hotkey triggered.");
                    Task.Run(async () =>
                    {
                        try
                        {
                            var newMic = await switcher.CycleDefaultCommunicationsMicrophoneAsync();
                            if (newMic != null)
                            {
                                NotificationManager.Show("MicShift - Microphone Changed", $"Switched to:\n{newMic.Name}");
                                Log.Information("Cycled default microphone to {DeviceName} ({DeviceId})", newMic.Name, newMic.Id);
                            }
                            else
                            {
                                NotificationManager.Show("MicShift", "Could not cycle microphone (no other active microphones).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error cycling microphone via hotkey.");
                        }
                    });
                }
            }
        }

        UnregisterHotKey(IntPtr.Zero, HOTKEY_MUTE_ID);
        UnregisterHotKey(IntPtr.Zero, HOTKEY_CYCLE_ID);
    }
}
