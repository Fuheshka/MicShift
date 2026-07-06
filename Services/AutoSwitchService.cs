using Serilog;

namespace MicShift;

public sealed class AutoSwitchService : IDisposable
{
    private readonly IAudioDeviceSwitcher _switcher;
    private string _deskName;
    private string _headsetName;
    private AudioMonitorService? _deskMonitor;
    private AudioMonitorService? _headsetMonitor;
    private CancellationTokenSource? _cts;
    private volatile bool _isAutoSwitchLogicActive;
    private volatile bool _isMonitorsActive;

    public bool IsRunning => _isAutoSwitchLogicActive;

    public float DeskPeakLevel => _deskMonitor?.CurrentPeakLevel ?? 0f;
    public float HeadsetPeakLevel => _headsetMonitor?.CurrentPeakLevel ?? 0f;

    /// <summary>
    /// Creates the service. Does NOT start monitors automatically — call UpdateMicrophones() explicitly.
    /// </summary>
    public AutoSwitchService(IAudioDeviceSwitcher switcher, string deskName, string headsetName)
    {
        _switcher = switcher;
        _deskName = deskName;
        _headsetName = headsetName;
        // NOTE: intentionally NOT calling StartMonitors() here.
        // The UI (MainWindow) is responsible for calling UpdateMicrophones() once it is ready,
        // to avoid a double-start race where App.OnStartup and OnWindowLoaded both start monitors.
    }

    /// <summary>
    /// Restarts monitors with the new device names. Safe to call multiple times.
    /// </summary>
    public void UpdateMicrophones(string deskName, string headsetName)
    {
        StopMonitors();
        _deskName = deskName;
        _headsetName = headsetName;
        StartMonitors();
    }

    public void Start()
    {
        _isAutoSwitchLogicActive = true;
        Log.Information("AutoSwitch logic enabled.");
    }

    public void Stop()
    {
        _isAutoSwitchLogicActive = false;
        Log.Information("AutoSwitch logic disabled.");
    }

    private void StartMonitors()
    {
        if (_isMonitorsActive) return;

        if (string.IsNullOrEmpty(_deskName) || string.IsNullOrEmpty(_headsetName))
        {
            Log.Warning("AutoSwitch: Microphone names are not configured. Monitors not started.");
            return;
        }

        try
        {
            // Resolve endpoint IDs through the switcher so the COM monitor
            // can find the exact WASAPI device without any name-matching hacks.
            var mics = _switcher.GetActiveMicrophones();
            var deskDevice    = mics.FirstOrDefault(m => m.Name.Equals(_deskName, StringComparison.OrdinalIgnoreCase));
            var headsetDevice = mics.FirstOrDefault(m => m.Name.Equals(_headsetName, StringComparison.OrdinalIgnoreCase));

            if (deskDevice == null || headsetDevice == null)
            {
                Log.Warning("AutoSwitch: Could not find one or both devices by name. Desk={DeskFound}, Headset={HeadsetFound}",
                    deskDevice != null, headsetDevice != null);
                return;
            }

            if (string.IsNullOrEmpty(deskDevice.EndpointId) || string.IsNullOrEmpty(headsetDevice.EndpointId))
            {
                Log.Warning("AutoSwitch: EndpointId is empty for one or both devices.");
                return;
            }

            _deskMonitor    = new AudioMonitorService(deskDevice.EndpointId, deskDevice.Name);
            _headsetMonitor = new AudioMonitorService(headsetDevice.EndpointId, headsetDevice.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to instantiate audio monitors.");
            StopMonitors();
            return;
        }

        _cts = new CancellationTokenSource();
        _isMonitorsActive = true;

        // Kick off the read loop on a background thread.
        Task.Run(() => RunAutoSwitchLoopAsync(_cts.Token));
        Log.Information("AutoSwitch audio monitors started.");
    }

    private void StopMonitors()
    {
        if (!_isMonitorsActive) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _deskMonitor?.Dispose();
        _headsetMonitor?.Dispose();
        _deskMonitor    = null;
        _headsetMonitor = null;

        _isMonitorsActive = false;
        Log.Information("AutoSwitch audio monitors stopped.");
    }

    private async Task RunAutoSwitchLoopAsync(CancellationToken token)
    {
        const float silenceThreshold           = 0.05f;  // 5% volume floor (ignores ambient noise)
        const float switchRatio                 = 2.5f;   // device must be 2.5× louder than the other
        const int   requiredConsecutiveSamples  = 15;     // ~500ms of consistent difference

        int deskWinsCount    = 0;
        int headsetWinsCount = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Monitors may have been replaced — capture references atomically.
                var deskMon    = _deskMonitor;
                var headsetMon = _headsetMonitor;

                if (deskMon == null || headsetMon == null)
                {
                    // Monitors not ready yet — just wait.
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }

                // Bug #4 fix: log the error but CONTINUE the loop instead of breaking.
                if (deskMon.LastException != null)
                {
                    Log.Warning("Desk monitor has an error, skipping this tick: {Msg}", deskMon.LastException.Message);
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }
                if (headsetMon.LastException != null)
                {
                    Log.Warning("Headset monitor has an error, skipping this tick: {Msg}", headsetMon.LastException.Message);
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                float deskLevel    = deskMon.CurrentPeakLevel;
                float headsetLevel = headsetMon.CurrentPeakLevel;

                // Auto-switching logic (only when the user has enabled it).
                if (_isAutoSwitchLogicActive)
                {
                    if (deskLevel > silenceThreshold || headsetLevel > silenceThreshold)
                    {
                        if (deskLevel > headsetLevel * switchRatio)
                        {
                            deskWinsCount++;
                            headsetWinsCount = 0;
                        }
                        else if (headsetLevel > deskLevel * switchRatio)
                        {
                            headsetWinsCount++;
                            deskWinsCount = 0;
                        }
                        else
                        {
                            if (deskWinsCount    > 0) deskWinsCount--;
                            if (headsetWinsCount > 0) headsetWinsCount--;
                        }

                        if (deskWinsCount >= requiredConsecutiveSamples)
                        {
                            deskWinsCount = 0;
                            await SwitchIfNecessaryAsync(_deskName).ConfigureAwait(false);
                        }
                        else if (headsetWinsCount >= requiredConsecutiveSamples)
                        {
                            headsetWinsCount = 0;
                            await SwitchIfNecessaryAsync(_headsetName).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (deskWinsCount    > 0) deskWinsCount--;
                        if (headsetWinsCount > 0) headsetWinsCount--;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in AutoSwitch loop — continuing.");
            }

            try
            {
                await Task.Delay(33, token).ConfigureAwait(false); // ~30 Hz
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.Debug("RunAutoSwitchLoopAsync exited.");
    }

    private async Task SwitchIfNecessaryAsync(string targetDeviceName)
    {
        try
        {
            var mics           = _switcher.GetActiveMicrophones();
            var currentDefault = mics.FirstOrDefault(m => m.IsDefaultCommunications);

            if (currentDefault != null && currentDefault.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase))
                return;

            var target = mics.FirstOrDefault(m => m.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                bool success = await _switcher.SetDefaultCommunicationsMicrophoneAsync(target.Id).ConfigureAwait(false);
                if (success)
                {
                    Log.Information("AutoSwitch: Switched default microphone to {DeviceName}", target.Name);
                    NotificationManager.Show("Auto-Switch", $"Switched to:\n{target.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoSwitch: Failed to switch default microphone.");
        }
    }

    public void Dispose()
    {
        StopMonitors();
    }
}
