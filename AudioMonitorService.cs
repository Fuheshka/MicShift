using NAudio.Wave;

namespace MicShift;

/// <summary>
/// Streams audio from a single capture device and exposes a rolling peak level (0.0 – 1.0).
/// Does NOT record to disk — samples are discarded after peak measurement.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    private readonly WaveInEvent _waveIn;
    private volatile float _currentPeakLevel;
    private volatile Exception? _lastException;
    private bool _disposed;

    /// <summary>The friendly name of the device being monitored.</summary>
    public string DeviceName { get; }

    /// <summary>Current peak amplitude in the last audio buffer (0.0 – 1.0).</summary>
    public float CurrentPeakLevel => _currentPeakLevel;

    /// <summary>Any exception thrown by the underlying capture device (e.g. if disconnected).</summary>
    public Exception? LastException => _lastException;

    /// <param name="deviceFriendlyName">
    /// The friendly name of the capture device (as returned by
    /// <see cref="IAudioDeviceSwitcher.GetActiveMicrophones"/>).
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when no WaveIn device with the given name can be found.
    /// </exception>
    public AudioMonitorService(string deviceFriendlyName)
    {
        DeviceName = deviceFriendlyName;
        int deviceIndex = FindWaveInDeviceIndex(deviceFriendlyName);

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
            }
        };
        _waveIn.StartRecording();
    }

    private static int FindWaveInDeviceIndex(string friendlyName)
    {
        int count = WaveIn.DeviceCount;

        for (int i = 0; i < count; i++)
        {
            WaveInCapabilities cap = WaveIn.GetCapabilities(i);
            // WaveIn names are truncated to 31 chars by the Win32 API.
            if (friendlyName.Contains(cap.ProductName, StringComparison.OrdinalIgnoreCase)
                || cap.ProductName.Contains(friendlyName[..Math.Min(friendlyName.Length, 28)],
                                            StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentException(
            $"No WaveIn device matching \"{friendlyName}\" was found. " +
            $"Available devices: {string.Join(", ", Enumerable.Range(0, count).Select(i => WaveIn.GetCapabilities(i).ProductName))}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float peak = 0f;
        int sampleCount = e.BytesRecorded / 2; // 16-bit samples

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
        _waveIn.StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _disposed = true;
    }
}
