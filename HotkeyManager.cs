using System.Runtime.InteropServices;
using Serilog;

namespace MicShift;

public static class HotkeyManager
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
