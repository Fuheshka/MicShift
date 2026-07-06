using System.Runtime.InteropServices;
using Serilog;
using Timer = System.Threading.Timer;

namespace MicShift;

/// <summary>
/// Reads the real-time peak audio level for a single capture device using
/// the Windows Core Audio COM API (IAudioMeterInformation) directly.
/// 
/// This avoids NAudio entirely, preventing the COM RCW conflict
/// ("Unable to cast System.__ComObject to MMDeviceEnumeratorComObject")
/// that occurs when both AudioSwitcher and NAudio try to create
/// MMDeviceEnumerator COM objects in the same process.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    // ── COM Interop Definitions ──────────────────────────────────────────────

    /// <summary>
    /// Our own coclass for MMDeviceEnumerator.
    /// Using a unique .NET type name prevents RCW cache collisions with AudioSwitcher.
    /// </summary>
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MicShiftDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out int pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(int u32ChannelCount,
                                                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out int pdwHardwareSupportMask);
    }

    private const int CLSCTX_ALL = 23; // CLSCTX_INPROC_SERVER | HANDLER | LOCAL | REMOTE

    // ── Instance State ───────────────────────────────────────────────────────

    private object? _enumeratorRef;   // prevent GC of the enumerator RCW
    private object? _deviceRef;       // prevent GC of the device RCW
    private IAudioMeterInformation? _meter;
    private Timer? _pollTimer;
    private volatile float _currentPeakLevel;
    private volatile bool _disposed;

    public string DeviceName { get; }
    public Exception? LastException { get; private set; }
    public float CurrentPeakLevel => _currentPeakLevel;

    /// <summary>
    /// Creates a peak meter for the device with the given Windows endpoint ID.
    /// </summary>
    /// <param name="endpointId">
    /// Windows endpoint ID string (e.g. "{0.0.1.00000000}.{guid}"),
    /// obtained from <see cref="AudioDeviceInfo.EndpointId"/>.
    /// </param>
    /// <param name="friendlyName">Human-readable device name for logging.</param>
    public AudioMonitorService(string endpointId, string friendlyName)
    {
        DeviceName = friendlyName;

        try
        {
            // 1. Create our own MMDeviceEnumerator COM object (unique .NET type → no RCW conflict).
            var enumerator = (IMMDeviceEnumerator)new MicShiftDeviceEnumerator();
            _enumeratorRef = enumerator;

            // 2. Get the device by its endpoint ID.
            int hr = enumerator.GetDevice(endpointId, out IMMDevice device);
            Marshal.ThrowExceptionForHR(hr);
            _deviceRef = device;

            // 3. Activate IAudioMeterInformation on the device.
            Guid iid = typeof(IAudioMeterInformation).GUID;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object meterObj);
            Marshal.ThrowExceptionForHR(hr);
            _meter = (IAudioMeterInformation)meterObj;

            // 4. Start polling at ~30 Hz.
            _pollTimer = new Timer(_ => PollPeakValue(), null, 0, 33);

            Log.Information("COM Peak Meter started for {Device} (EndpointId: {Id})", friendlyName, endpointId);
        }
        catch (Exception ex)
        {
            LastException = ex;
            Log.Error(ex, "Failed to start COM peak meter for {Device} (EndpointId: {Id})", friendlyName, endpointId);
        }
    }

    private void PollPeakValue()
    {
        if (_disposed) return;

        try
        {
            if (_meter != null)
            {
                int hr = _meter.GetPeakValue(out float peak);
                if (hr == 0)
                {
                    _currentPeakLevel = peak; // 0.0 – 1.0 from WASAPI
                }
            }
        }
        catch
        {
            // Swallow — timer will retry on next tick.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer?.Dispose();
        _pollTimer = null;

        try
        {
            if (_meter != null) { Marshal.ReleaseComObject(_meter); _meter = null; }
            if (_deviceRef != null) { Marshal.ReleaseComObject(_deviceRef); _deviceRef = null; }
            if (_enumeratorRef != null) { Marshal.ReleaseComObject(_enumeratorRef); _enumeratorRef = null; }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Exception releasing COM objects for peak meter {Device}", DeviceName);
        }
    }
}
