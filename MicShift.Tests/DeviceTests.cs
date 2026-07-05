using Moq;
using Xunit;
using MicShift;

namespace MicShift.Tests;

public class AudioDeviceTests
{
    [Fact]
    public void AudioDeviceInfo_RecordProperties_WorkCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var name = "Test Microphone";
        var isDefault = true;

        // Act
        var info = new AudioDeviceInfo(id, name, isDefault);

        // Assert
        Assert.Equal(id, info.Id);
        Assert.Equal(name, info.Name);
        Assert.True(info.IsDefaultCommunications);
    }

    [Fact]
    public void AudioDeviceInfo_RecordComparison_WorksCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var info1 = new AudioDeviceInfo(id, "Mic", false);
        var info2 = new AudioDeviceInfo(id, "Mic", false);

        // Assert
        Assert.Equal(info1, info2);
    }

    [Fact]
    public void MockSwitcher_GetActiveMicrophones_ReturnsConfiguredDevices()
    {
        // Arrange
        var mock = new Mock<IAudioDeviceSwitcher>();
        var devices = new List<AudioDeviceInfo>
        {
            new(Guid.NewGuid(), "Mic 1", true),
            new(Guid.NewGuid(), "Mic 2", false)
        };
        mock.Setup(x => x.GetActiveMicrophones()).Returns(devices);

        // Act
        var result = mock.Object.GetActiveMicrophones();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.Name == "Mic 1");
    }
}
