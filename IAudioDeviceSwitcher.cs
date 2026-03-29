namespace MicShift;

/// <summary>
/// Immutable snapshot of an audio capture device at the time of query.
/// </summary>
public sealed record AudioDeviceInfo(Guid Id, string Name, bool IsDefaultCommunications);

/// <summary>
/// Contract for listing and switching the default communications microphone.
/// </summary>
public interface IAudioDeviceSwitcher
{
    /// <summary>
    /// Returns all currently active (plugged-in, enabled) capture devices.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetActiveMicrophones();

    /// <summary>
    /// Sets the device identified by <paramref name="deviceId"/> as the default
    /// communications microphone.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the switch succeeded; <c>false</c> if the device was not
    /// found or the OS rejected the request.
    /// </returns>
    Task<bool> SetDefaultCommunicationsMicrophoneAsync(Guid deviceId);
}
