using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class QuickAskWindowTests
{
    [TestMethod]
    public void EmptyQuestionDoesNotCallTheChatPipeline()
    {
        var askCount = 0;
        Exception? failure = null;
        RunOnSta(() =>
        {
            QuickAskWindow? window = null;
            try
            {
                window = new QuickAskWindow(false, _ =>
                {
                    askCount++;
                    return Task.FromResult("unexpected");
                }, () => { });
                window.Show();
                var submit = (Button?)window.FindName("SubmitButton")
                    ?? throw new InvalidOperationException("Quick Ask submit button was not found.");
                var input = (TextBox?)window.FindName("QuestionInput")
                    ?? throw new InvalidOperationException("Quick Ask input was not found.");

                input.Text = "  ";
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual(0, askCount);
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    ((TextBlock?)window.FindName("StatusText"))?.Text));
                Assert.IsTrue(window.IsVisible);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });

        if (failure is not null)
            Assert.Fail("Empty-question Quick Ask test failed: " + failure);
    }

    [TestMethod]
    public void StopButtonRequestsCancellationWithoutClosingTheWindowOrDuplicatingSubmission()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var askCount = 0;
        var cancelCount = 0;
        Exception? failure = null;

        RunOnSta(() =>
        {
            QuickAskWindow? window = null;
            try
            {
                window = new QuickAskWindow(
                    isArabic: false,
                    askAsync: _ =>
                    {
                        askCount++;
                        return completion.Task;
                    },
                    cancelRequest: () => cancelCount++);
                window.Show();

                var input = (TextBox?)window.FindName("QuestionInput")
                    ?? throw new InvalidOperationException("Quick Ask input was not found.");
                var submit = (Button?)window.FindName("SubmitButton")
                    ?? throw new InvalidOperationException("Quick Ask submit button was not found.");
                var stop = (Button?)window.FindName("CancelButton")
                    ?? throw new InvalidOperationException("Quick Ask stop button was not found.");
                var status = (TextBlock?)window.FindName("StatusText")
                    ?? throw new InvalidOperationException("Quick Ask status region was not found.");

                input.Text = "Check this calculation";
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, askCount, "A pending submission must reject duplicate clicks.");

                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, cancelCount, "Repeated stop clicks must not issue duplicate cancellation requests.");
                Assert.IsFalse(stop.IsEnabled, "The stop control must remain disabled until the pending request settles.");
                Assert.IsTrue(window.IsVisible, "Requesting cancellation must not close the window before the operation settles.");
                StringAssert.Contains(status.Text, "Stop requested");
                Assert.IsFalse(input.IsEnabled, "The input remains disabled until the pending request settles.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                completion.TrySetResult("test finished");
                window?.Close();
            }
        });

        if (failure is not null)
            Assert.Fail("Quick Ask cancellation test failed: " + failure);
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
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "The Quick Ask WPF test timed out.");
        if (failure is not null)
            Assert.Fail("Quick Ask STA test failed: " + failure);
    }

    [TestMethod]
    public void SubmittingQuestionShowsTheReturnedAnswer()
    {
        string? receivedQuestion = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            QuickAskWindow? window = null;
            try
            {
                window = new QuickAskWindow(
                    isArabic: true,
                    askAsync: question =>
                    {
                        receivedQuestion = question;
                        return Task.FromResult("الإجابة: " + question);
                    },
                    cancelRequest: () => { });

                window.Show();
                window.UpdateLayout();
                var input = (TextBox?)window.FindName("QuestionInput")
                    ?? throw new InvalidOperationException("Quick Ask input was not found.");
                var submit = (Button?)window.FindName("SubmitButton")
                    ?? throw new InvalidOperationException("Quick Ask submit button was not found.");
                var answer = (TextBlock?)window.FindName("AnswerText")
                    ?? throw new InvalidOperationException("Quick Ask answer region was not found.");

                input.Text = "  اشرح لي هذه الوظيفة  ";
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("اشرح لي هذه الوظيفة", receivedQuestion);
                Assert.AreEqual("الإجابة: اشرح لي هذه الوظيفة", answer.Text);
                var answerRegion = (ScrollViewer?)window.FindName("AnswerScrollViewer")
                    ?? throw new InvalidOperationException("Quick Ask answer container was not found.");
                Assert.AreEqual(Visibility.Visible, answerRegion.Visibility);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "The Quick Ask WPF test timed out.");
        if (failure is not null)
            Assert.Fail("Quick Ask test failed: " + failure);
    }
}
