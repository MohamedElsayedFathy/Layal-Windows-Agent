using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
    private void OpenQuickAskWindow()
    {
        if (_incognitoMode)
        {
            SetChatActivityStatus(IsArabic
                ? "السؤال السريع غير متاح أثناء المحادثة الخاصة؛ استخدم نافذة المحادثة المؤقتة فقط."
                : "Quick Ask is unavailable during private chat; use the temporary main chat only.");
            return;
        }
        if (AppRuntime.IsExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        ShowFromTray();
        MainTabs.SelectedItem = ChatTab;

        if (_quickAskWindow is { } existing)
        {
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            existing.FocusInput();
            return;
        }

        var quickAsk = new QuickAskWindow(
            IsArabic,
            SubmitQuickAskAsync,
            CancelQuickAskRequest,
            GlobalHotkeyService.GetDisplayName(AppRuntime.Settings.QuickAskHotkeyPreset))
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _quickAskWindow = quickAsk;
        quickAsk.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickAskWindow, quickAsk))
                _quickAskWindow = null;
        };
        quickAsk.Show();
        quickAsk.Activate();
        quickAsk.FocusInput();
    }

    private void CancelQuickAskRequest() => _chatCancellation?.Cancel();

    private async Task<QuickAskResult> SubmitQuickAskAsync(string question)
    {
        var blockReason = QuickAskRequestPolicy.Evaluate(
            isPrivateChat: _incognitoMode,
            chatRequestBusy: _chatCancellation is not null,
            voiceConversationBusy: _voiceConversationCancellation is not null,
            hasPendingDocument: !string.IsNullOrWhiteSpace(_pendingDocumentText),
            hasPendingImage: _pendingVisionImageBytes is { Length: > 0 });
        if (blockReason != QuickAskBlockReason.None)
        {
            var message = blockReason switch
            {
                QuickAskBlockReason.PrivateChat => IsArabic
                    ? "السؤال السريع غير متاح أثناء المحادثة الخاصة. استخدم نافذة المحادثة المؤقتة فقط."
                    : "Quick Ask is unavailable during private chat. Use the temporary main chat only.",
                QuickAskBlockReason.RequestBusy => IsArabic
                    ? "هناك طلب أو جلسة صوتية قيد التشغيل. أوقفها أولًا ثم أعد السؤال."
                    : "A chat request or voice session is already running. Stop it before asking again.",
                QuickAskBlockReason.PendingAttachment => IsArabic
                    ? "هناك مستند أو صورة مرفقة بالمحادثة. Quick Ask لا يرسل المرفقات؛ أزل المرفق أو أرسله أولًا من نافذة المحادثة."
                    : "A document or image is attached to the chat. Quick Ask never sends pending attachments; remove or send it from the main chat first.",
                _ => IsArabic ? "تعذر بدء السؤال السريع." : "Quick Ask could not start."
            };
            return QuickAskResult.Failure(message);
        }

        var priorDraft = ChatInput.Text;
        // Use object identity, not a collection count: AddMessage may evict old entries at the chat-history limit.
        var previousMessages = new HashSet<ChatMessage>(ChatMessages, ReferenceEqualityComparer.Instance);
        ChatInput.Text = question;
        Task sendTask;
        try
        {
            // Reuse the native local conversation route while keeping Quick Ask silent.
            sendTask = SendMessageAsync(suppressVoiceReply: true);
        }
        finally
        {
            // SendMessageAsync captures and clears the question synchronously before its first await.
            // Restore the user's unfinished main-chat draft immediately and leave it untouched.
            ChatInput.Text = priorDraft;
        }

        await sendTask;
        var newMessages = ChatMessages.Where(message => !previousMessages.Contains(message)).ToList();
        var answer = newMessages.LastOrDefault(message =>
            string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(message.Content));
        if (answer is not null)
            return QuickAskResult.Success(answer.Content);

        var status = newMessages.LastOrDefault(message =>
            string.Equals(message.Role, "status", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(message.Content));
        return QuickAskResult.Failure(status?.Content ?? (IsArabic
            ? "لم تُنتج المحادثة إجابة نصية. افتح سجل المحادثة لمعرفة الحالة."
            : "The chat did not return a text answer. Check the main conversation for its status."));
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // With a custom caption (WindowChrome) a maximized window overhangs the screen by the resize border.
        var maximized = WindowState == WindowState.Maximized;
        WindowRoot.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
    }

    private bool IsArabic => AppRuntime.Settings.Language == "ar";
    private bool IsDarkTheme => AppRuntime.Settings.ThemeMode == "dark";

    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    private void ApplyWindowBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            WindowBackdropService.TryApply(handle, IsDarkTheme, SystemParameters.HighContrast);
    }

    private void ApplyTheme()
    {
        // Replace the theme brushes and gradients themselves, not only their source colors.
        // DynamicResource consumers then receive the replacement even if a control was
        // constructed before the user switched themes.
        var highContrast = SystemParameters.HighContrast;
        var palette = UiThemePalette.Create(IsDarkTheme, highContrast, AppRuntime.Settings.ThemePreset);

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value)!;
            colors[key] = color;
            Application.Current.Resources[key] = color;
        }

        void SetSolidBrush(string brushKey, string colorKey) =>
            Application.Current.Resources[brushKey] = new SolidColorBrush(colors[colorKey]);

        SetSolidBrush("Layal.Background", "Layal.Color.Background");
        SetSolidBrush("Layal.Panel", "Layal.Color.Surface");
        SetSolidBrush("Layal.PanelAlt", "Layal.Color.SurfaceRaised");
        SetSolidBrush("Layal.PanelHover", "Layal.Color.SurfaceHover");
        SetSolidBrush("Layal.Border", "Layal.Color.Border");
        SetSolidBrush("Layal.Cyan", "Layal.Color.Cyan");
        SetSolidBrush("Layal.Purple", "Layal.Color.Purple");
        SetSolidBrush("Layal.Text", "Layal.Color.Text");
        SetSolidBrush("Layal.Muted", "Layal.Color.Muted");
        SetSolidBrush("Layal.ScrollThumb", "Layal.Color.ScrollThumb");
        SetSolidBrush("Layal.Success", "Layal.Color.Success");
        SetSolidBrush("Layal.Warning", "Layal.Color.Warning");
        SetSolidBrush("Layal.Danger", "Layal.Color.Danger");
        SetSolidBrush("Layal.Focus", "Layal.Color.Focus");
        SetSolidBrush("Layal.White", "Layal.Color.Surface");
        SetSolidBrush("Layal.BrandBorder", "Layal.Color.BrandBorder");
        SetSolidBrush("Layal.DecorativeAmber", "Layal.Color.BrandDot");
        SetSolidBrush("Layal.DangerSurface", "Layal.Color.DangerSurface");
        SetSolidBrush("Layal.DangerBorder", "Layal.Color.DangerBorder");
        SetSolidBrush("Layal.HoverSurface", "Layal.Color.HoverSurface");
        SetSolidBrush("Layal.MenuHover", "Layal.Color.MenuHover");
        SetSolidBrush("Layal.MenuHoverText", "Layal.Color.MenuHoverText");
        SetSolidBrush("Layal.InputFocus", "Layal.Color.InputFocus");
        SetSolidBrush("Layal.ListHover", "Layal.Color.ListHover");
        SetSolidBrush("Layal.ListSelected", "Layal.Color.ListSelected");
        SetSolidBrush("Layal.NavSelected", "Layal.Color.NavSelected");
        SetSolidBrush("Layal.NavSelectedBorder", "Layal.Color.NavSelectedBorder");
        SetSolidBrush("Layal.NavHover", "Layal.Color.NavHover");
        foreach (var iconName in new[] { "Chat", "Overview", "Tasks", "Skills", "Cameras", "Memory", "Api", "Settings" })
        {
            SetSolidBrush($"Layal.Icon.{iconName}Background", $"Layal.Color.Icon{iconName}Background");
            SetSolidBrush($"Layal.Icon.{iconName}Foreground", $"Layal.Color.Icon{iconName}Foreground");
        }
        SetSolidBrush("Layal.GlassFill", "Layal.Color.Glass");
        SetSolidBrush("Layal.GlassStrongFill", "Layal.Color.GlassStrong");
        SetSolidBrush("Layal.GlassBorderBrush", "Layal.Color.GlassBorder");
        SetSolidBrush("Layal.OnAccent", "Layal.Color.OnAccent");

        LinearGradientBrush MakeGradient(params (string Key, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            foreach (var stop in stops)
                brush.GradientStops.Add(new GradientStop(colors[stop.Key], stop.Offset));
            return brush;
        }

        Application.Current.Resources["Layal.Background"] = MakeGradient(
            ("Layal.Color.Background", 0),
            ("Layal.Color.BackgroundEnd", 1));
        Application.Current.Resources["Layal.HeaderGradient"] = MakeGradient(
            ("Layal.Color.HeaderStart", 0),
            ("Layal.Color.HeaderMiddle", 0.56),
            ("Layal.Color.HeaderEnd", 1));
        Application.Current.Resources["Layal.CardGradient"] = MakeGradient(
            ("Layal.Color.CardStart", 0),
            ("Layal.Color.CardEnd", 1));
        Application.Current.Resources["Layal.PageHeroGradient"] = MakeGradient(
            ("Layal.Color.CardStart", 0),
            ("Layal.Color.HeaderMiddle", 0.58),
            ("Layal.Color.CardEnd", 1));
        Application.Current.Resources["Layal.PrimaryButtonGradient"] = MakeGradient(
            ("Layal.Color.ButtonStart", 0),
            ("Layal.Color.ButtonEnd", 1));
        Application.Current.Resources["Layal.Backdrop"] = MakeGradient(
            ("Layal.Color.BackdropStart", 0),
            ("Layal.Color.BackdropMiddle", 0.55),
            ("Layal.Color.BackdropEnd", 1));
        Application.Current.Resources["Layal.AccentGradient"] = MakeGradient(
            ("Layal.Color.AccentStart", 0),
            ("Layal.Color.AccentEnd", 1));
        Application.Current.Resources["Layal.CardShadow"] = new DropShadowEffect
        {
            Color = colors["Layal.Color.Shadow"],
            BlurRadius = 18,
            ShadowDepth = 1,
            Opacity = 0.10
        };
        var backdropBase = colors["Layal.Color.Background"];
        var opaqueWindowSurface = new SolidColorBrush(Color.FromRgb(
            backdropBase.R, backdropBase.G, backdropBase.B));
        Background = opaqueWindowSurface;
        Application.Current.Resources["Layal.AppBackdropWash"] = new SolidColorBrush(opaqueWindowSurface.Color);

        if (highContrast)
        {
            // High Contrast is controlled by Windows and must override app-authored gradients,
            // shadows and accent surfaces rather than merely recoloring their source stops.
            var systemSurface = new SolidColorBrush(SystemColors.WindowColor);
            Application.Current.Resources["Layal.Background"] = systemSurface;
            Application.Current.Resources["Layal.HeaderGradient"] = systemSurface;
            Application.Current.Resources["Layal.CardGradient"] = systemSurface;
            Application.Current.Resources["Layal.PrimaryButtonGradient"] = systemSurface;
            Application.Current.Resources["Layal.Backdrop"] = systemSurface;
            Application.Current.Resources["Layal.AccentGradient"] = new SolidColorBrush(SystemColors.HighlightColor);
            Application.Current.Resources["Layal.CardShadow"] = new DropShadowEffect
            {
                Color = SystemColors.WindowTextColor,
                BlurRadius = 0,
                ShadowDepth = 0,
                Opacity = 0
            };
            Application.Current.Resources["Layal.AppBackdropWash"] = new SolidColorBrush(SystemColors.WindowColor);
        }

        AppRuntime.Tray?.SetTheme(IsDarkTheme, highContrast);
        LayalWindowChrome.RefreshAll(IsDarkTheme, highContrast);
        ApplyWindowBackdrop();
    }

}
