using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace MicShift;

/// <summary>
/// Windows implementation of <see cref="IAudioDeviceSwitcher"/> backed by
/// the Windows Core Audio API via AudioSwitcher.AudioApi.CoreAudio.
/// </summary>
public sealed class WindowsAudioDeviceSwitcher : IAudioDeviceSwitcher, IDisposable
{
    private readonly CoreAudioController _controller = new();
    private bool _disposed;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> GetActiveMicrophones()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return _controller
                .GetCaptureDevices(DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.Id, d.FullName, d.IsDefaultCommunicationsDevice))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to enumerate audio capture devices.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetDefaultCommunicationsMicrophoneAsync(Guid deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CoreAudioDevice? device;

        try
        {
            device = _controller
                .GetCaptureDevices(DeviceState.Active)
                .FirstOrDefault(d => d.Id == deviceId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to query audio devices before switching.", ex);
        }

        if (device is null)
            return false;

        try
        {
            // Set both roles: multimedia default AND communications default.
            bool defaultOk = await device.SetAsDefaultAsync();
            bool commsOk   = await device.SetAsDefaultCommunicationsAsync();
            return defaultOk && commsOk;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The OS rejected the request to set \"{device.FullName}\" as the default device.",
                ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _controller.Dispose();
        _disposed = true;
    }
}
