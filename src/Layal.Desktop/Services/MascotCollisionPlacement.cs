using System;
using System.Linq;
using System.Windows;

namespace Layal.Services;

/// <summary>Finds a nearby mascot position that stays inside the work area and clear of Layal's main window.</summary>
public static class MascotCollisionPlacement
{
    public static Point? FindAvailablePosition(Rect mainWindow, Size mascotSize, Rect workArea, Point preferredPosition, double gap = 12)
    {
        if (mascotSize.Width <= 0 || mascotSize.Height <= 0 ||
            workArea.Width < mascotSize.Width || workArea.Height < mascotSize.Height)
            return null;

        var minX = workArea.Left;
        var maxX = workArea.Right - mascotSize.Width;
        var minY = workArea.Top;
        var maxY = workArea.Bottom - mascotSize.Height;
        var xCandidates = new[]
        {
            minX,
            Math.Clamp(mainWindow.Left - mascotSize.Width - gap, minX, maxX),
            Math.Clamp(mainWindow.Right + gap, minX, maxX),
            Math.Clamp(preferredPosition.X, minX, maxX),
            maxX
        }.Distinct();
        var yCandidates = new[]
        {
            minY,
            Math.Clamp(mainWindow.Top - mascotSize.Height - gap, minY, maxY),
            Math.Clamp(mainWindow.Bottom + gap, minY, maxY),
            Math.Clamp(preferredPosition.Y, minY, maxY),
            maxY
        }.Distinct();

        var mascotBounds = from x in xCandidates
                           from y in yCandidates
                           let bounds = new Rect(x, y, mascotSize.Width, mascotSize.Height)
                           where !bounds.IntersectsWith(mainWindow)
                           select bounds;
        var availableBounds = mascotBounds
            .OrderBy(bounds => DistanceSquared(bounds.TopLeft, preferredPosition))
            .ToArray();

        return availableBounds.Length == 0 ? null : availableBounds[0].TopLeft;
    }

    private static double DistanceSquared(Point left, Point right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return x * x + y * y;
    }
}