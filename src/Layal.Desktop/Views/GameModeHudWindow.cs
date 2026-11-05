using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Layal.Views;

/// <summary>
/// A minimal always-on-top HUD that can be opened manually or by the user's explicit foreground
/// process allowlist. It never reads window titles, game memory, or input and never injects input.
/// </summary>
public sealed class GameModeHudWindow : Window
{
    private readonly bool _isArabic;
    private readonly Func<string> _getActivity;
    private readonly string _shortcutDisplay;
    private readonly bool _automaticallyLaunched;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _clock;
    private readonly TextBlock _activity;
    private bool _exitRaised;
    private bool _closingProgrammatically;

    /// <summary>True when the user dismissed the HUD with its button or window chrome.</summary>
    public bool WasDismissedByUser { get; private set; }

    public event EventHandler? ExitRequested;

    public GameModeHudWindow(
        bool isArabic,
        Func<string> getActivity,
        string shortcutDisplay = "Alt+Shift+S",
        bool automaticallyLaunched = false)
    {
        _isArabic = isArabic;
        _getActivity = getActivity ?? throw new ArgumentNullException(nameof(getActivity));
        _shortcutDisplay = string.IsNullOrWhiteSpace(shortcutDisplay) ? "Alt+Shift+S" : shortcutDisplay.Trim();
        _automaticallyLaunched = automaticallyLaunched;

        Title = isArabic ? "ليال | وضع الألعاب" : "Layal | Game mode";
        Width = 335;
        Height = 172;
        MinWidth = Width;
        MinHeight = Height;
        MaxWidth = Width;
        MaxHeight = Height;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 16);
        Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Top + 28);
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "HUD يدوي أو تم فتحه من قائمة السماح. لا يقرأ عناوين النوافذ أو ذاكرة اللعبة أو مدخلاتها، ولا يتحكم في اللعبة."
            : "A manual or allowlist-launched HUD. It does not read window titles, game memory, or input, and never controls a game.");

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = isArabic
                ? (_automaticallyLaunched ? "وضع الألعاب — تلقائي بقائمة السماح" : "وضع الألعاب — يدوي")
                : (_automaticallyLaunched ? "Game mode — allowlist detected" : "Game mode — manual"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _clock = new TextBlock
        {
            Text = DateTimeOffset.Now.ToString("t"),
            Margin = new Thickness(0, 3, 0, 5),
            Foreground = Application.Current.TryFindResource("Layal.Cyan") as Brush ?? Brushes.Teal
        };
        AutomationProperties.SetName(_clock, isArabic ? "الوقت الحالي" : "Current time");
        AutomationProperties.SetLiveSetting(_clock, AutomationLiveSetting.Polite);
        Grid.SetRow(_clock, 1);
        root.Children.Add(_clock);

        _activity = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(_activity, isArabic ? "حالة ليال الحالية" : "Current Layal activity");
        AutomationProperties.SetHelpText(_activity, isArabic
            ? "حالة المهمة الحالية؛ لا تتضمن بيانات من اللعبة."
            : "Current task status; no game data is inspected.");
        Grid.SetRow(_activity, 2);
        root.Children.Add(_activity);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var shortcut = new TextBlock
        {
            Text = _shortcutDisplay + " · " + (_isArabic ? "Esc داخل ليال" : "Esc in Layal"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(shortcut, isArabic ? "اختصارات ليال" : "Layal shortcuts");
        footer.Children.Add(shortcut);
        var exit = new Button
        {
            Content = isArabic ? "إنهاء الوضع" : "Exit mode",
            MinHeight = 30,
            MinWidth = 90
        };
        AutomationProperties.SetName(exit, isArabic ? "إنهاء وضع الألعاب" : "Exit game mode");
        AutomationProperties.SetHelpText(exit, isArabic
            ? "يخفي HUD. تظل التذكيرات التي أنشأتها مستمرة؛ وتستأنف المتابعة الاستباقية بعد مغادرة اللعبة المسموح بها."
            : "Hides the HUD. Your saved reminders continue; proactive check-ins resume after focus leaves the allowlisted game.");
        exit.Click += (_, _) => CloseByUser();
        footer.Children.Add(exit);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        UpdateStatus();
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _clock.Text = DateTimeOffset.Now.ToString("t");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var activity = _getActivity();
        _activity.Text = string.IsNullOrWhiteSpace(activity)
            ? (_isArabic
                ? $"لا توجد مهمة نشطة.\nسؤال سريع: {_shortcutDisplay}\nالإلغاء من نافذة ليال: Esc"
                : $"No active task.\nQuick Ask: {_shortcutDisplay}\nCancel inside Layal: Esc")
            : activity + Environment.NewLine +
              (_isArabic ? $"سؤال سريع: {_shortcutDisplay} · الإلغاء داخل ليال: Esc" : $"Quick Ask: {_shortcutDisplay} · Cancel in Layal: Esc");
    }

    /// <summary>Close after the user explicitly chooses to dismiss the HUD.</summary>
    public void CloseByUser()
    {
        if (!IsVisible) return;
        WasDismissedByUser = true;
        Close();
    }

    /// <summary>Close because detection ended, settings changed, or Layal is shutting down.</summary>
    public void CloseFromDetection()
    {
        if (!IsVisible) return;
        _closingProgrammatically = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingProgrammatically)
            WasDismissedByUser = true;
        base.OnClosing(e);
    }

    private void RaiseExitRequested()
    {
        if (_exitRaised) return;
        _exitRaised = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        RaiseExitRequested();
        base.OnClosed(e);
    }
}
