using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Layal.Services;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Layal.Views;

/// <summary>
/// Application-owned, theme-aware replacement for native MessageBox prompts.
/// Keeps consent and confirmation surfaces inside Layal's visual and accessibility system.
/// </summary>
internal static class LayalDialogService
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption = "",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowCore(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption = "",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowCore(owner, messageBoxText, caption, button, icon, defaultResult);

    private static MessageBoxResult ShowCore(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult requestedDefault)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            throw new InvalidOperationException("Layal dialogs must be shown on the WPF UI dispatcher.");

        var isArabic = string.Equals(AppRuntime.Settings.Language, "ar", StringComparison.OrdinalIgnoreCase);
        var highContrast = SystemParameters.HighContrast;
        var dark = string.Equals(AppRuntime.Settings.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase);
        var safeDefault = GetSafeDefault(buttons);
        var defaultResult = IsAllowedResult(buttons, requestedDefault) ? requestedDefault : safeDefault;
        var selectedResult = MessageBoxResult.None;
        var hasBackdrop = false;

        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(caption) ? (isArabic ? "رسالة من ليال" : "Layal message") : caption,
            Width = 600,
            MinWidth = 420,
            MaxWidth = 760,
            MaxHeight = 720,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = true,
            Background = Brushes.Transparent,
            Foreground = highContrast ? SystemColors.WindowTextBrush : ResourceBrush("Layal.Text", Brushes.Black),
            FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Focusable = true
        };

        if (owner is { IsVisible: true } && owner != dialog)
            dialog.Owner = owner;

        var surface = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22),
            Effect = highContrast ? null : ResourceEffect("Layal.CardShadow")
        };
        surface.SetResourceReference(Border.BackgroundProperty, highContrast ? "Layal.Panel" : "Layal.GlassFill");
        surface.SetResourceReference(Border.BorderBrushProperty, highContrast ? "Layal.Border" : "Layal.GlassBorderBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.Child = layout;

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var closeButton = new Button
        {
            Content = "×",
            Width = 34,
            Height = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 20,
            FontWeight = FontWeights.Normal
        };
        closeButton.SetResourceReference(Control.BackgroundProperty, highContrast ? "Layal.Panel" : "Layal.GlassStrongFill");
        AutomationProperties.SetName(closeButton, isArabic ? "إغلاق نافذة التأكيد" : "Close dialog");
        DockPanel.SetDock(closeButton, Dock.Right);
        header.Children.Add(closeButton);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyphShell = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 11, 0),
            Child = new TextBlock
            {
                Text = GetIconGlyph(icon),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        var iconColorKey = GetIconBrushKey(icon);
        glyphShell.SetResourceReference(Border.BackgroundProperty, highContrast ? "Layal.Panel" : "Layal.DangerSurface");
        ((TextBlock)glyphShell.Child).SetResourceReference(TextBlock.ForegroundProperty, iconColorKey);
        titleRow.Children.Add(glyphShell);

        var titleText = new TextBlock
        {
            Text = dialog.Title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 470
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Layal.Text");
        titleRow.Children.Add(titleText);
        header.Children.Add(titleRow);

        var message = new TextBlock
        {
            Text = messageBoxText ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            Margin = new Thickness(0, 0, 0, 18),
            IsTextSelectionEnabled = true
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, highContrast ? "Layal.Text" : "Layal.Text");
        AutomationProperties.SetName(message, isArabic ? "تفاصيل الرسالة" : "Message details");
        AutomationProperties.SetHelpText(message, messageBoxText ?? string.Empty);

        var messageScroll = new ScrollViewer
        {
            Content = message,
            MaxHeight = 470,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        Grid.SetRow(messageScroll, 1);
        layout.Children.Add(messageScroll);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            FlowDirection = dialog.FlowDirection,
            Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);

        Button? defaultButton = null;
        foreach (var result in GetButtonResults(buttons))
        {
            var isPrimary = result is MessageBoxResult.Yes or MessageBoxResult.OK;
            var action = new Button
            {
                Content = GetButtonLabel(result, isArabic),
                MinWidth = 96,
                MinHeight = 38,
                Margin = new Thickness(5, 0, 0, 0),
                Padding = new Thickness(14, 8, 14, 8),
                IsDefault = result == defaultResult
            };
            action.SetResourceReference(Control.BackgroundProperty,
                highContrast ? "Layal.Panel" : isPrimary ? "Layal.PrimaryButtonGradient" : "Layal.GlassStrongFill");
            action.SetResourceReference(Control.BorderBrushProperty,
                highContrast ? "Layal.Border" : isPrimary ? "Layal.AccentGradient" : "Layal.GlassBorderBrush");
            action.SetResourceReference(Control.ForegroundProperty,
                highContrast ? "Layal.Text" : isPrimary ? "Layal.OnAccent" : "Layal.Text");
            AutomationProperties.SetName(action, GetButtonLabel(result, isArabic));
            action.Click += (_, _) => Complete(result);
            actions.Children.Add(action);
            if (result == defaultResult)
                defaultButton = action;
        }

        void Complete(MessageBoxResult result)
        {
            selectedResult = result;
            dialog.DialogResult = result is MessageBoxResult.Yes or MessageBoxResult.OK;
        }

        closeButton.Click += (_, _) => Complete(safeDefault);
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None)
                return;
            e.Handled = true;
            Complete(safeDefault);
        };
        dialog.Closed += (_, _) =>
        {
            if (selectedResult == MessageBoxResult.None)
                selectedResult = safeDefault;
        };
        dialog.ContentRendered += (_, _) =>
        {
            if (defaultButton is not null)
                defaultButton.Focus();
            else
                dialog.Focus();
        };
        dialog.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(dialog).Handle;
            hasBackdrop = WindowBackdropService.TryApply(handle, dark, highContrast);
            if (!hasBackdrop && !highContrast)
                surface.SetResourceReference(Border.BackgroundProperty, "Layal.Panel");
        };

        AutomationProperties.SetName(dialog, dialog.Title);
        AutomationProperties.SetHelpText(dialog, isArabic ? "نافذة مؤقتة تتطلب مراجعة الرسالة واختيار إجراء." : "Temporary prompt. Review the message and choose an action.");
        dialog.Content = surface;
        LayalWindowChrome.ApplyGlass(dialog, isArabic);
        _ = dialog.ShowDialog();
        return selectedResult == MessageBoxResult.None ? safeDefault : selectedResult;
    }

    private static IReadOnlyList<MessageBoxResult> GetButtonResults(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.Cancel, MessageBoxResult.OK },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.No, MessageBoxResult.Yes },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.No, MessageBoxResult.Cancel, MessageBoxResult.Yes },
            _ => new[] { MessageBoxResult.OK }
        };

    private static MessageBoxResult GetSafeDefault(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };

    private static bool IsAllowedResult(MessageBoxButton buttons, MessageBoxResult result) =>
        GetButtonResults(buttons).Contains(result);

    private static string GetButtonLabel(MessageBoxResult result, bool isArabic) =>
        result switch
        {
            MessageBoxResult.Yes => isArabic ? "نعم" : "Yes",
            MessageBoxResult.No => isArabic ? "لا" : "No",
            MessageBoxResult.Cancel => isArabic ? "إلغاء" : "Cancel",
            _ => isArabic ? "حسنًا" : "OK"
        };

    private static string GetIconGlyph(MessageBoxImage icon) =>
        icon switch
        {
            MessageBoxImage.Error => "×",
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            _ => "i"
        };

    private static string GetIconBrushKey(MessageBoxImage icon) =>
        icon switch
        {
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "Layal.Danger",
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "Layal.Warning",
            _ => "Layal.Cyan"
        };

    private static Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static System.Windows.Media.Effects.Effect? ResourceEffect(string key) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Effects.Effect;
}
