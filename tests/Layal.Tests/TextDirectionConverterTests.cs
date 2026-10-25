using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class TextDirectionConverterTests
{
    private readonly TextDirectionConverter _converter = new();

    [TestMethod]
    public void ArabicTextUsesRightToLeftWithoutChangingTheContainingLayout()
    {
        Assert.AreEqual(FlowDirection.RightToLeft, Convert("مهمة جديدة"));
        Assert.AreEqual(FlowDirection.RightToLeft, Convert("OpenAI API — اشرح النتيجة"));
    }

    [TestMethod]
    public void DirectionConverterAlsoReturnsMatchingTextAlignment()
    {
        Assert.AreEqual(TextAlignment.Right,
            _converter.Convert("نتيجة المهمة", typeof(TextAlignment), parameter: null!, CultureInfo.InvariantCulture));
        Assert.AreEqual(TextAlignment.Left,
            _converter.Convert("model-42", typeof(TextAlignment), parameter: null!, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void ExistingResolvedFlowDirectionCanDriveTextAlignment()
    {
        Assert.AreEqual(TextAlignment.Right,
            _converter.Convert(FlowDirection.RightToLeft, typeof(TextAlignment), parameter: null!, CultureInfo.InvariantCulture));
        Assert.AreEqual(TextAlignment.Left,
            _converter.Convert(FlowDirection.LeftToRight, typeof(TextAlignment), parameter: null!, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void LatinUrlsIdentifiersAndEmptyValuesRemainLeftToRight()
    {
        Assert.AreEqual(FlowDirection.LeftToRight, Convert("https://example.test/v1"));
        Assert.AreEqual(FlowDirection.LeftToRight, Convert("gpt-4.1-mini"));
        Assert.AreEqual(FlowDirection.LeftToRight, Convert(string.Empty));
        Assert.AreEqual(FlowDirection.LeftToRight, Convert(null));
    }

    private FlowDirection Convert(object? value) =>
        (FlowDirection)_converter.Convert(value!, typeof(FlowDirection), parameter: null!, CultureInfo.InvariantCulture);
}
