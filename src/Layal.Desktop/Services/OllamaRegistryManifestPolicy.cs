using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Layal.Services;

public sealed record OllamaRegistryManifestSnapshot(
    string ModelId,
    string RegistryHost,
    string Tag,
    string MediaType,
    string ManifestDigest,
    string[] AllowedModelDigests,
    DateTimeOffset VerifiedAtUtc);

/// <summary>
/// Validates the raw OCI/Docker manifest bytes and the exact child manifests Ollama may select.
/// Ollama /api/tags reports the selected child digest for a manifest list, not its parent digest.
/// </summary>
public static class OllamaRegistryManifestPolicy
{
    public const string RegistryHost = "registry.ollama.ai";
    public const string ModelId = "qwen3.5:2b";
    public const string Tag = "2b";
    public const string ManifestListMediaType = "application/vnd.ollama.manifest.list.v2+json";
    public const string ManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";

    public static OllamaRegistryManifestSnapshot Parse(
        byte[] manifestBytes,
        string? dockerContentDigest,
        DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        if (manifestBytes.Length == 0 || manifestBytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("The official Qwen manifest has an invalid size.");

        var computedDigest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(dockerContentDigest))
        {
            var headerDigest = NormalizeRegistryDigest(dockerContentDigest);
            OllamaModelDigestPolicy.EnsureMatches(headerDigest, computedDigest);
        }

        using var document = JsonDocument.Parse(manifestBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) ||
            version != 2)
        {
            throw new InvalidDataException("The official Qwen manifest must use schemaVersion 2.");
        }

        var mediaType = ReadRequiredString(root, "mediaType");
        string[] allowedDigests;
        if (string.Equals(mediaType, ManifestListMediaType, StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array ||
                manifests.GetArrayLength() == 0)
            {
                throw new InvalidDataException("The Qwen manifest list does not declare runnable child manifests.");
            }

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in manifests.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The Qwen manifest list contains an invalid child entry.");

                var digest = NormalizeRegistryDigest(ReadRequiredString(child, "digest"));
                values.Add(digest);
            }

            allowedDigests = values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (allowedDigests.Length == 0)
                throw new InvalidDataException("The Qwen manifest list contains no usable child digests.");
        }
        else if (string.Equals(mediaType, ManifestMediaType, StringComparison.Ordinal))
        {
            allowedDigests = new[] { computedDigest };
        }
        else
        {
            throw new InvalidDataException(
                $"The official Qwen registry returned unsupported manifest mediaType '{mediaType}'.");
        }

        return new OllamaRegistryManifestSnapshot(
            ModelId,
            RegistryHost,
            Tag,
            mediaType,
            computedDigest,
            allowedDigests,
            verifiedAtUtc);
    }

    public static OllamaRegistryManifestSnapshot ValidateSnapshot(
        OllamaRegistryManifestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.ModelId, ModelId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RegistryHost, RegistryHost, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Tag, Tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted Qwen registry pin targets an unexpected model or registry.");
        }

        if (!OllamaModelDigestPolicy.IsValidSha256Digest(snapshot.ManifestDigest))
            throw new InvalidDataException("The persisted Qwen registry manifest digest is not a full SHA-256.");

        if (snapshot.MediaType != ManifestListMediaType &&
            snapshot.MediaType != ManifestMediaType)
        {
            throw new InvalidDataException("The persisted Qwen registry manifest mediaType is unsupported.");
        }

        if (snapshot.AllowedModelDigests is null ||
            snapshot.AllowedModelDigests.Length == 0 ||
            snapshot.AllowedModelDigests.Any(digest => !OllamaModelDigestPolicy.IsValidSha256Digest(digest)))
        {
            throw new InvalidDataException("The persisted Qwen registry pin has invalid child digests.");
        }

        var normalized = snapshot.AllowedModelDigests
            .Select(digest => digest.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(digest => digest, StringComparer.Ordinal)
            .ToArray();

        if (snapshot.MediaType == ManifestMediaType &&
            (normalized.Length != 1 ||
             !OllamaModelDigestPolicy.Matches(normalized[0], snapshot.ManifestDigest)))
        {
            throw new InvalidDataException("A single Qwen manifest pin must match its own manifest digest.");
        }

        return snapshot with
        {
            ManifestDigest = snapshot.ManifestDigest.ToLowerInvariant(),
            AllowedModelDigests = normalized
        };
    }

    public static bool ContainsSelectedModelDigest(
        OllamaRegistryManifestSnapshot snapshot,
        string? localDigest)
    {
        var valid = ValidateSnapshot(snapshot);
        if (!OllamaModelDigestPolicy.IsValidSha256Digest(localDigest))
            return false;

        return valid.AllowedModelDigests.Any(
            digest => OllamaModelDigestPolicy.Matches(digest, localDigest!));
    }

    public static void EnsureSelectedModelDigestMatches(
        OllamaRegistryManifestSnapshot snapshot,
        string? localDigest)
    {
        if (!ContainsSelectedModelDigest(snapshot, localDigest))
        {
            throw new InvalidDataException(
                "The installed Qwen manifest digest is not one of the runnable child manifests declared " +
                "by the pinned official Ollama registry manifest. The model is not marked ready.");
        }
    }

    public static void EnsureSameRegistryManifest(
        OllamaRegistryManifestSnapshot expected,
        OllamaRegistryManifestSnapshot actual)
    {
        var normalizedExpected = ValidateSnapshot(expected);
        var normalizedActual = ValidateSnapshot(actual);
        if (!string.Equals(
                normalizedExpected.ManifestDigest,
                normalizedActual.ManifestDigest,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalizedExpected.MediaType, normalizedActual.MediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The official qwen3.5:2b tag changed after its registry manifest was pinned. " +
                "Silent model replacement is blocked until an explicit model-update/reset flow exists.");
        }
    }

    private static string NormalizeRegistryDigest(string value)
    {
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registry manifest digest must use SHA-256.");
        var hex = value[prefix.Length..];
        if (!OllamaModelDigestPolicy.IsValidSha256Digest(hex))
            throw new InvalidDataException("The registry returned an invalid full SHA-256 digest.");
        return hex.ToLowerInvariant();
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"The official Qwen manifest is missing required field '{name}'.");
        }

        return property.GetString()!;
    }
}
