using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Layal.Models;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MemoryNoteReviewWindowTests
{
    [TestMethod]
    public void SaveAndDeleteReviewsShowTheCompleteNoteInAReadOnlyAccessiblePreview()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Memory review windows require the Windows WPF desktop runtime.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var content = "محتوى عربي مع English text — " + nameof(SaveAndDeleteReviewsShowTheCompleteNoteInAReadOnlyAccessiblePreview) + "\n" +
                    "Long body marker: " + "full-preview-".PadRight(6000, 'x');
                var note = new MemoryNote
                {
                    Title = "ملاحظة مراجعة",
                    Category = "Personal",
                    Content = content
                };

                foreach (var action in new[]
                {
                    MemoryNoteReviewAction.SaveNew,
                    MemoryNoteReviewAction.SaveChanges,
                    MemoryNoteReviewAction.Delete
                })
                {
                    var window = new MemoryNoteReviewWindow(note, isArabic: true, action);
                    var glassSurface = window.Content as Border
                        ?? throw new AssertFailedException("The review window must use the shared glass surface.");
                    Assert.AreEqual(new CornerRadius(18), glassSurface.CornerRadius,
                        "The complete-note review must use the shared rounded glass geometry.");
                    var root = glassSurface.Child as Grid
                        ?? throw new AssertFailedException("The review window must retain its structured WPF layout inside the glass surface.");
                    var previewContainer = root.Children.OfType<Border>().Single();
                    var preview = previewContainer.Child as TextBox
                        ?? throw new AssertFailedException("The complete note preview TextBox was not found.");
                    Assert.IsTrue(preview.IsReadOnly, "The action review must not let its confirmation preview change the note.");
                    Assert.AreEqual(content, preview.Text, "The full note content must be visible without truncation.");
                    Assert.AreEqual("المحتوى الكامل للملاحظة", AutomationProperties.GetName(preview));
                    Assert.IsTrue(preview.AcceptsReturn);
                    Assert.AreEqual(ScrollBarVisibility.Auto, preview.VerticalScrollBarVisibility);

                    var buttonPanel = root.Children.OfType<StackPanel>().Last();
                    var confirm = buttonPanel.Children.OfType<Button>().Last();
                    Assert.AreEqual(action == MemoryNoteReviewAction.Delete ? "حذف نهائي" :
                        action == MemoryNoteReviewAction.SaveChanges ? "حفظ التعديلات" : "حفظ الملاحظة",
                        confirm.Content);
                    Assert.AreEqual(action != MemoryNoteReviewAction.Delete, confirm.IsDefault,
                        "Delete must not be the implicit default button; Save may be confirmed with Enter.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
            Assert.Fail("Memory review window test did not finish within 30 seconds.");
        if (failure is not null)
            Assert.Fail("Memory review window test failed: " + failure);
    }
}
