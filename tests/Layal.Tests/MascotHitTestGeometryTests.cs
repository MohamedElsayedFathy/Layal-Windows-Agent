using System.Windows;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MascotHitTestGeometryTests
{
    [TestMethod]
    public void CoreAndSensorsAreHitTargetsButTransparentPaddingIsNot()
    {
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(124, 80), 248, taskPropsVisible: false));
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(20, 60), 248, taskPropsVisible: false));
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(225, 60), 248, taskPropsVisible: false));

        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(8, 8), 248, taskPropsVisible: true));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(120, 160), 248, taskPropsVisible: true));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(247, 20), 248, taskPropsVisible: false));
    }

    [TestMethod]
    public void TaskPropsAreInteractiveOnlyWhileVisible()
    {
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(15, 110), 248, taskPropsVisible: true));   // Paper
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(46, 100), 248, taskPropsVisible: true));  // Pencil
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(225, 120), 248, taskPropsVisible: true)); // Holographic computer

        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(15, 110), 248, taskPropsVisible: false));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(46, 100), 248, taskPropsVisible: false));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(225, 120), 248, taskPropsVisible: false));
    }

    [TestMethod]
    public void HitRegionCoordinatesNormalizeAtLargerWindowScales()
    {
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(186, 120), 372, taskPropsVisible: false));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(12, 12), 372, taskPropsVisible: true));
        Assert.IsTrue(MascotHitTestGeometry.IsPointOnMascot(new Point(30, 165), 372, taskPropsVisible: true)); // Paper, scaled by 1.5
    }

    [TestMethod]
    public void InvalidDimensionsAndCoordinatesFailClosed()
    {
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(100, 50), 0, taskPropsVisible: true));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(double.NaN, 50), 248, taskPropsVisible: true));
        Assert.IsFalse(MascotHitTestGeometry.IsPointOnMascot(new Point(100, double.PositiveInfinity), 248, taskPropsVisible: true));
    }
}
