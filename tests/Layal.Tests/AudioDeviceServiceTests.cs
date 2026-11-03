using System;
using System.Collections.Generic;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AudioDeviceServiceTests
{
    [TestMethod]
    public void ResolveDeviceNumber_UsesWindowsDefaultWhenNoDeviceWasSaved()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(AudioDeviceService.WindowsDefaultDeviceId, "Windows default", -1),
            new("mic:usb", "USB microphone", 2)
        };

        Assert.AreEqual(-1, AudioDeviceService.ResolveDeviceNumber(null, devices, "microphone"));
        Assert.AreEqual(-1, AudioDeviceService.ResolveDeviceNumber(string.Empty, devices, "microphone"));
        Assert.AreEqual(-1, AudioDeviceService.ResolveDeviceNumber(
            AudioDeviceService.WindowsDefaultDeviceId, devices, "microphone"));
    }

    [TestMethod]
    public void ResolveDeviceNumber_UsesTheExactSavedDeviceInsteadOfTheFirstDevice()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(AudioDeviceService.WindowsDefaultDeviceId, "Windows default", -1),
            new("mic:built-in", "Built-in microphone", 0),
            new("mic:usb", "USB microphone", 2)
        };

        Assert.AreEqual(2, AudioDeviceService.ResolveDeviceNumber("mic:usb", devices, "microphone"));
    }

    [TestMethod]
    public void ResolveDeviceNumber_RejectsADeviceThatWasDisconnected()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(AudioDeviceService.WindowsDefaultDeviceId, "Windows default", -1),
            AudioDeviceService.CreateUnavailableSelection("mic:disconnected", isInput: true, isArabic: true)
        };

        var error = CaptureException<InvalidOperationException>(() =>
            AudioDeviceService.ResolveDeviceNumber("mic:disconnected", devices, "microphone"));

        StringAssert.Contains(error.Message, "not available");
    }

    [TestMethod]
    public void ResolveDeviceNumber_RejectsUnknownSavedIdsInsteadOfSilentlyChangingMicrophones()
    {
        var devices = new List<AudioDeviceOption>
        {
            new(AudioDeviceService.WindowsDefaultDeviceId, "Windows default", -1),
            new("mic:built-in", "Built-in microphone", 0)
        };

        CaptureException<InvalidOperationException>(() =>
            AudioDeviceService.ResolveDeviceNumber("mic:removed", devices, "microphone"));
    }

    [TestMethod]
    public void CreateUnavailableSelection_PreservesTheSavedIdButCannotBeOpened()
    {
        var unavailable = AudioDeviceService.CreateUnavailableSelection(
            "speaker:old", isInput: false, isArabic: false);

        Assert.AreEqual("speaker:old", unavailable.Id);
        Assert.IsFalse(unavailable.IsAvailable);
        Assert.AreEqual(-2, unavailable.DeviceNumber);
        StringAssert.Contains(unavailable.Name, "unavailable");
    }
    private static TException CaptureException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
        throw new AssertFailedException("Expected exception was not thrown.");
    }

}
