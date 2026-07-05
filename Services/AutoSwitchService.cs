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
    private bool _isAutoSwitchLogicActive;
    private bool _isMonitorsActive;

    public bool IsRunning => _isAutoSwitchLogicActive;

    public float DeskPeakLevel => _deskMonitor?.CurrentPeakLevel ?? 0f;
    public float HeadsetPeakLevel => _headsetMonitor?.CurrentPeakLevel ?? 0f;

    public AutoSwitchService(IAudioDeviceSwitcher switcher, string deskName, string headsetName)
    {
        _switcher = switcher;
        _deskName = deskName;
        _headsetName = headsetName;

        StartMonitors();
    }

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
            _deskMonitor = new AudioMonitorService(_deskName);
            _headsetMonitor = new AudioMonitorService(_headsetName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start audio monitors.");
            NotificationManager.Show("MicShift Error", "Failed to start audio monitors.");
            StopMonitors();
            return;
        }

        _cts = new CancellationTokenSource();
        _isMonitorsActive = true;

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
        _deskMonitor = null;
        _headsetMonitor = null;

        _isMonitorsActive = false;
        Log.Information("AutoSwitch audio monitors stopped.");
    }

    private async Task RunAutoSwitchLoopAsync(CancellationToken token)
    {
        const float silenceThreshold = 0.03f; // 3% volume floor
        const int requiredConsecutiveSamples = 6; // ~200ms of consistent volume difference
        
        int deskWinsCount = 0;
        int headsetWinsCount = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_deskMonitor == null || _headsetMonitor == null)
                    break;

                if (_deskMonitor.LastException != null)
                {
                    Log.Error(_deskMonitor.LastException, "Desk microphone error in AutoSwitchService.");
                    break;
                }
                if (_headsetMonitor.LastException != null)
                {
                    Log.Error(_headsetMonitor.LastException, "Headset microphone error in AutoSwitchService.");
                    break;
                }

                float deskLevel = _deskMonitor.CurrentPeakLevel;
                float headsetLevel = _headsetMonitor.CurrentPeakLevel;

                // Only perform auto-switching check if enabled
                if (_isAutoSwitchLogicActive)
                {
                    if (deskLevel > silenceThreshold || headsetLevel > silenceThreshold)
                    {
                        if (deskLevel > headsetLevel * 1.5f)
                        {
                            deskWinsCount++;
                            headsetWinsCount = 0;
                        }
                        else if (headsetLevel > deskLevel * 1.5f)
                        {
                            headsetWinsCount++;
                            deskWinsCount = 0;
                        }
                        else
                        {
                            if (deskWinsCount > 0) deskWinsCount--;
                            if (headsetWinsCount > 0) headsetWinsCount--;
                        }

                        if (deskWinsCount >= requiredConsecutiveSamples)
                        {
                            deskWinsCount = 0;
                            await SwitchIfNecessaryAsync(_deskName);
                        }
                        else if (headsetWinsCount >= requiredConsecutiveSamples)
                        {
                            headsetWinsCount = 0;
                            await SwitchIfNecessaryAsync(_headsetName);
                        }
                    }
                    else
                    {
                        if (deskWinsCount > 0) deskWinsCount--;
                        if (headsetWinsCount > 0) headsetWinsCount--;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in AutoSwitch loop execution.");
            }

            try { await Task.Delay(33, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SwitchIfNecessaryAsync(string targetDeviceName)
    {
        try
        {
            var mics = _switcher.GetActiveMicrophones();
            var currentDefault = mics.FirstOrDefault(m => m.IsDefaultCommunications);
            
            if (currentDefault != null && currentDefault.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var target = mics.FirstOrDefault(m => m.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                bool success = await _switcher.SetDefaultCommunicationsMicrophoneAsync(target.Id);
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
