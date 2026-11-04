using Layal.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AudioDeviceSettingsTests
{
    [TestMethod]
    public void NewSettings_DefaultToWindowsManagedAudioEndpoints()
    {
        var settings = new UserSettings();

        Assert.AreEqual(AudioDeviceDefault, settings.AudioInputDeviceId);
        Assert.AreEqual(AudioDeviceDefault, settings.AudioOutputDeviceId);
    }

    [TestMethod]
    public void CloneAndCopyFrom_PreserveSelectedInputAndOutputDevices()
    {
        var settings = new UserSettings
        {
            AudioInputDeviceId = "winmm:microphone",
            AudioOutputDeviceId = "winmm:speaker"
        };

        var clone = settings.Clone();
        Assert.AreEqual("winmm:microphone", clone.AudioInputDeviceId);
        Assert.AreEqual("winmm:speaker", clone.AudioOutputDeviceId);

        var target = new UserSettings();
        target.CopyFrom(settings);
        Assert.AreEqual("winmm:microphone", target.AudioInputDeviceId);
        Assert.AreEqual("winmm:speaker", target.AudioOutputDeviceId);
    }

    private const string AudioDeviceDefault = "windows-default";
}
