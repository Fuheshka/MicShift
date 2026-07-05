using NAudio.Wave;
using Serilog;

namespace MicShift;

/// <summary>
/// Streams audio from a single capture device using WinMM WaveInEvent
/// and exposes a rolling peak level (0.0 – 1.0).
/// Uses 100% safe WinMM APIs to avoid COM cast exceptions with AudioSwitcher.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    private WaveInEvent? _waveIn;
    private volatile float _currentPeakLevel;
    private volatile Exception? _lastException;
    private bool _disposed;

    public string DeviceName { get; }
    public Exception? LastException => _lastException;
    public float CurrentPeakLevel => _currentPeakLevel;

    public AudioMonitorService(string friendlyName)
    {
        DeviceName = friendlyName;

        try
        {
            int deviceIndex = FindWaveInDeviceIndex(friendlyName);
            if (deviceIndex < 0)
            {
                Log.Warning("WaveIn Monitor: Could not find matching WaveIn device for {DeviceName}", friendlyName);
                return;
            }

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat   = new WaveFormat(rate: 44100, bits: 16, channels: 1),
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += (sender, args) =>
            {
                if (args.Exception != null)
                {
                    _lastException = args.Exception;
                    Log.Error(args.Exception, "WaveIn recording stopped unexpectedly for {DeviceName}", friendlyName);
                }
            };
            
            _waveIn.StartRecording();
            Log.Information("WaveIn Monitor started for device: {DeviceName} (Index: {Index})", friendlyName, deviceIndex);
        }
        catch (Exception ex)
        {
            _lastException = ex;
            Log.Error(ex, "Failed to start WaveIn audio monitor for {DeviceName}", friendlyName);
        }
    }

    private static int FindWaveInDeviceIndex(string friendlyName)
    {
        int count = WaveIn.DeviceCount;
        if (count == 0) return -1;

        // 1. Exact or partial name match
        for (int i = 0; i < count; i++)
        {
            try
            {
                WaveInCapabilities cap = WaveIn.GetCapabilities(i);
                string capName = cap.ProductName;

                if (friendlyName.Contains(capName, StringComparison.OrdinalIgnoreCase)
                    || capName.Contains(friendlyName[..Math.Min(friendlyName.Length, 15)], StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Failed to get WaveIn capabilities for index {Index}", i);
            }
        }

        // 2. Keyword match (e.g. matching "PD200X", "G435")
        var words = friendlyName.Split(new[] { ' ', '(', ')', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length >= 3 && w.Any(char.IsLetterOrDigit));
        foreach (var word in words)
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (WaveIn.GetCapabilities(i).ProductName.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
                catch
                {
                    // Ignore capabilities errors
                }
            }
        }

        // 3. Fallback to 0 (default system device index) if nothing matches
        Log.Warning("WaveIn Monitor: No match found for {DeviceName}. Falling back to default device index 0.", friendlyName);
        return 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float peak = 0f;

        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = Math.Abs(sample / 32768f);
            if (normalized > peak)
                peak = normalized;
        }

        _currentPeakLevel = peak;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Exception disposing WaveIn for {DeviceName}", DeviceName);
        }

        _disposed = true;
    }
}
