using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace Layal.Services;

/// <summary>A Windows audio endpoint exposed by NAudio's WinMM device catalog.</summary>
public sealed record AudioDeviceOption(
    string Id,
    string Name,
    int DeviceNumber,
    bool IsAvailable = true);

/// <summary>
/// Enumerates selectable Windows input/output devices and resolves saved device IDs without
/// silently falling back to a different physical microphone or speaker.
/// </summary>
public static class AudioDeviceService
{
    public const string WindowsDefaultDeviceId = "windows-default";

    public static IReadOnlyList<AudioDeviceOption> GetInputDevices(bool isArabic = true)
    {
        var devices = new List<AudioDeviceOption>
        {
            new(WindowsDefaultDeviceId,
                isArabic ? "الميكروفون الافتراضي في Windows" : "Windows default microphone",
                -1)
        };
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var inputCount = 0;
        try { inputCount = WaveIn.DeviceCount; } catch { return devices; }
        for (var index = 0; index < inputCount; index++)
        {
            try
            {
                var capabilities = WaveIn.GetCapabilities(index);
                AddDevice(devices, usedIds, capabilities.ProductGuid, capabilities.ManufacturerGuid, capabilities.NameGuid, capabilities.ProductName, index,
                    isArabic ? "ميكروفون" : "Microphone");
            }
            catch
            {
                // One disconnected or driver-broken endpoint must not hide other audio devices.
            }
        }
        return devices;
    }

    public static IReadOnlyList<AudioDeviceOption> GetOutputDevices(bool isArabic = true)
    {
        var devices = new List<AudioDeviceOption>
        {
            new(WindowsDefaultDeviceId,
                isArabic ? "مخرج الصوت الافتراضي في Windows" : "Windows default output",
                -1)
        };
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var outputCount = 0;
        try { outputCount = WaveOut.DeviceCount; } catch { return devices; }
        for (var index = 0; index < outputCount; index++)
        {
            try
            {
                var capabilities = WaveOut.GetCapabilities(index);
                AddDevice(devices, usedIds, capabilities.ProductGuid, capabilities.ManufacturerGuid, capabilities.NameGuid, capabilities.ProductName, index,
                    isArabic ? "مخرج صوت" : "Audio output");
            }
            catch
            {
                // Keep enumerating when an individual endpoint disappears during refresh.
            }
        }
        return devices;
    }

    public static AudioDeviceOption CreateUnavailableSelection(string? id, bool isInput, bool isArabic)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            string.Equals(id, WindowsDefaultDeviceId, StringComparison.Ordinal))
            throw new ArgumentException("A non-default saved audio device ID is required.", nameof(id));

        return new AudioDeviceOption(
            id,
            isArabic
                ? (isInput ? "الميكروفون المحفوظ غير متاح — اختر جهازًا آخر" : "مخرج الصوت المحفوظ غير متاح — اختر جهازًا آخر")
                : (isInput ? "Saved microphone unavailable — choose another device" : "Saved output unavailable — choose another device"),
            -2,
            IsAvailable: false);
    }

    public static int ResolveInputDeviceNumber(string? selectedId) =>
        ResolveDeviceNumber(selectedId, GetInputDevices(), "microphone");

    public static int ResolveOutputDeviceNumber(string? selectedId) =>
        ResolveDeviceNumber(selectedId, GetOutputDevices(), "audio output");

    /// <summary>Pure resolver used by the Windows capture/playback paths and hardware-free tests.</summary>
    public static int ResolveDeviceNumber(
        string? selectedId,
        IReadOnlyList<AudioDeviceOption> availableDevices,
        string deviceKind)
    {
        ArgumentNullException.ThrowIfNull(availableDevices);
        if (string.IsNullOrWhiteSpace(selectedId) ||
            string.Equals(selectedId.Trim(), WindowsDefaultDeviceId, StringComparison.Ordinal))
            return -1;

        var selected = availableDevices.FirstOrDefault(device =>
            string.Equals(device.Id, selectedId.Trim(), StringComparison.Ordinal));
        if (selected is null || !selected.IsAvailable || selected.DeviceNumber < 0)
            throw new InvalidOperationException(
                $"The selected {deviceKind} is not available. Refresh audio devices and choose an available device or the Windows default.");

        return selected.DeviceNumber;
    }

    private static void AddDevice(
        ICollection<AudioDeviceOption> devices,
        ISet<string> usedIds,
        Guid productGuid,
        Guid manufacturerGuid,
        Guid nameGuid,
        string? productName,
        int deviceNumber,
        string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? fallbackName : productName.Trim();
        var hasHardwareIdentity = productGuid != Guid.Empty || manufacturerGuid != Guid.Empty || nameGuid != Guid.Empty;
        var hardwareIdentity = string.Join(":",
            manufacturerGuid.ToString("N"),
            productGuid.ToString("N"),
            nameGuid.ToString("N"));
        var idBase = "winmm:" + (hasHardwareIdentity ? hardwareIdentity : "index-" + deviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ":" + Uri.EscapeDataString(name);
        var id = idBase;
        if (!usedIds.Add(id))
        {
            id = idBase + ":" + deviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            usedIds.Add(id);
        }
        devices.Add(new AudioDeviceOption(id, name, deviceNumber));
    }
}
