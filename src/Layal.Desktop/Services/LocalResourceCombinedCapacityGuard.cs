using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Layal.Services;

public sealed record LocalResourceStorageReservation(string DirectoryPath, long RequiredBytes);

public static class LocalResourceCombinedCapacityGuard
{
    public static void EnsureAvailable(IEnumerable<LocalResourceStorageReservation> reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        var items = reservations.ToArray();
        foreach (var item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.DirectoryPath);
            if (item.RequiredBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(reservations));
        }

        var grouped = items
            .Select(item =>
            {
                var fullPath = Path.GetFullPath(item.DirectoryPath);
                var root = Path.GetPathRoot(fullPath)
                    ?? throw new IOException("A resource reservation has no volume root.");
                return new
                {
                    Directory = fullPath,
                    Root = root,
                    item.RequiredBytes
                };
            })
            .GroupBy(item => item.Root, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var required = group.Aggregate(
                0L, (sum, item) => checked(sum + item.RequiredBytes));
            var available = LocalResourceStorageCapacityGuard.GetAvailableBytes(group.Key);
            if (available < required)
                throw new InsufficientStorageException(
                    group.Key, required, available);
        }
    }
}
