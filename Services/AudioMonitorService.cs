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

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out int numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out int numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    private const int CLSCTX_ALL = 23; // CLSCTX_INPROC_SERVER | HANDLER | LOCAL | REMOTE

    // ── Instance State ───────────────────────────────────────────────────────

    private object? _enumeratorRef;   // prevent GC of the enumerator RCW
    private object? _deviceRef;       // prevent GC of the device RCW
    private object? _audioClientRef;  // dummy capture stream to wake up the mic
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
            // 1. Create MMDeviceEnumerator directly via Activator to avoid RCW type-casting conflicts with AudioSwitcher.
            Type enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                ?? throw new InvalidOperationException("MMDeviceEnumerator COM type not found.");
            var enumeratorObj = Activator.CreateInstance(enumeratorType) 
                ?? throw new InvalidOperationException("Failed to create MMDeviceEnumerator.");
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
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

            // 4. Wake up the microphone by starting a dummy shared capture stream.
            // Without this, WASAPI IAudioMeterInformation returns exactly 0.0 if no other app is recording.
            Guid audioClientIid = typeof(IAudioClient).GUID;
            hr = device.Activate(ref audioClientIid, CLSCTX_ALL, IntPtr.Zero, out object audioClientObj);
            if (hr == 0 && audioClientObj is IAudioClient audioClient)
            {
                _audioClientRef = audioClient;
                hr = audioClient.GetMixFormat(out IntPtr waveFormatEx);
                if (hr == 0)
                {
                    // AUDCLNT_SHAREMODE_SHARED = 0
                    hr = audioClient.Initialize(0, 0, 10000000, 0, waveFormatEx, IntPtr.Zero);
                    if (hr == 0)
                    {
                        audioClient.Start();
                    }
                    Marshal.FreeCoTaskMem(waveFormatEx);
                }
            }

            // 5. Start polling at ~30 Hz.
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
            if (_audioClientRef is IAudioClient client)
            {
                client.Stop();
                Marshal.ReleaseComObject(_audioClientRef);
                _audioClientRef = null;
            }
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
