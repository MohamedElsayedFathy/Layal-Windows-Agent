using System;
using System.IO;
using Layal;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaModelStoragePathTests
{
    [TestMethod]
    public void CustomLayalResourceFolderOwnsTheOllamaModelStore()
    {
        using var temp = new TestTempDirectory();
        var previousSettings = AppRuntime.Settings;
        var previousEnvironment = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", null);
            AppRuntime.Settings = new UserSettings
            {
                LocalAiDataDirectory = Path.Combine(temp.Path, "LayalResources")
            };

            var expected = Path.Combine(
                Path.GetFullPath(AppRuntime.Settings.LocalAiDataDirectory), "ollama-models");
            Assert.AreEqual(expected, OllamaLocalModelSetupService.ModelsDirectory);
        }
        finally
        {
            AppRuntime.Settings = previousSettings;
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", previousEnvironment);
        }
    }

    [TestMethod]
    public void ExplicitSavedOllamaModelsDirectoryTakesPrecedenceForResourceRelocation()
    {
        using var temp = new TestTempDirectory();
        var previousSettings = AppRuntime.Settings;
        var previousEnvironment = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", Path.Combine(temp.Path, "legacy-models"));
            var selected = Path.Combine(temp.Path, "LayalResources");
            var savedModels = Path.Combine(selected, "ollama-models");
            AppRuntime.Settings = new UserSettings
            {
                LocalAiDataDirectory = selected,
                OllamaModelsDirectory = savedModels
            };

            Assert.AreEqual(Path.GetFullPath(savedModels), OllamaLocalModelSetupService.ModelsDirectory);
        }
        finally
        {
            AppRuntime.Settings = previousSettings;
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", previousEnvironment);
        }
    }

    [TestMethod]
    public void ExplicitOllamaModelsEnvironmentIsPreservedWithoutCustomLayalFolder()
    {
        using var temp = new TestTempDirectory();
        var previousSettings = AppRuntime.Settings;
        var previousEnvironment = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        try
        {
            var configured = Path.Combine(temp.Path, "custom-ollama-models");
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", configured);
            AppRuntime.Settings = new UserSettings();
            Assert.AreEqual(Path.GetFullPath(configured), OllamaLocalModelSetupService.ModelsDirectory);
        }
        finally
        {
            AppRuntime.Settings = previousSettings;
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", previousEnvironment);
        }
    }

    [TestMethod]
    public void DefaultModelManifestIsDetectedInsideTheEffectiveModelStore()
    {
        using var temp = new TestTempDirectory();
        var previousSettings = AppRuntime.Settings;
        var previousEnvironment = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        try
        {
            AppRuntime.Settings = new UserSettings
            {
                LocalAiDataDirectory = Path.Combine(temp.Path, "LayalResources")
            };
            var manifest = Path.Combine(
                OllamaLocalModelSetupService.ModelsDirectory,
                "manifests", "registry.ollama.ai", "library", "qwen3.5", "2b");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            Assert.IsFalse(OllamaLocalModelSetupService.HasDefaultModelManifest());
            File.WriteAllText(manifest, """{"schemaVersion":2}""");
            Assert.IsTrue(OllamaLocalModelSetupService.HasDefaultModelManifest());
        }
        finally
        {
            AppRuntime.Settings = previousSettings;
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", previousEnvironment);
        }
    }
}
