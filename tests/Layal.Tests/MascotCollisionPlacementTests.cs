using System.Windows;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MascotCollisionPlacementTests
{
    [TestMethod]
    public void FindsNearestVisibleSlotBesideCompactMainWindow()
    {
        var mainWindow = new Rect(100, 100, 900, 600);
        var workArea = new Rect(0, 0, 1536, 816);
        var mascotSize = new Size(248, 175);

        var position = MascotCollisionPlacement.FindAvailablePosition(
            mainWindow,
            mascotSize,
            workArea,
            new Point(500, 300));

        Assert.IsNotNull(position);
        var mascotBounds = new Rect(position.Value, mascotSize);
        Assert.IsFalse(mascotBounds.IntersectsWith(mainWindow));
        Assert.IsTrue(workArea.Contains(mascotBounds));
    }

    [TestMethod]
    public void ReturnsNullWhenMaximizedWindowLeavesNoSafeSlot()
    {
        var workArea = new Rect(0, 0, 1536, 816);
        var position = MascotCollisionPlacement.FindAvailablePosition(
            workArea,
            new Size(248, 175),
            workArea,
            new Point(1200, 80));

        Assert.IsNull(position);
    }

    [TestMethod]
    public void KeepsPreferredPositionWhenThereIsNoOverlap()
    {
        var preferredPosition = new Point(1200, 80);
        var position = MascotCollisionPlacement.FindAvailablePosition(
            new Rect(0, 0, 900, 600),
            new Size(248, 175),
            new Rect(0, 0, 1536, 816),
            preferredPosition);

        Assert.AreEqual(preferredPosition, position);
    }
}