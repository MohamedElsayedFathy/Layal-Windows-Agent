using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Layal.Views;

public partial class QuickAskWindow : Window
{
    private readonly bool _isArabic;
    private readonly Func<string, Task<string>> _askAsync;
    private readonly Action _cancelRequest;
    private bool _isSubmitting;
    private bool _isDisposed;

    public QuickAskWindow(bool isArabic, Func<string, Task<string>> askAsync, Action cancelRequest)
    {
        _isArabic = isArabic;
        _askAsync = askAsync ?? throw new ArgumentNullException(nameof(askAsync));
        _cancelRequest = cancelRequest ?? throw new ArgumentNullException(nameof(cancelRequest));
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
            _isArabic ? "Ctrl+Enter للإرسال، وShift+Enter لسطر جديد، وEsc للإغلاق أو إيقاف الطلب."
                : "Ctrl+Enter submits, Shift+Enter inserts a new line, and Esc closes or stops the request.");
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
        SubmitButton.IsEnabled = false;
        QuestionInput.IsEnabled = false;
        CancelButton.Content = _isArabic ? "إيقاف الطلب" : "Stop request";
        StatusText.Text = _isArabic ? "جارٍ إرسال السؤال إلى المحادثة..." : "Sending the question to chat...";
        AnswerScrollViewer.Visibility = Visibility.Collapsed;

        try
        {
            var answer = await _askAsync(question);
            if (_isDisposed)
                return;

            AnswerText.Text = answer;
            AnswerScrollViewer.Visibility = Visibility.Visible;
            StatusText.Text = _isArabic ? "انتهى الطلب. يمكنك كتابة سؤال متابعة." : "Request finished. You can ask a follow-up.";
            QuestionInput.Clear();
            Height = Math.Min(MaxHeight, Math.Max(Height, 390));
            QuestionInput.Focus();
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
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
        {
            _cancelRequest();
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

            _cancelRequest();
        }

        Close();
    }

    private void QuickAskWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_isSubmitting)
            {
                _cancelRequest();
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

    protected override void OnClosed(EventArgs e)
    {
        _isDisposed = true;
        base.OnClosed(e);
    }
}
