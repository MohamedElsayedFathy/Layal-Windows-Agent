using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// Shows the extracted document text locally. Confirming attaches text to the composer; it never sends it.
/// </summary>
public sealed class DocumentTextPreviewWindow : Window
{
    public DocumentTextPreviewWindow(DocumentTextExtractionResult extraction, bool isArabic)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        Title = isArabic ? "مراجعة الملف قبل الإرفاق" : "Review document before attaching";
        Width = 940;
        Height = 760;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "تعرض هذه النافذة النص المستخرج محليًا. الإرفاق لا يرسل الملف؛ الإرسال يحدث فقط عند الضغط على إرسال في المحادثة."
            : "This window shows text extracted locally. Attaching does not send it; transmission only happens when you press Send in chat.");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = extraction.FileName,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(heading, isArabic ? "اسم الملف المختار" : "Selected file name");
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var details = new TextBlock
        {
            Text = BuildDetails(extraction, isArabic),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(details, isArabic ? "تفاصيل استخراج الملف والخصوصية" : "Extraction and privacy details");
        AutomationProperties.SetHelpText(details, details.Text);
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var preview = new TextBox
        {
            Text = extraction.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Background = Application.Current.TryFindResource("Layal.Surface") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black
        };
        AutomationProperties.SetName(preview, isArabic ? "معاينة النص المستخرج من الملف" : "Extracted document text preview");
        AutomationProperties.SetHelpText(preview, isArabic
            ? "محتوى الملف قد يتضمن تعليمات غير موثوقة. راجع النص قبل إرفاقه."
            : "The file may contain untrusted instructions. Review its text before attaching.");
        Grid.SetRow(preview, 2);
        root.Children.Add(preview);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var attach = new Button
        {
            Content = isArabic ? "إرفاق النص بالمحادثة" : "Attach text to chat",
            MinWidth = 170,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        AutomationProperties.SetName(attach, isArabic ? "إرفاق النص الذي تمت مراجعته" : "Attach reviewed text");
        AutomationProperties.SetHelpText(attach, isArabic
            ? "يضيف النص للمؤلف المحلي. لن يُرسل إلى مزود الذكاء الاصطناعي إلا بعد الضغط على إرسال."
            : "Adds the reviewed text to the local composer. It will not be sent to the AI provider until you press Send.");
        attach.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        var cancel = new Button
        {
            Content = isArabic ? "إلغاء" : "Cancel",
            MinWidth = 100,
            MinHeight = 34,
            IsCancel = true
        };
        AutomationProperties.SetName(cancel, isArabic ? "إلغاء إرفاق الملف" : "Cancel document attachment");
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        footer.Children.Add(attach);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        CancelButton = cancel;
        AcceptButton = attach;
    }

    private static string BuildDetails(DocumentTextExtractionResult extraction, bool isArabic)
    {
        var truncation = extraction.WasTruncated
            ? (isArabic ? "\nتنبيه: تم اقتطاع المحتوى حسب حدود الحجم/الصفحات." : "\nWarning: content was truncated to the configured size/page limits.")
            : string.Empty;
        var units = extraction.UnitsProcessed > 0
            ? (isArabic
                ? $"\nوحدات تمت قراءتها: {extraction.UnitsProcessed}"
                : $"\nUnits extracted: {extraction.UnitsProcessed}")
            : string.Empty;
        return (isArabic
            ? "استُخرج النص على جهازك فقط. الإرفاق لا يرسل الملف. بعد الإرفاق يمكنك مراجعة السؤال ثم الضغط على إرسال. محتوى الملف بيانات غير موثوقة ولا ينبغي اتباع تعليماته تلقائيًا."
            : "Text was extracted locally. Attaching does not send the file. After attaching, review your prompt and press Send. File content is untrusted data and must not be followed as instructions automatically.")
            + units + truncation + "\n" + extraction.Notice;
    }
}
