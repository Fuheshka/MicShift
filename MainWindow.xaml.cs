using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace MicShift;

public partial class MainWindow : Window
{
    private readonly WindowsAudioDeviceSwitcher _switcher;
    private readonly AutoSwitchService _autoSwitch;
    private readonly DispatcherTimer _timer;
    private bool _isInitializing = true;

    public MainWindow(WindowsAudioDeviceSwitcher switcher, AutoSwitchService autoSwitch)
    {
        InitializeComponent();

        _switcher = switcher;
        _autoSwitch = autoSwitch;

        // Configure timer for volume meters
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _timer.Tick += OnTimerTick;

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshMicrophoneLists();

            // Load saved settings
            var settings = SettingsManager.Load();
            
            // Set selections
            if (!string.IsNullOrEmpty(settings.DeskMicrophoneName))
            {
                DeskMicComboBox.SelectedItem = settings.DeskMicrophoneName;
            }
            if (!string.IsNullOrEmpty(settings.HeadsetMicrophoneName))
            {
                HeadsetMicComboBox.SelectedItem = settings.HeadsetMicrophoneName;
            }

            // Check if microphones are configured
            bool isConfigured = !string.IsNullOrEmpty(settings.DeskMicrophoneName) && !string.IsNullOrEmpty(settings.HeadsetMicrophoneName);
            if (!isConfigured)
            {
                // Disable auto-switching if not configured
                settings.AutoSwitchEnabled = false;
                SettingsManager.Save(settings);
                _autoSwitch.Stop();
                AutoSwitchToggle.IsChecked = false;
                AutoSwitchToggle.IsEnabled = false;
            }
            else
            {
                AutoSwitchToggle.IsEnabled = true;
                AutoSwitchToggle.IsChecked = settings.AutoSwitchEnabled;
            }

            _isInitializing = false;

            UpdateStatusUI();
            _timer.Start();

            // Get executing version
            var version = typeof(App).Assembly.GetName().Version;
            VersionTextBlock.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.3.0";

            Log.Information("MainWindow UI initialized and shown.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading MainWindow UI.");
        }
    }

    private void RefreshMicrophoneLists()
    {
        try
        {
            var mics = _switcher.GetActiveMicrophones().Select(m => m.Name).ToList();
            DeskMicComboBox.ItemsSource = mics;
            HeadsetMicComboBox.ItemsSource = mics;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh active microphones list in UI.");
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Update VU meters
        float deskLevel = _autoSwitch.DeskPeakLevel;
        float headsetLevel = _autoSwitch.HeadsetPeakLevel;

        DeskLevelBar.Value = deskLevel * 100f;
        DeskLevelValue.Text = $"{deskLevel * 100f:F1}%";

        // Dynamic color shifting for volume peak visualization
        if (deskLevel >= 0.8f)
            DeskLevelBar.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // Red
        else
            DeskLevelBar.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 182, 212)); // Cyan

        HeadsetLevelBar.Value = headsetLevel * 100f;
        HeadsetLevelValue.Text = $"{headsetLevel * 100f:F1}%";

        if (headsetLevel >= 0.8f)
            HeadsetLevelBar.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // Red
        else
            HeadsetLevelBar.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Emerald Green
    }

    private void OnMicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        string deskName = DeskMicComboBox.SelectedItem as string ?? string.Empty;
        string headsetName = HeadsetMicComboBox.SelectedItem as string ?? string.Empty;

        // Save settings
        var settings = SettingsManager.Load();
        settings.DeskMicrophoneName = deskName;
        settings.HeadsetMicrophoneName = headsetName;

        bool isConfigured = !string.IsNullOrEmpty(deskName) && !string.IsNullOrEmpty(headsetName);
        if (!isConfigured)
        {
            settings.AutoSwitchEnabled = false;
            AutoSwitchToggle.IsChecked = false;
            AutoSwitchToggle.IsEnabled = false;
            _autoSwitch.Stop();
        }
        else
        {
            AutoSwitchToggle.IsEnabled = true;
        }
        
        SettingsManager.Save(settings);

        // Update active service
        _autoSwitch.UpdateMicrophones(deskName, headsetName);
        UpdateStatusUI();
        Log.Information("UI Mic Switch configured: Desk={DeskName}, Headset={HeadsetName}", deskName, headsetName);
    }

    private void OnAutoSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool isChecked = AutoSwitchToggle.IsChecked ?? false;

        var settings = SettingsManager.Load();
        settings.AutoSwitchEnabled = isChecked;
        SettingsManager.Save(settings);

        if (isChecked)
        {
            _autoSwitch.Start();
        }
        else
        {
            _autoSwitch.Stop();
        }

        UpdateStatusUI();
    }

    private void UpdateStatusUI()
    {
        if (_autoSwitch.IsRunning)
        {
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Emerald Green
            StatusText.Text = "Auto-Switching is Active";
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // Red
            StatusText.Text = "Auto-Switching is Inactive";
        }
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
        NotificationManager.Show("MicShift Running", "MicShift is running in the background. Double-click the tray icon to open.");
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Log.Information("Exit button clicked in UI. Shutting down.");
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Intercept close button and hide instead
        e.Cancel = true;
        this.Hide();
        NotificationManager.Show("MicShift Running", "MicShift is running in the background. Double-click the tray icon to open.");
    }
}
