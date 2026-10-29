using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Layal.Views;

/// <summary>
/// A user-invoked, minimal always-on-top HUD. It does not inspect games, windows, input,
/// graphics memory, or processes and never injects input into a game.
/// </summary>
public sealed class GameModeHudWindow : Window
{
    private readonly bool _isArabic;
    private readonly Func<string> _getActivity;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _clock;
    private readonly TextBlock _activity;
    private bool _exitRaised;

    public event EventHandler? ExitRequested;

    public GameModeHudWindow(bool isArabic, Func<string> getActivity)
    {
        _isArabic = isArabic;
        _getActivity = getActivity ?? throw new ArgumentNullException(nameof(getActivity));

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
            ? "HUD يدوي محدود. لا يكتشف الألعاب ولا يراقب العمليات أو الشاشة، ولا يتحكم في اللعبة."
            : "A minimal manual HUD. It does not detect games or monitor processes/screens, and never controls a game.");

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = isArabic ? "وضع الألعاب — يدوي" : "Game mode — manual",
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
            Text = "Alt+Shift+S · Esc",
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
            ? "يغلق HUD ويعيد التذكيرات إلى العمل وفق إعداداتها."
            : "Closes the HUD and restores reminders according to their settings.");
        exit.Click += (_, _) =>
        {
            RaiseExitRequested();
            Close();
        };
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
                ? "لا توجد مهمة نشطة.\nسؤال سريع: Alt+Shift+S\nإلغاء العملية الحالية: Esc"
                : "No active task.\nQuick Ask: Alt+Shift+S\nCancel current operation: Esc")
            : activity + Environment.NewLine +
              (_isArabic ? "سؤال سريع: Alt+Shift+S · إلغاء: Esc" : "Quick Ask: Alt+Shift+S · Cancel: Esc");
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
