using System;
using System.Windows;

namespace Layal.Services;

/// <summary>
/// Hit-region geometry for the floating mascot, separated from WPF input events so its
/// design-space boundaries and scale normalization can be regression-tested on Windows CI.
/// </summary>
public static class MascotHitTestGeometry
{
    private const double DesignWidth = 248.0;

    public static bool IsPointOnMascot(Point point, double windowWidth, bool taskPropsVisible)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(windowWidth) || windowWidth <= 0)
            return false;

        // The larger transparent window leaves room for floating props. Normalize back to
        // design coordinates so resizing never turns empty margins into click targets.
        var scale = Math.Max(0.1, windowWidth / DesignWidth);
        var x = point.X / scale;
        var y = point.Y / scale;

        var onCore = x >= 48 && x <= 201 && y >= 12 && y <= 139;
        var onLeftSensor = x >= 5 && x <= 35 && y >= 42 && y <= 94;
        var onRightSensor = x >= 213 && x <= 243 && y >= 42 && y <= 94;
        var onPaper = taskPropsVisible && x >= 3 && x <= 43 && y >= 90 && y <= 142;
        var onPencil = taskPropsVisible && x >= 28 && x <= 48 && y >= 84 && y <= 134;
        var onComputer = taskPropsVisible && x >= 190 && x <= 246 && y >= 91 && y <= 143;

        return onCore || onLeftSensor || onRightSensor || onPaper || onPencil || onComputer;
    }
}
