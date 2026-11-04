using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Layal.Views;

public sealed record QuickAskResult(bool Succeeded, string Message)
{
    public static QuickAskResult Success(string message) => new(true, message ?? string.Empty);
    public static QuickAskResult Failure(string message) => new(false, message ?? string.Empty);
}

public partial class QuickAskWindow : Window
{
    private readonly bool _isArabic;
    private readonly Func<string, Task<QuickAskResult>> _askAsync;
    private readonly Action _cancelRequest;
    private readonly string _shortcutDisplay;
    private bool _isSubmitting;
    private bool _isCancellationRequested;
    private bool _isDisposed;

    public QuickAskWindow(bool isArabic, Func<string, Task<QuickAskResult>> askAsync, Action cancelRequest, string shortcutDisplay = "Alt+Shift+S")
    {
        _isArabic = isArabic;
        _askAsync = askAsync ?? throw new ArgumentNullException(nameof(askAsync));
        _cancelRequest = cancelRequest ?? throw new ArgumentNullException(nameof(cancelRequest));
        _shortcutDisplay = shortcutDisplay;
        InitializeComponent();
        ApplyLanguage();
    }

    public void FocusInput()
    {
        if (!IsVisible)
            return;

        Activate();
        QuestionInput.Focus();
        QuestionInput.SelectAll();
    }

    private void ApplyLanguage()
    {
        Title = _isArabic ? "ليال | سؤال سريع" : "Layal | Quick Ask";
        HeadingText.Text = _isArabic ? "سؤال سريع" : "Quick Ask";
        HintText.Text = _isArabic
            ? "اكتب سؤالك. يستخدم Layal سياق المحادثة وإعدادات المزود الحالية؛ لا يتم التقاط الشاشة أو إرفاق ملفات تلقائيًا."
            : "Type a question using Layal's current chat context and provider settings; no screen capture or files are attached automatically.";
        QuestionInput.FlowDirection = _isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        QuestionInput.TextAlignment = _isArabic ? TextAlignment.Right : TextAlignment.Left;
        AutomationProperties.SetName(QuestionInput, _isArabic ? "سؤالك" : "Your question");
        AutomationProperties.SetHelpText(QuestionInput,
            _isArabic ? $"الاختصار العالمي: {_shortcutDisplay}. Ctrl+Enter للإرسال، وShift+Enter لسطر جديد، وEsc للإغلاق أو إيقاف الطلب."
                : $"Global shortcut: {_shortcutDisplay}. Ctrl+Enter submits, Shift+Enter inserts a new line, and Esc closes or stops the request.");
        CloseButton.Content = _isArabic ? "إغلاق" : "Close";
        CancelButton.Content = _isArabic ? "إغلاق" : "Close";
        SubmitButton.Content = _isArabic ? "اسأل" : "Ask";
        StatusText.Text = string.Empty;
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
            return;

        var question = QuestionInput.Text.Trim();
        if (question.Length == 0)
        {
            StatusText.Text = _isArabic ? "اكتب السؤال أولًا." : "Enter a question first.";
            QuestionInput.Focus();
            return;
        }

        _isSubmitting = true;
        _isCancellationRequested = false;
        SubmitButton.IsEnabled = false;
        QuestionInput.IsEnabled = false;
        CancelButton.Content = _isArabic ? "إيقاف الطلب" : "Stop request";
        StatusText.Text = _isArabic ? "جارٍ إرسال السؤال إلى المحادثة..." : "Sending the question to chat...";
        AnswerScrollViewer.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _askAsync(question);
            if (_isDisposed)
                return;

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Message))
            {
                AnswerScrollViewer.Visibility = Visibility.Collapsed;
                StatusText.Text = string.IsNullOrWhiteSpace(result.Message)
                    ? (_isArabic ? "لم تكتمل الإجابة. يمكنك مراجعة حالة المحادثة أو إعادة المحاولة." : "The answer did not complete. Check chat status or try again.")
                    : result.Message;
                return;
            }

            AnswerText.Text = result.Message;
            AnswerScrollViewer.Visibility = Visibility.Visible;
            StatusText.Text = _isArabic ? "انتهى الطلب. يمكنك كتابة سؤال متابعة." : "Request finished. You can ask a follow-up.";
            QuestionInput.Clear();
            Height = Math.Min(MaxHeight, Math.Max(Height, 390));
        }
        catch (Exception)
        {
            if (!_isDisposed)
                StatusText.Text = _isArabic
                    ? "تعذر إكمال السؤال السريع. راجع حالة المحادثة الرئيسية."
                    : "Quick Ask could not finish. Check the main chat status.";
        }
        finally
        {
            _isSubmitting = false;
            if (!_isDisposed && IsLoaded)
            {
                SubmitButton.IsEnabled = true;
                QuestionInput.IsEnabled = true;
                CancelButton.Content = _isArabic ? "إغلاق" : "Close";
                CancelButton.IsEnabled = true;
                QuestionInput.Focus();
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
        {
            RequestCancellation();
            StatusText.Text = _isArabic
                ? "طلب الإيقاف أُرسل. جارٍ انتظار توقف العملية الحالية..."
                : "Stop requested. Waiting for the current operation to cancel...";
            return;
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
        {
            var result = MessageBox.Show(
                this,
                _isArabic ? "هل تريد إيقاف السؤال الحالي وإغلاق النافذة؟" : "Stop the current question and close this window?",
                _isArabic ? "إيقاف السؤال السريع" : "Stop Quick Ask",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
                return;

            RequestCancellation();
        }

        Close();
    }

    private void RequestCancellation()
    {
        if (!_isSubmitting || _isCancellationRequested)
            return;

        _isCancellationRequested = true;
        _cancelRequest();
        CancelButton.IsEnabled = false;
    }

    private void QuickAskWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_isSubmitting)
            {
                RequestCancellation();
                StatusText.Text = _isArabic
                    ? "طلب الإيقاف أُرسل. جارٍ انتظار توقف العملية الحالية..."
                    : "Stop requested. Waiting for the current operation to cancel...";
            }
            else
            {
                Close();
            }
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SubmitButton_Click(SubmitButton, new RoutedEventArgs(Button.ClickEvent));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The title-bar X must cancel the in-flight chat request just like Escape and Stop.
        if (_isSubmitting)
            RequestCancellation();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isDisposed = true;
        base.OnClosed(e);
    }
}
