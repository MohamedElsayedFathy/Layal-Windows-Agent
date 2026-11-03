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
