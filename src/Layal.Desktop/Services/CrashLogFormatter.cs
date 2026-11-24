namespace Layal.Services;

internal static class CrashLogFormatter
{
    public static string Format(DateTimeOffset timestamp, string source, Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var exceptionType = exception?.GetType().FullName ?? "Unknown";
        return $"[{timestamp:O}] {source}: {exceptionType}{Environment.NewLine}{exception?.StackTrace}{Environment.NewLine}{Environment.NewLine}";
    }
}
