using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;

using Color = System.Windows.Media.Color;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MicShift;

public partial class MainWindow : Window
{
    private readonly WindowsAudioDeviceSwitcher _switcher;
    private readonly AutoSwitchService _autoSwitch;
    private readonly DispatcherTimer _timer;
    private bool _isInitializing = true;
    private bool _isExplicitExit = false;
    private int _activeIndicatorTickCounter = 0;

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

            // Set default tab navigation view
            UpdateSidebarNavigation(0);

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
            ThemeToggle.IsChecked = settings.IsDarkMode;

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

            // Start monitors exactly once — only if both mics are configured.
            // We do NOT call UpdateMicrophones() from the constructor of AutoSwitchService
            // to avoid a double-start race condition (Bug #2/#3).
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

        AnimateBar(DeskLevelBar, DeskLevelValue, deskLevel, FindResource("AccentColor") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(6, 182, 212)));
        AnimateBar(HeadsetLevelBar, HeadsetLevelValue, headsetLevel, FindResource("SecondaryAccentColor") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(16, 185, 129)));

        // Update active microphone indicators — throttled to ~1 Hz (every 30 ticks at 33ms).
        _activeIndicatorTickCounter++;
        if (_activeIndicatorTickCounter >= 30)
        {
            _activeIndicatorTickCounter = 0;
            try
            {
                var activeMics = _switcher.GetActiveMicrophones();
                var defaultMic = activeMics.FirstOrDefault(m => m.IsDefaultCommunications);
                if (defaultMic != null)
                {
                    string deskName    = DeskMicComboBox.SelectedItem    as string ?? string.Empty;
                    string headsetName = HeadsetMicComboBox.SelectedItem as string ?? string.Empty;

                    if (!string.IsNullOrEmpty(deskName) && defaultMic.Name.Equals(deskName, StringComparison.OrdinalIgnoreCase))
                    {
                        DeskActiveLabel.Text       = "• Active";
                        DeskActiveLabel.Foreground = FindResource("AccentColor") as Brush;
                        HeadsetActiveLabel.Text    = "";
                    }
                    else if (!string.IsNullOrEmpty(headsetName) && defaultMic.Name.Equals(headsetName, StringComparison.OrdinalIgnoreCase))
                    {
                        HeadsetActiveLabel.Text       = "• Active";
                        HeadsetActiveLabel.Foreground = FindResource("SecondaryAccentColor") as Brush;
                        DeskActiveLabel.Text          = "";
                    }
                    else
                    {
                        DeskActiveLabel.Text    = "";
                        HeadsetActiveLabel.Text = "";
                    }
                }
                else
                {
                    DeskActiveLabel.Text    = "";
                    HeadsetActiveLabel.Text = "";
                }
            }
            catch
            {
                // Fail silently — COM enumeration may not be available every tick.
            }
        }
    }

    private void AnimateBar(ProgressBar bar, TextBlock valueText, float targetLevel, SolidColorBrush activeColor)
    {
        // User requested raw, accurate values instead of artificially boosted ones
        float targetValue = targetLevel * 100f;
        
        if (targetValue > 100f) targetValue = 100f;
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

    private void OnThemeToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool isDark = ThemeToggle.IsChecked ?? false;

        var settings = SettingsManager.Load();
        settings.IsDarkMode = isDark;
        SettingsManager.Save(settings);

        // Apply theme dynamically
        App.ApplyTheme(isDark);

        // Refresh navigation bar buttons background color since resources changed
        UpdateSidebarNavigation(MainTabControl.SelectedIndex);
    }

    private void UpdateStatusUI()
    {
        if (_autoSwitch.IsRunning)
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
            StatusText.Text = "Active";
        }
        else
        {
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
            StatusText.Text = "Inactive";
        }
    }

    private void OnNavDashboardClick(object sender, RoutedEventArgs e)
    {
        UpdateSidebarNavigation(0);
    }

    private void OnNavSettingsClick(object sender, RoutedEventArgs e)
    {
        UpdateSidebarNavigation(1);
    }

    private void UpdateSidebarNavigation(int selectedIndex)
    {
        MainTabControl.SelectedIndex = selectedIndex;

        // Update visual active states for navigation buttons
        if (selectedIndex == 0)
        {
            NavDashboardBtn.Background = FindResource("CardBackground") as Brush;
            NavDashboardBtn.Foreground = FindResource("AccentColor") as Brush;
            NavSettingsBtn.Background = Brushes.Transparent;
            NavSettingsBtn.Foreground = FindResource("MutedText") as Brush;
        }
        else
        {
            NavDashboardBtn.Background = Brushes.Transparent;
            NavDashboardBtn.Foreground = FindResource("MutedText") as Brush;
            NavSettingsBtn.Background = FindResource("CardBackground") as Brush;
            NavSettingsBtn.Foreground = FindResource("AccentColor") as Brush;
        }
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
        NotificationManager.Show("MicShift", "Running in background.");
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
        NotificationManager.Show("MicShift", "Running in background.");
    }
}
