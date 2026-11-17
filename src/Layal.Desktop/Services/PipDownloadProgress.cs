namespace Layal.Services;

public sealed record PipDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    double? RemainingSeconds);
