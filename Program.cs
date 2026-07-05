using MicShift;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MicShift";

using var switcher = new WindowsAudioDeviceSwitcher();

// ── Main menu ──────────────────────────────────────────────────────────────
while (true)
{
    AnsiConsole.Clear();
    PrintHeader();

    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<MenuChoice>()
            .Title("[yellow]Select an option:[/]")
            .PageSize(5)
            .AddChoices(new MenuChoice[]
            {
                new("S", "[cyan]Switch default microphone[/]"),
                new("C", "[cyan]Calibration mode (live level meters)[/]"),
                new("Q", "[red]Quit[/]")
            }));

    switch (choice.Action)
    {
        case "S": await RunSwitcherAsync(switcher); break;
        case "C": await RunCalibrationAsync(switcher); break;
        case "Q": goto done;
    }
}
done:
AnsiConsole.Clear();
AnsiConsole.MarkupLine("[yellow]Goodbye.[/]");
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
            PrintError($"Failed to list devices: {ex.Message}");
            WaitForKey("Press any key to retry...");
            continue;
        }

        if (mics.Count == 0)
        {
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
                PrintSuccess($"Set \"{selected.Name}\" as Default + Communications default.");
            else
                PrintWarning($"Could not switch to \"{selected.Name}\".");
        }
        catch (Exception ex)
        {
            PrintError($"Error: {ex.Message}");
        }

        WaitForKey("\n  Press any key to continue...");
    }
}

// ── Calibration flow ───────────────────────────────────────────────────────
static async Task RunCalibrationAsync(WindowsAudioDeviceSwitcher switcher)
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
        PrintError($"Failed to list devices: {ex.Message}");
        WaitForKey("Press any key to go back...");
        return;
    }

    if (mics.Count < 1)
    {
        PrintWarning("No active microphones found.");
        WaitForKey("Press any key to go back...");
        return;
    }

    int deskIndex = PickDevice(mics, "desk (main)");
    if (deskIndex < 0) return;
    int headsetIndex = PickDevice(mics, "headset");
    if (headsetIndex < 0) return;

    string deskName = mics[deskIndex].Name;
    string headsetName = mics[headsetIndex].Name;

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
                            throw new InvalidOperationException($"Desk microphone \"{deskName}\" was unplugged.");
                        if (!activeMics.Any(m => m.Name == headsetName))
                            throw new InvalidOperationException($"Headset microphone \"{headsetName}\" was unplugged.");
                    }

                    // Check for internal NAudio capture exceptions
                    if (deskMonitor.LastException != null)
                        throw new InvalidOperationException($"Desk microphone error: {deskMonitor.LastException.Message}");
                    if (headsetMonitor.LastException != null)
                        throw new InvalidOperationException($"Headset microphone error: {headsetMonitor.LastException.Message}");

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
        AnsiConsole.WriteLine();
        PrintError($"Error during calibration: {ex.Message}");
    }
    finally
    {
        Console.CursorVisible = true;
        deskMonitor?.Dispose();
        headsetMonitor?.Dispose();
    }

    AnsiConsole.WriteLine();
    PrintSuccess("Calibration stopped.");
    WaitForKey("Press any key to go back...");
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
