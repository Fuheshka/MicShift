using MicShift;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MicShift";

using var switcher = new WindowsAudioDeviceSwitcher();

// ── Main menu ──────────────────────────────────────────────────────────────
while (true)
{
    Console.Clear();
    PrintHeader();

    Console.WriteLine("  [S] Switch default microphone");
    Console.WriteLine("  [C] Calibration mode (live level meters)");
    Console.WriteLine("  [Q] Quit");
    Console.WriteLine();
    Console.Write("  Choose: ");

    string? menuInput = Console.ReadLine()?.Trim().ToUpperInvariant();

    switch (menuInput)
    {
        case "S": await RunSwitcherAsync(switcher); break;
        case "C": await RunCalibrationAsync(switcher); break;
        case "Q": goto done;
        default:
            PrintWarning("Unknown option.");
            WaitForKey("Press any key to continue...");
            break;
    }
}
done:
Console.Clear();
Console.WriteLine("Goodbye.");
return;

// ── Switcher flow ──────────────────────────────────────────────────────────
static async Task RunSwitcherAsync(WindowsAudioDeviceSwitcher switcher)
{
    while (true)
    {
        Console.Clear();
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

        Console.WriteLine("  Active microphones:\n");
        for (int i = 0; i < mics.Count; i++)
        {
            AudioDeviceInfo mic = mics[i];
            if (mic.IsDefaultCommunications)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [{i + 1}] {mic.Name}  <-- DEFAULT");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine($"  [{i + 1}] {mic.Name}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Enter the number to set as default,");
        Console.Write("  or [B] to go back: ");

        string? input = Console.ReadLine()?.Trim();

        if (string.Equals(input, "b", StringComparison.OrdinalIgnoreCase))
            return;

        if (!int.TryParse(input, out int choice) || choice < 1 || choice > mics.Count)
        {
            PrintWarning("Invalid selection.");
            WaitForKey("Press any key to try again...");
            continue;
        }

        AudioDeviceInfo selected = mics[choice - 1];

        if (selected.IsDefaultCommunications)
        {
            PrintWarning($"\"{selected.Name}\" is already the default.");
            WaitForKey("Press any key to continue...");
            continue;
        }

        Console.WriteLine();
        Console.Write($"  Switching to \"{selected.Name}\"...");

        try
        {
            bool success = await switcher.SetDefaultCommunicationsMicrophoneAsync(selected.Id);
            Console.WriteLine();
            if (success)
                PrintSuccess($"Set \"{selected.Name}\" as Default + Communications default.");
            else
                PrintWarning($"Could not switch to \"{selected.Name}\".");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            PrintError($"Error: {ex.Message}");
        }

        WaitForKey("\n  Press any key to continue...");
    }
}

// ── Calibration flow ───────────────────────────────────────────────────────
static async Task RunCalibrationAsync(WindowsAudioDeviceSwitcher switcher)
{
    Console.Clear();
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

    Console.WriteLine("  Available microphones:\n");
    for (int i = 0; i < mics.Count; i++)
        Console.WriteLine($"  [{i + 1}] {mics[i].Name}");

    Console.WriteLine();

    int deskIndex   = PickDevice(mics, "desk (main)");
    int headsetIndex = PickDevice(mics, "headset");

    if (deskIndex < 0 || headsetIndex < 0) return;

    AudioMonitorService? deskMonitor   = null;
    AudioMonitorService? headsetMonitor = null;

    try
    {
        try
        {
            deskMonitor = new AudioMonitorService(mics[deskIndex].Name);
        }
        catch (ArgumentException ex)
        {
            PrintError($"Could not start monitor for desk mic: {ex.Message}");
            WaitForKey("Press any key to go back...");
            return;
        }

        try
        {
            headsetMonitor = new AudioMonitorService(mics[headsetIndex].Name);
        }
        catch (ArgumentException ex)
        {
            PrintError($"Could not start monitor for headset mic: {ex.Message}");
            WaitForKey("Press any key to go back...");
            return;
        }

        Console.CursorVisible = false;
        Console.Clear();
        PrintHeader();
        Console.WriteLine("  Live level meters  —  press [Q] to stop\n");

        // Reserve rows for the two meters.
        int deskRow    = Console.CursorTop;
        Console.WriteLine();
        int headsetRow = Console.CursorTop;
        Console.WriteLine();
        Console.WriteLine();

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

        // Render loop at ~30 fps.
        while (!cts.Token.IsCancellationRequested)
        {
            DrawMeter(deskRow,    $"  Desk    [{mics[deskIndex].Name[..Math.Min(mics[deskIndex].Name.Length, 22)],   -22}]", deskMonitor.CurrentPeakLevel);
            DrawMeter(headsetRow, $"  Headset [{mics[headsetIndex].Name[..Math.Min(mics[headsetIndex].Name.Length, 22)], -22}]", headsetMonitor.CurrentPeakLevel);

            try { await Task.Delay(33, cts.Token); }
            catch (TaskCanceledException) { break; }
        }
    }
    finally
    {
        Console.CursorVisible = true;
        deskMonitor?.Dispose();
        headsetMonitor?.Dispose();
    }

    Console.WriteLine();
    PrintSuccess("Calibration stopped.");
    WaitForKey("Press any key to go back...");
}

static int PickDevice(List<AudioDeviceInfo> mics, string label)
{
    while (true)
    {
        Console.Write($"  Enter number for the {label} mic (or [B] to cancel): ");
        string? input = Console.ReadLine()?.Trim();

        if (string.Equals(input, "b", StringComparison.OrdinalIgnoreCase))
            return -1;

        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= mics.Count)
            return idx - 1;

        PrintWarning("  Invalid selection, try again.");
    }
}

static void DrawMeter(int row, string label, float level)
{
    const int barWidth = 40;
    int filled = (int)(level * barWidth);
    filled = Math.Clamp(filled, 0, barWidth);

    string bar = new string('█', filled) + new string('░', barWidth - filled);

    // Colour: green < 70%, yellow < 90%, red >= 90%
    ConsoleColor color = level switch
    {
        >= 0.9f => ConsoleColor.Red,
        >= 0.7f => ConsoleColor.Yellow,
        _       => ConsoleColor.Green
    };

    int savedTop  = Console.CursorTop;
    int savedLeft = Console.CursorLeft;

    Console.SetCursorPosition(0, row);
    Console.Write(label + " ");
    Console.ForegroundColor = color;
    Console.Write($"[{bar}]");
    Console.ResetColor();
    Console.Write($" {level * 100,5:F1}%  ");

    Console.SetCursorPosition(savedLeft, savedTop);
}

// ── Helpers ────────────────────────────────────────────────────────────────

static void PrintHeader()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  ╔══════════════════════╗");
    Console.WriteLine("  ║       MicShift       ║");
    Console.WriteLine("  ╚══════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
}

static void PrintSuccess(string message)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✔ {message}");
    Console.ResetColor();
}

static void PrintWarning(string message)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  ⚠ {message}");
    Console.ResetColor();
}

static void PrintError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  ✘ {message}");
    Console.ResetColor();
}

static void WaitForKey(string prompt)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(prompt);
    Console.ResetColor();
    Console.ReadKey(intercept: true);
}
