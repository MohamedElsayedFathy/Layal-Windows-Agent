using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Layal.Services;

public sealed record LayalDataPaths(
    string DataRoot,
    string WorkspaceRoot,
    bool IsolatedAcceptance,
    string? AcceptanceRoot);

/// <summary>
/// Resolves Layal's per-user storage roots. The optional acceptance root is deliberately
/// process-scoped so a live diagnostic can isolate app state without changing the user's
/// normal LocalAppData or Documents folders.
/// </summary>
public static class LayalDataPathPolicy
{
    public const string AcceptanceRootEnvironmentVariable = "LAYAL_ACCEPTANCE_ROOT";

    public static LayalDataPaths Resolve(
        string? acceptanceRoot,
        string localAppData,
        string documents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(documents);

        var personalDataRoot = Path.GetFullPath(Path.Combine(localAppData, "Layal"));
        var personalWorkspaceRoot = Path.GetFullPath(Path.Combine(documents, "LayalWorkspace"));

        if (string.IsNullOrWhiteSpace(acceptanceRoot))
            return new LayalDataPaths(personalDataRoot, personalWorkspaceRoot, false, null);

        var normalizedAcceptanceRoot = acceptanceRoot.Trim();
        if (!Path.IsPathFullyQualified(normalizedAcceptanceRoot))
            throw new ArgumentException("The isolated acceptance root must be an absolute path.", nameof(acceptanceRoot));

        var root = Path.GetFullPath(normalizedAcceptanceRoot);
        var isolatedDataRoot = Path.GetFullPath(Path.Combine(root, "LocalAppData", "Layal"));
        var isolatedWorkspaceRoot = Path.GetFullPath(Path.Combine(root, "Documents", "LayalWorkspace"));

        if (PathsOverlap(isolatedDataRoot, personalDataRoot))
            throw new ArgumentException("The isolated acceptance data root overlaps the current user's Layal data.", nameof(acceptanceRoot));
        if (PathsOverlap(isolatedWorkspaceRoot, personalWorkspaceRoot))
            throw new ArgumentException("The isolated acceptance workspace overlaps the current user's Layal workspace.", nameof(acceptanceRoot));

        return new LayalDataPaths(isolatedDataRoot, isolatedWorkspaceRoot, true, root);
    }

    public static string GetAcceptanceScopeSuffix(string acceptanceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceRoot);
        var normalized = Path.GetFullPath(acceptanceRoot);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = WithTrailingSeparator(Path.GetFullPath(first));
        var normalizedSecond = WithTrailingSeparator(Path.GetFullPath(second));
        return normalizedFirst.StartsWith(normalizedSecond, StringComparison.OrdinalIgnoreCase) ||
               normalizedSecond.StartsWith(normalizedFirst, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
