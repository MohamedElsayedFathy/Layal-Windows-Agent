using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void MissingFileReturnsSafeDefaults()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual("http://127.0.0.1:31415/v1", settings.ProviderBaseUrl);
        Assert.AreEqual("auto", settings.ModelId);
        Assert.IsTrue(settings.MascotAlwaysOnTop);
        Assert.IsTrue(settings.MascotAnimationsEnabled);
    }

    [TestMethod]
    public void LoadNormalizesInvalidValuesAndClampsMascotSettings()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"),
            "{\"Language\":\"fr\",\"ProviderBaseUrl\":\" \",\"ModelId\":\"\",\"MascotScale\":12,\"MascotOpacity\":-2}");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual("http://127.0.0.1:31415/v1", settings.ProviderBaseUrl);
        Assert.AreEqual("auto", settings.ModelId);
        Assert.AreEqual(1.8, settings.MascotScale, 0.0001);
        Assert.AreEqual(0.35, settings.MascotOpacity, 0.0001);
    }

    [TestMethod]
    public void SaveAndLoadRoundTripPreservesValidSettings()
    {
        using var temp = new TestTempDirectory();
        var store = new SettingsStore(temp.Path);
        store.Save(new UserSettings
        {
            Language = "en",
            ThemeMode = "dark",
            ProviderBaseUrl = "https://provider.example/v1",
            ModelId = "test-model",
            MascotAlwaysOnTop = false,
            MascotAnimationsEnabled = false,
            MascotScale = 1.25,
            MascotOpacity = 0.8,
            MascotLeft = 1284.5,
            MascotTop = 424.25
        });

        var reloaded = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("en", reloaded.Language);
        Assert.AreEqual("dark", reloaded.ThemeMode);
        Assert.AreEqual("https://provider.example/v1", reloaded.ProviderBaseUrl);
        Assert.AreEqual("test-model", reloaded.ModelId);
        Assert.IsFalse(reloaded.MascotAlwaysOnTop);
        Assert.IsFalse(reloaded.MascotAnimationsEnabled);
        Assert.AreEqual(1.25, reloaded.MascotScale, 0.0001);
        Assert.AreEqual(0.8, reloaded.MascotOpacity, 0.0001);
        Assert.AreEqual(1284.5, reloaded.MascotLeft, 0.0001);
        Assert.AreEqual(424.25, reloaded.MascotTop, 0.0001);
    }

    [TestMethod]
    public void DamagedSettingsDoNotPreventStartup()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "settings.json"), "{ this is not valid json");

        var settings = new SettingsStore(temp.Path).Load();

        Assert.AreEqual("ar", settings.Language);
        Assert.AreEqual("auto", settings.ModelId);
    }
}
