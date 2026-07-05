using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;

using Color = System.Windows.Media.Color;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace MicShift;

public partial class MainWindow : Window
{
    private readonly WindowsAudioDeviceSwitcher _switcher;
    private readonly AutoSwitchService _autoSwitch;
    private readonly DispatcherTimer _timer;
    private bool _isInitializing = true;
    private bool _isExplicitExit = false;

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
            // Start pulsing status indicator animation
            if (FindResource("PulseStoryboard") is Storyboard pulseStoryboard)
            {
                pulseStoryboard.Begin(this);
            }

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

            NotificationsToggle.IsChecked = settings.NotificationsEnabled;

            // Check if microphones are configured
            bool isConfigured = !string.IsNullOrEmpty(settings.DeskMicrophoneName) && !string.IsNullOrEmpty(settings.HeadsetMicrophoneName);
            if (!isConfigured)
            {
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

            // Force update microphones on load to initialize monitors
            if (isConfigured)
            {
                _autoSwitch.UpdateMicrophones(settings.DeskMicrophoneName, settings.HeadsetMicrophoneName);
            }

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

        AnimateBar(DeskLevelBar, DeskLevelValue, deskLevel, new SolidColorBrush(Color.FromRgb(6, 182, 212))); // Cyan
        AnimateBar(HeadsetLevelBar, HeadsetLevelValue, headsetLevel, new SolidColorBrush(Color.FromRgb(16, 185, 129))); // Emerald Green
    }

    private void AnimateBar(ProgressBar bar, TextBlock valueText, float targetLevel, SolidColorBrush activeColor)
    {
        float targetValue = targetLevel * 100f;

        // Smoothly animate ProgressBar Value
        var animation = new DoubleAnimation
        {
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(ProgressBar.ValueProperty, animation);

        valueText.Text = $"{targetValue:F1}%";

        // Dynamic warning color for peaks above 80%
        if (targetLevel >= 0.8f)
        {
            bar.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
        }
        else
        {
            bar.Foreground = activeColor;
        }
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

        // Update active service (this will also update active monitors dynamically!)
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

    private void OnNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool isChecked = NotificationsToggle.IsChecked ?? false;

        var settings = SettingsManager.Load();
        settings.NotificationsEnabled = isChecked;
        SettingsManager.Save(settings);
    }

    private void UpdateStatusUI()
    {
        if (_autoSwitch.IsRunning)
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
            StatusText.Text = "Auto-Switching is Active";
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
            StatusText.Text = "Auto-Switching is Inactive";
        }
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
        NotificationManager.Show("MicShift Running", "MicShift is running in the background. Double-click the tray icon to open.");
    }

    public void PrepareForExit()
    {
        _isExplicitExit = true;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Log.Information("Exit button clicked in UI. Shutting down.");
        PrepareForExit();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            _timer.Stop();
            return;
        }

        // Intercept close button and hide instead
        e.Cancel = true;
        this.Hide();
        NotificationManager.Show("MicShift Running", "MicShift is running in the background. Double-click the tray icon to open.");
    }
}
