using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class WindowBackdropServiceTests
{
    [TestMethod]
    public void ZeroWindowHandleIsRejectedWithoutCallingWindowsCompositionApis()
    {
        Assert.IsFalse(
            WindowBackdropService.TryApply(IntPtr.Zero, dark: false, highContrast: false),
            "A missing native window handle must use the WPF glass fallback rather than calling DWM.");
    }
}
