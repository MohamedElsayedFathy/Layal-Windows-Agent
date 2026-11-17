using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalAiResourceDataMigrationServiceTests
{
    [TestMethod]
    public async Task CopiesAndVerifiesLayalAndOllamaFoldersWithoutDeletingSources()
    {
        using var temp = new TestTempDirectory();
        var localAi = Path.Combine(temp.Path, "source", "LocalAi");
        var ollama = Path.Combine(temp.Path, "source", "OllamaModels");
        var destination = Path.Combine(temp.Path, "destination", "Resources");
        Directory.CreateDirectory(localAi);
        Directory.CreateDirectory(ollama);
        await File.WriteAllTextAsync(Path.Combine(localAi, "whisper.bin"), "whisper-model");
        Directory.CreateDirectory(Path.Combine(localAi, "hf-cache", "blobs"));
        await File.WriteAllTextAsync(Path.Combine(localAi, "hf-cache", "blobs", "voice.bin"), "voice-model");
        Directory.CreateDirectory(Path.Combine(ollama, "manifests", "registry.ollama.ai", "library", "qwen3.5"));
        await File.WriteAllTextAsync(
            Path.Combine(ollama, "manifests", "registry.ollama.ai", "library", "qwen3.5", "2b"),
            """{"schemaVersion":2}""");
        await File.WriteAllTextAsync(Path.Combine(ollama, ".layal-server-lease.json"), """{"ProcessId":1}""");
        await File.WriteAllTextAsync(Path.Combine(localAi, "resource-state.json"), """{"Resources":{}}""");
        Directory.CreateDirectory(Path.Combine(localAi, "python-runtime", "3.13.17"));
        await File.WriteAllTextAsync(Path.Combine(localAi, "python-runtime", "3.13.17", "python.exe"), "base-python");
        Directory.CreateDirectory(Path.Combine(localAi, "voicetut", "venv", "Scripts"));
        await File.WriteAllTextAsync(Path.Combine(localAi, "voicetut", "venv", "Scripts", "python.exe"), "path-bound-venv");
        await File.WriteAllTextAsync(Path.Combine(localAi, "voicetut", "voicetut-install.json"), "install-marker");
        await File.WriteAllTextAsync(Path.Combine(localAi, "voicetut", "python-packages.freeze.txt"), "torch==2.8.0");

        var result = await LocalAiResourceDataMigrationService.MigrateAsync(
            new[]
            {
                new LocalAiResourceMigrationSource(localAi, "."),
                new LocalAiResourceMigrationSource(ollama, "ollama-models")
            },
            destination);

        Assert.AreEqual(3, result.CopiedFiles);
        Assert.IsTrue(result.CopiedBytes > 0);
        Assert.AreEqual("whisper-model",
            await File.ReadAllTextAsync(Path.Combine(destination, "whisper.bin")));
        Assert.AreEqual("voice-model",
            await File.ReadAllTextAsync(Path.Combine(destination, "hf-cache", "blobs", "voice.bin")));
        Assert.AreEqual(
            """{"schemaVersion":2}""",
            await File.ReadAllTextAsync(Path.Combine(destination, "ollama-models", "manifests", "registry.ollama.ai", "library", "qwen3.5", "2b")));
        Assert.IsTrue(File.Exists(Path.Combine(localAi, "whisper.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(localAi, "resource-state.json")));
        Assert.IsTrue(File.Exists(Path.Combine(localAi, "python-runtime", "3.13.17", "python.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(localAi, "voicetut", "venv", "Scripts", "python.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(ollama, "manifests", "registry.ollama.ai", "library", "qwen3.5", "2b")));
        Assert.IsTrue(File.Exists(Path.Combine(ollama, ".layal-server-lease.json")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "ollama-models", ".layal-server-lease.json")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "resource-state.json")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "python-runtime", "3.13.17", "python.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "voicetut", "venv", "Scripts", "python.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "voicetut", "voicetut-install.json")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "voicetut", "python-packages.freeze.txt")));
    }

    [TestMethod]
    public async Task RefusesNonEmptyDestinationWithoutChangingIt()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "resource.dat"), "source");
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            LocalAiResourceDataMigrationService.MigrateAsync(source, destination));

        Assert.AreEqual("source", await File.ReadAllTextAsync(Path.Combine(source, "resource.dat")));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
    }

    [TestMethod]
    public async Task CancellationRemovesOnlyTheStagingCopyAndPreservesSource()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), new byte[1024 * 1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            LocalAiResourceDataMigrationService.MigrateAsync(source, destination, cancellationToken: cancellation.Token));

        Assert.IsTrue(File.Exists(Path.Combine(source, "large.bin")));
        Assert.IsFalse(Directory.Exists(destination));
        Assert.AreEqual(0, Directory.EnumerateDirectories(temp.Path)
            .Count(path => Path.GetFileName(path).Contains(".layal-migration-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RejectsOverlappingSourcesAndDestinations()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var nested = Path.Combine(source, "nested");
        var destination = Path.Combine(temp.Path, "destination");
        Directory.CreateDirectory(nested);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            LocalAiResourceDataMigrationService.MigrateAsync(
                new[]
                {
                    new LocalAiResourceMigrationSource(source, "."),
                    new LocalAiResourceMigrationSource(nested, "other")
                },
                destination));

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            LocalAiResourceDataMigrationService.MigrateAsync(source, Path.Combine(source, "new")));
    }
}
