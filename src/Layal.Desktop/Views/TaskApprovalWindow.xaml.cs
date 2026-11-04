using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace Layal.Views;

public sealed partial class TaskApprovalWindow : Window
{
    private readonly Action _requestTaskCancellation;

    public TaskApprovalWindow(bool isArabic, string title, string details, Action requestTaskCancellation)
    {
        _requestTaskCancellation = requestTaskCancellation ?? throw new ArgumentNullException(nameof(requestTaskCancellation));
        InitializeComponent();

        Title = isArabic ? "موافقة مطلوبة من ليال" : "Layal requires approval";
        HeadingText.Text = string.IsNullOrWhiteSpace(title)
            ? (isArabic ? "الإجراء التالي يحتاج موافقتك" : "Action requires your approval")
            : title;
        IntroText.Text = isArabic
            ? "راجع تفاصيل الإجراء قبل الموافقة:"
            : "Review the action details before approving:";
        DetailsText.Text = details ?? string.Empty;
        EscapeHintText.Text = isArabic
            ? "اضغط Esc لإلغاء المهمة بالكامل. زر «رفض الإجراء» يرفض هذا الإجراء فقط ويترك المهمة قابلة للمتابعة."
            : "Press Esc to cancel the entire task. Decline action rejects only this action and leaves the task available to continue.";

        FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        DetailsText.TextAlignment = isArabic ? TextAlignment.Right : TextAlignment.Left;
        AutomationProperties.SetName(this, isArabic ? "موافقة مطلوبة من ليال" : "Layal approval required");
        AutomationProperties.SetHelpText(this, EscapeHintText.Text);
        AutomationProperties.SetName(DetailsText, isArabic ? "تفاصيل الإجراء المطلوب" : "Requested action details");
    }

    public bool ShowForApproval(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();

        // Cancellation may originate from Escape or another application shutdown/privacy path.
        // Close the modal prompt on its dispatcher so the orchestrator can observe the token.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (IsVisible)
                        Close();
                }));
            }
            catch (InvalidOperationException)
            {
                // The UI dispatcher is already shutting down; no approval can continue.
            }
        });

        cancellationToken.ThrowIfCancellationRequested();
        var approved = ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return approved;
    }

    private void TaskApprovalWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None)
            return;

        e.Handled = true;
        _requestTaskCancellation();
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void DeclineButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
