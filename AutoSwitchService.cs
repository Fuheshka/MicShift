using Serilog;

namespace MicShift;

public sealed class AutoSwitchService : IDisposable
{
    private readonly IAudioDeviceSwitcher _switcher;
    private readonly string _deskName;
    private readonly string _headsetName;
    private AudioMonitorService? _deskMonitor;
    private AudioMonitorService? _headsetMonitor;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public AutoSwitchService(IAudioDeviceSwitcher switcher, string deskName, string headsetName)
    {
        _switcher = switcher;
        _deskName = deskName;
        _headsetName = headsetName;
    }

    public void Start()
    {
        if (_isRunning) return;

        if (string.IsNullOrEmpty(_deskName) || string.IsNullOrEmpty(_headsetName))
        {
            Log.Warning("AutoSwitch: Microphone names are not configured. Cannot start service.");
            return;
        }

        try
        {
            _deskMonitor = new AudioMonitorService(_deskName);
            _headsetMonitor = new AudioMonitorService(_headsetName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start audio monitors for AutoSwitchService.");
            NotificationManager.Show("MicShift Error", "Failed to start audio monitors for auto-switching.");
            Stop();
            return;
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;

        Task.Run(() => RunAutoSwitchLoopAsync(_cts.Token));
        Log.Information("AutoSwitchService started.");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _deskMonitor?.Dispose();
        _headsetMonitor?.Dispose();
        _deskMonitor = null;
        _headsetMonitor = null;

        _isRunning = false;
        Log.Information("AutoSwitchService stopped.");
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

                // Only evaluate if at least one microphone detects sound above the silence floor
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
                        // Levels are too close, decay wins slowly to avoid switching on momentary spikes
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
                    // Ambient silence, cool down win counters
                    if (deskWinsCount > 0) deskWinsCount--;
                    if (headsetWinsCount > 0) headsetWinsCount--;
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
                return; // Device is already active/default
            }

            var target = mics.FirstOrDefault(m => m.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                bool success = await _switcher.SetDefaultCommunicationsMicrophoneAsync(target.Id);
                if (success)
                {
                    Log.Information("AutoSwitch: Switched default microphone to {DeviceName}", target.Name);
                    NotificationManager.Show("MicShift Auto-Switch", $"Switched to: {target.Name}");
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
        Stop();
    }
}
