using System;
using System.Security.Cryptography;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaRegistryManifestPolicyTests
{
    [TestMethod]
    public void ParsesManifestListAndRetainsOnlyItsFullChildDigests()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.ollama.manifest.list.v2+json",
              "manifests": [
                {
                  "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "runner": "ggml",
                  "format": "gguf"
                },
                {
                  "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
                  "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "runner": "llamacpp",
                  "format": "gguf"
                }
              ]
            }
            """);
        var header = "sha256:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        var snapshot = OllamaRegistryManifestPolicy.Parse(body, header, DateTimeOffset.UtcNow);

        Assert.AreEqual(OllamaRegistryManifestPolicy.ModelId, snapshot.ModelId);
        Assert.AreEqual(OllamaRegistryManifestPolicy.RegistryHost, snapshot.RegistryHost);
        Assert.AreEqual(OllamaRegistryManifestPolicy.ManifestListMediaType, snapshot.MediaType);
        Assert.AreEqual(2, snapshot.AllowedModelDigests.Length);
        Assert.IsTrue(OllamaRegistryManifestPolicy.ContainsSelectedModelDigest(
            snapshot, new string('a', 64)));
        Assert.IsTrue(OllamaRegistryManifestPolicy.ContainsSelectedModelDigest(
            snapshot, new string('b', 64)));
        Assert.IsFalse(OllamaRegistryManifestPolicy.ContainsSelectedModelDigest(
            snapshot, new string('c', 64)));
    }

    [TestMethod]
    public void RejectsRegistryHeaderWhenItDoesNotMatchTheRawManifestBytes()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
              "config": { "mediaType": "application/vnd.ollama.image.model", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 },
              "layers": []
            }
            """);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.Parse(
                body,
                "sha256:" + new string('f', 64),
                DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void PlainManifestDigestIsTheOnlyAllowedLocalDigest()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
              "config": { "mediaType": "application/vnd.ollama.image.model", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 },
              "layers": []
            }
            """);
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var snapshot = OllamaRegistryManifestPolicy.Parse(body, null, DateTimeOffset.UtcNow);

        Assert.AreEqual(digest, snapshot.ManifestDigest);
        Assert.IsTrue(OllamaRegistryManifestPolicy.ContainsSelectedModelDigest(snapshot, digest));
        Assert.IsFalse(OllamaRegistryManifestPolicy.ContainsSelectedModelDigest(snapshot, new string('a', 64)));
    }

    [TestMethod]
    public void RejectsMissingOrInvalidChildManifestDigest()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.ollama.manifest.list.v2+json",
              "manifests": [
                { "mediaType": "application/vnd.docker.distribution.manifest.v2+json", "runner": "ggml" }
              ]
            }
            """);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.Parse(body, null, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void RejectsUnexpectedModelRegistryAndMediaTypeInPersistedPin()
    {
        var snapshot = new OllamaRegistryManifestSnapshot(
            "qwen3.5:2b",
            "registry.ollama.ai",
            "2b",
            OllamaRegistryManifestPolicy.ManifestListMediaType,
            new string('d', 64),
            new[] { new string('a', 64) },
            DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.ValidateSnapshot(snapshot with { RegistryHost = "example.com" }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.ValidateSnapshot(snapshot with { ModelId = "other:latest" }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.ValidateSnapshot(snapshot with { MediaType = "text/plain" }));
    }

    [TestMethod]
    public void RejectsRegistryManifestChangeAfterAFullPinWasSaved()
    {
        var first = new OllamaRegistryManifestSnapshot(
            OllamaRegistryManifestPolicy.ModelId,
            OllamaRegistryManifestPolicy.RegistryHost,
            OllamaRegistryManifestPolicy.Tag,
            OllamaRegistryManifestPolicy.ManifestMediaType,
            new string('a', 64),
            new[] { new string('a', 64) },
            DateTimeOffset.UtcNow);
        var changed = first with
        {
            ManifestDigest = new string('b', 64),
            AllowedModelDigests = new[] { new string('b', 64) }
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.EnsureSameRegistryManifest(first, changed));
    }

    [TestMethod]
    public void RegistryPinAllowsOnlyAChildDeclaredByTheTrustedManifestList()
    {
        var snapshot = new OllamaRegistryManifestSnapshot(
            OllamaRegistryManifestPolicy.ModelId,
            OllamaRegistryManifestPolicy.RegistryHost,
            OllamaRegistryManifestPolicy.Tag,
            OllamaRegistryManifestPolicy.ManifestListMediaType,
            new string('d', 64),
            new[] { new string('a', 64), new string('b', 64) },
            DateTimeOffset.UtcNow);

        OllamaRegistryManifestPolicy.EnsureSelectedModelDigestMatches(snapshot, new string('b', 64));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            OllamaRegistryManifestPolicy.EnsureSelectedModelDigestMatches(snapshot, new string('c', 64)));
    }
}
