using System.IO;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using Serilog;

namespace MicShift;

public static class NotificationManager
{
    private static NotifyIcon? _notifyIcon;
    private static ContextMenuStrip? _contextMenu;

    public static void Initialize(WindowsAudioDeviceSwitcher switcher, AutoSwitchService autoSwitchService)
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = "MicShift"
            };

            // Try to load custom icon from embedded WPF resources
            try
            {
                var resourceUri = new Uri("pack://application:,,,/logo.png");
                var streamResourceInfo = Application.GetResourceStream(resourceUri);
                if (streamResourceInfo != null)
                {
                    using var stream = streamResourceInfo.Stream;
                    using var bitmap = new System.Drawing.Bitmap(stream);
                    _notifyIcon.Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load tray icon from resources. Falling back to default.");
                _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
            }

            // Double click restores the main window
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            _contextMenu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("Open Settings", null, (s, e) => ShowMainWindow());

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

                // Sync main window toggle if open
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var win = Application.Current.MainWindow as MainWindow;
                    if (win != null)
                    {
                        win.AutoSwitchToggle.IsChecked = settings.AutoSwitchEnabled;
                    }
                });

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
                Log.Information("Exit clicked from System Tray menu. Shutting down.");
                Application.Current.Shutdown();
            });

            _contextMenu.Items.Add(openItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(muteItem);
            _contextMenu.Items.Add(cycleItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(autoSwitchItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = _contextMenu;
            Log.Information("System Tray notification icon initialized successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize NotifyIcon for System Tray.");
        }
    }

    private static void ShowMainWindow()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = Application.Current.MainWindow;
            if (win != null)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
                Log.Debug("MainWindow restored from system tray.");
            }
        });
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
            Log.Information("System Tray icon disposed.");
        }
        if (_contextMenu != null)
        {
            _contextMenu.Dispose();
            _contextMenu = null;
        }
    }
}
