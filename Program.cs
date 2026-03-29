using MicShift;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MicShift";

using var switcher = new WindowsAudioDeviceSwitcher();

while (true)
{
    Console.Clear();
    PrintHeader();

    // --- List devices ---
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
        PrintWarning("No active microphones found. Make sure a microphone is connected and enabled.");
        WaitForKey("Press any key to exit...");
        break;
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

    // --- Prompt ---
    Console.WriteLine();
    Console.WriteLine("  Enter the number of the microphone to set as default,");
    Console.Write("  or [Q] to quit: ");

    string? input = Console.ReadLine()?.Trim();

    if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase))
        break;

    if (!int.TryParse(input, out int choice) || choice < 1 || choice > mics.Count)
    {
        PrintWarning("Invalid selection.");
        WaitForKey("Press any key to try again...");
        continue;
    }

    AudioDeviceInfo selected = mics[choice - 1];

    if (selected.IsDefaultCommunications)
    {
        PrintWarning($"\"{selected.Name}\" is already the default communications microphone.");
        WaitForKey("Press any key to continue...");
        continue;
    }

    // --- Switch ---
    Console.WriteLine();
    Console.Write($"  Switching to \"{selected.Name}\"...");

    try
    {
        bool success = await switcher.SetDefaultCommunicationsMicrophoneAsync(selected.Id);
        Console.WriteLine();

        if (success)
            PrintSuccess($"Successfully set \"{selected.Name}\" as the default communications microphone.");
        else
            PrintWarning($"Could not switch to \"{selected.Name}\". The device may have become unavailable.");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        PrintError($"Error switching microphone: {ex.Message}");
    }

    WaitForKey("\n  Press any key to continue...");
}

Console.Clear();
Console.WriteLine("Goodbye.");

// ---- Helpers ----

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
