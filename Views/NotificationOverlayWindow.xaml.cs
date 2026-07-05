using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MicShift.Views;

public partial class NotificationOverlayWindow : Window
{
    private DispatcherTimer? _timer;

    public NotificationOverlayWindow(string title, string message)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position in the bottom-right corner of the working area (above the taskbar)
        var workingArea = SystemParameters.WorkArea;
        Left = workingArea.Right - Width - 20;
        Top = workingArea.Bottom - Height - 20;

        // Animate Fade In
        Opacity = 0;
        var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
        BeginAnimation(OpacityProperty, fadeIn);

        // Timer to trigger fade out after 1.5 seconds
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (s, ev) =>
        {
            _timer.Stop();
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (sender2, e2) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        _timer.Start();
    }
}
