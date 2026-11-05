using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;

namespace Layal.Views;

public enum MemoryNoteReviewAction
{
    SaveNew,
    SaveChanges,
    Delete
}

/// <summary>
/// Shows the exact local note payload before a save/update/delete. This window never sends
/// note content to an AI provider or another process.
/// </summary>
public sealed class MemoryNoteReviewWindow : Window
{
    public MemoryNoteReviewWindow(MemoryNote note, bool isArabic, MemoryNoteReviewAction action)
    {
        ArgumentNullException.ThrowIfNull(note);
        var isDelete = action == MemoryNoteReviewAction.Delete;
        var isSave = !isDelete;

        Title = action switch
        {
            MemoryNoteReviewAction.SaveNew => isArabic ? "مراجعة الملاحظة قبل الحفظ" : "Review note before saving",
            MemoryNoteReviewAction.SaveChanges => isArabic ? "مراجعة التعديلات قبل الحفظ" : "Review changes before saving",
            _ => isArabic ? "مراجعة الحذف النهائي" : "Review permanent deletion"
        };
        Width = 760;
        Height = 680;
        MinWidth = 520;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        FlowDirection = FlowDirection.LeftToRight;
        Background = SystemColors.WindowBrush;
        AutomationProperties.SetName(this, Title);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var heading = new TextBlock
        {
            Text = action switch
            {
                MemoryNoteReviewAction.SaveNew => isArabic ? "راجع الملاحظة التي ستُحفظ" : "Review the note that will be saved",
                MemoryNoteReviewAction.SaveChanges => isArabic ? "راجع النص الكامل للتعديلات" : "Review the complete edited note",
                _ => isArabic ? "هل تريد حذف هذه الملاحظة نهائيًا؟" : "Permanently delete this memory note?"
            },
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(heading, heading.Text);
        header.Children.Add(heading);

        var explanation = new TextBlock
        {
            Text = isDelete
                ? (isArabic
                    ? "سيُحذف هذا السجل من الذاكرة المحلية المشفّرة. لا يحذف ذلك نسخًا سبق تصديرها أو إرسالها في محادثة أو إلى مزود AI."
                    : "This record will be removed from encrypted local memory. Copies already exported, included in chats, or sent to an AI provider are not removed.")
                : (isArabic
                    ? "النص أدناه هو المحتوى الكامل الذي سيُحفظ محليًا مشفّرًا. لا يُرسل إلى AI عند الحفظ."
                    : "The text below is the complete content that will be saved locally in encrypted form. Saving does not send it to AI."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = isDelete ? Brushes.DarkRed : SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetName(explanation, explanation.Text);
        header.Children.Add(explanation);

        var metadata = new TextBlock
        {
            Text = (isArabic ? "التصنيف: " : "Category: ") + note.Category + Environment.NewLine +
                   (isArabic ? "العنوان: " : "Title: ") + note.Title,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
            FontWeight = FontWeights.SemiBold,
            FlowDirection = ContainsArabic(note.Title + note.Category) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            TextAlignment = ContainsArabic(note.Title + note.Category) ? TextAlignment.Right : TextAlignment.Left
        };
        AutomationProperties.SetName(metadata, (isArabic ? "تصنيف وعنوان الملاحظة" : "Memory note category and title"));
        header.Children.Add(metadata);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var preview = new TextBox
        {
            Text = note.Content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            FontSize = 14,
            Background = SystemColors.ControlLightLightBrush,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            FlowDirection = ContainsArabic(note.Content) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            TextAlignment = ContainsArabic(note.Content) ? TextAlignment.Right : TextAlignment.Left
        };
        AutomationProperties.SetName(preview, isArabic ? "المحتوى الكامل للملاحظة" : "Complete memory note content");
        AutomationProperties.SetHelpText(preview, isArabic
            ? "عرض للقراءة فقط. راجع النص كاملًا قبل تأكيد الإجراء."
            : "Read-only preview. Review the complete text before confirming the action.");
        var previewBorder = new Border
        {
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = preview
        };
        Grid.SetRow(previewBorder, 1);
        root.Children.Add(previewBorder);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = isArabic ? "إلغاء" : "Cancel",
            MinWidth = 112,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        AutomationProperties.SetName(cancel, isArabic ? "إلغاء مراجعة الملاحظة" : "Cancel note review");
        var accept = new Button
        {
            Content = action switch
            {
                MemoryNoteReviewAction.SaveNew => isArabic ? "حفظ الملاحظة" : "Save note",
                MemoryNoteReviewAction.SaveChanges => isArabic ? "حفظ التعديلات" : "Save changes",
                _ => isArabic ? "حذف نهائي" : "Delete permanently"
            },
            MinWidth = 142,
            MinHeight = 36,
            IsDefault = isSave,
            Foreground = isDelete ? Brushes.DarkRed : SystemColors.ControlTextBrush
        };
        AutomationProperties.SetName(accept, accept.Content?.ToString() ?? (isArabic ? "تأكيد" : "Confirm"));
        AutomationProperties.SetHelpText(accept, isDelete
            ? (isArabic ? "يحذف الملاحظة المحددة من مخزن الذاكرة المحلي." : "Deletes this note from the local memory store.")
            : (isArabic ? "يحفظ النص الكامل الظاهر أعلاه في الذاكرة المحلية." : "Saves the exact complete text shown above to local memory."));
        accept.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private static bool ContainsArabic(string value) =>
        value.Any(character => character is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\u08A0' and <= '\u08FF');
}
