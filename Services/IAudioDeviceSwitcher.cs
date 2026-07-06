namespace MicShift;

/// <summary>
/// Immutable snapshot of an audio capture device at the time of query.
/// </summary>
public sealed record AudioDeviceInfo(Guid Id, string Name, bool IsDefaultCommunications, string EndpointId = "");

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

    /// <summary>
    /// Toggles the mute state of the current default communications microphone.
    /// </summary>
    /// <param name="deviceName">Returns the friendly name of the toggled device.</param>
    /// <param name="isMuted">Returns the new mute state (true if muted, false if unmuted).</param>
    /// <returns>true if the toggle was successful; false if no default communications device exists.</returns>
    bool ToggleDefaultMicrophoneMute(out string deviceName, out bool isMuted);

    /// <summary>
    /// Cycles the default communications microphone to the next active microphone in the list.
    /// </summary>
    /// <returns>The new default microphone info, or null if cycle was not possible.</returns>
    Task<AudioDeviceInfo?> CycleDefaultCommunicationsMicrophoneAsync();
}
