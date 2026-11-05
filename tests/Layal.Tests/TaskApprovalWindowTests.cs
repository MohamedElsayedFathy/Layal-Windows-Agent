using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Reflection;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TaskApprovalWindowTests
{
    [TestMethod]
    public void DeclineRejectsOnlyTheCurrentActionWithoutCancellingTheTask()
    {
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;

        RunOnSta(() =>
        {
            TaskApprovalWindow? dialog = null;
            try
            {
                dialog = new TaskApprovalWindow(false, "Create file", "Review the file content.", cancellation.Cancel);
                dialog.Loaded += (_, _) =>
                {
                    var decline = (Button?)dialog.FindName("DeclineButton")
                        ?? throw new InvalidOperationException("The approval decline button was not found.");
                    decline.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };

                Assert.IsFalse(dialog.ShowForApproval(cancellation.Token));
                Assert.IsFalse(cancellation.IsCancellationRequested,
                    "Declining one action must not cancel the full agent task.");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog?.Close(); }
        });

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }


    [TestMethod]
    public void ClosingApprovalDialogThroughWindowChromeDeclinesOnlyTheAction()
    {
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;

        RunOnSta(() =>
        {
            TaskApprovalWindow? dialog = null;
            try
            {
                dialog = new TaskApprovalWindow(false, "Delete record", "Review the exact record and effect.", cancellation.Cancel);
                dialog.Loaded += (_, _) => dialog.Close();

                Assert.IsFalse(dialog.ShowForApproval(cancellation.Token),
                    "Closing the approval window must be treated as a decline.");
                Assert.IsFalse(cancellation.IsCancellationRequested,
                    "Closing one approval must not cancel the whole agent task.");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog?.Close(); }
        });

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }


    [TestMethod]
    public void EscapeCancelsTheEntireTaskAndCannotApproveThePendingAction()
    {
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;

        RunOnSta(() =>
        {
            TaskApprovalWindow? dialog = null;
            try
            {
                dialog = new TaskApprovalWindow(false, "Delete record", "Review the exact record and effect.", cancellation.Cancel);
                dialog.Loaded += (_, _) =>
                {
                    var source = PresentationSource.FromVisual(dialog)
                        ?? throw new InvalidOperationException("The approval window has no presentation source.");
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    var handler = typeof(TaskApprovalWindow).GetMethod(
                        "TaskApprovalWindow_PreviewKeyDown",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("The approval Escape handler was not found.");
                    handler.Invoke(dialog, new object[] { dialog, escape });
                };

                Assert.ThrowsExactly<OperationCanceledException>(() => dialog.ShowForApproval(cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested,
                    "Escape must cancel the whole task, not just decline the current action.");
                Assert.IsFalse(dialog.IsVisible, "The pending approval must close after Escape cancels the task.");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog?.Close(); }
        });

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public void PreCancelledTokenRejectsApprovalBeforeShowingDialog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? failure = null;

        RunOnSta(() =>
        {
            TaskApprovalWindow? dialog = null;
            try
            {
                dialog = new TaskApprovalWindow(false, "Create file", "Review the file content.", cancellation.Cancel);
                Assert.ThrowsExactly<OperationCanceledException>(() => dialog.ShowForApproval(cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
                Assert.IsFalse(dialog.IsVisible, "A pre-cancelled request must never display an approval dialog.");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog?.Close(); }
        });

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
