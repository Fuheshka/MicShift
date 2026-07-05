using NAudio.CoreAudioApi;
using Serilog;

namespace MicShift;

/// <summary>
/// Streams peak volume levels directly from Windows Core Audio WASAPI (AudioMeterInformation)
/// without opening a recording stream. Bulletproof and uses 0% CPU.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    private readonly MMDevice? _device;
    private readonly MMDeviceEnumerator _enumerator;
    private bool _disposed;

    public string DeviceName { get; }
    public Exception? LastException { get; }

    public AudioMonitorService(string friendlyName)
    {
        DeviceName = friendlyName;
        _enumerator = new MMDeviceEnumerator();
        
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            // Search for exact match
            _device = devices.FirstOrDefault(d => d.FriendlyName.Equals(friendlyName, StringComparison.OrdinalIgnoreCase));
            
            // Fallback: partial match if name got truncated
            if (_device == null)
            {
                _device = devices.FirstOrDefault(d => d.FriendlyName.Contains(friendlyName, StringComparison.OrdinalIgnoreCase)
                    || friendlyName.Contains(d.FriendlyName, StringComparison.OrdinalIgnoreCase));
            }

            if (_device == null)
            {
                Log.Warning("WASAPI Monitor: Could not find active device matching {DeviceName}", friendlyName);
            }
            else
            {
                Log.Information("WASAPI Monitor started for device: {FriendlyName}", _device.FriendlyName);
            }
        }
        catch (Exception ex)
        {
            LastException = ex;
            Log.Error(ex, "Failed to initialize WASAPI volume monitor for {DeviceName}", friendlyName);
        }
    }

    /// <summary>
    /// Current peak volume (0.0 to 1.0)
    /// </summary>
    public float CurrentPeakLevel
    {
        get
        {
            if (_disposed || _device == null) return 0f;
            try
            {
                // MasterPeakValue is updated by Windows Audio service in real-time
                return _device.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Failed to read peak value from device {DeviceName}", DeviceName);
                return 0f;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _device?.Dispose();
        _enumerator.Dispose();
        _disposed = true;
    }
}
