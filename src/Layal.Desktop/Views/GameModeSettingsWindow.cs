using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// Configures opt-in foreground game detection. Only process names explicitly supplied
/// by the user are matched; the UI does not request or retain process/window details.
/// </summary>
public sealed class GameModeSettingsWindow : Window
{
    private readonly UserSettings _draft;
    private readonly Action<UserSettings> _save;
    private readonly bool _isArabic;
    private readonly CheckBox _enabled;
    private readonly TextBox _processNames;
    private readonly TextBlock _status;

    public GameModeSettingsWindow(UserSettings settings, Action<UserSettings> save, bool isArabic)
    {
        _draft = settings?.Clone() ?? throw new ArgumentNullException(nameof(settings));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _isArabic = isArabic;

        Title = isArabic ? "إعداد اكتشاف وضع الألعاب" : "Game detection settings";
        Width = 650;
        Height = 520;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "كشف اختياري يعتمد فقط على اسم عملية النافذة الأمامية وقائمة سماح تحددها أنت."
            : "Optional detection based only on the foreground window's process name and your explicit allowlist.");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = isArabic ? "الكشف التلقائي — قائمة سماح صريحة" : "Automatic detection — explicit allowlist",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _enabled = new CheckBox
        {
            Content = isArabic ? "تفعيل الكشف التلقائي للألعاب" : "Enable automatic game detection",
            IsChecked = _draft.GameModeAutoDetectionEnabled,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_enabled, isArabic ? "تفعيل كشف الألعاب التلقائي" : "Enable automatic game detection");
        AutomationProperties.SetHelpText(_enabled, isArabic
            ? "مغلق افتراضيًا. عند التفعيل يفحص ليال اسم العملية المرتبطة بالنافذة الأمامية كل بضع ثوانٍ فقط."
            : "Off by default. When enabled, Layal checks only the process name of the foreground window every few seconds.");
        Grid.SetRow(_enabled, 1);
        root.Children.Add(_enabled);

        var editor = new Grid();
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = isArabic
                ? "أسماء العمليات المسموح بها (اسم الملف دون المسار، سطر لكل لعبة)"
                : "Allowed process names (filename only, no path; one game per line)",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(label, 0);
        editor.Children.Add(label);

        var hint = new TextBlock
        {
            Text = isArabic
                ? "مثال: cs2.exe أو Cyberpunk2077.exe أو game-win64.exe. الحد 30 اسمًا؛ المسموح أحرف وأرقام وشرطة وشرطة سفلية ونقطة فقط."
                : "Example: cs2.exe, Cyberpunk2077.exe, or game-win64.exe. Up to 30 names; letters, numbers, dash, underscore, and dot only.",
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(hint, 1);
        editor.Children.Add(hint);

        _processNames = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 150,
            Text = string.Join(Environment.NewLine, _draft.GameModeAllowedProcessNames ?? new())
        };
        AutomationProperties.SetName(_processNames, isArabic ? "قائمة أسماء عمليات الألعاب" : "Game process allowlist");
        AutomationProperties.SetHelpText(_processNames, isArabic
            ? "اكتب اسم العملية فقط دون مسار أو معاملات تشغيل. كل اسم في سطر مستقل."
            : "Enter process names only, without paths or launch arguments. One name per line.");
        Grid.SetRow(_processNames, 2);
        editor.Children.Add(_processNames);

        var policy = new TextBlock
        {
            Text = isArabic
                ? "الخصوصية: لا يسرد Layal العمليات ولا يقرأ عناوين النوافذ أو سطر الأوامر أو ذاكرة اللعبة أو مدخلات لوحة المفاتيح/الماوس. يقرأ اسم العملية للنافذة الأمامية فقط ويقارنه بالقائمة. لا شبكة ولا حقن إدخال. عند عدم التطابق يُغلق HUD الذي فُتح تلقائيًا؛ وضع الألعاب اليدوي يظل متاحًا."
                : "Privacy: Layal does not enumerate processes or read window titles, command lines, game memory, or keyboard/mouse input. It reads only the foreground window's process name and compares it with this list. No network or input injection. On a non-match, an automatically launched HUD closes; manual game mode remains available.",
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetName(policy, isArabic ? "شرح الخصوصية لاكتشاف الألعاب" : "Game detection privacy explanation");
        Grid.SetRow(policy, 3);
        editor.Children.Add(policy);

        Grid.SetRow(editor, 2);
        root.Children.Add(editor);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 8),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(_status, isArabic ? "حالة إعداد الألعاب" : "Game detection settings status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = new Button
        {
            Content = isArabic ? "حفظ الإعدادات" : "Save settings",
            MinWidth = 125,
            MinHeight = 34,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(save, isArabic ? "حفظ إعداد اكتشاف الألعاب" : "Save game detection settings");
        save.Click += Save_Click;
        var cancel = new Button
        {
            Content = isArabic ? "إلغاء" : "Cancel",
            MinWidth = 95,
            MinHeight = 34,
            IsCancel = true
        };
        AutomationProperties.SetName(cancel, isArabic ? "إلغاء إعداد اكتشاف الألعاب" : "Cancel game detection settings");
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var names = GameModeProcessAllowlist.ParseAndValidate(_processNames.Text);
            var enabled = _enabled.IsChecked == true;
            if (enabled && names.Count == 0)
                throw new ArgumentException(_isArabic
                    ? "أدخل اسم عملية لعبة واحدًا على الأقل، أو اترك الكشف معطلًا."
                    : "Enter at least one game process name, or leave automatic detection disabled.");

            _draft.GameModeAutoDetectionEnabled = enabled;
            _draft.GameModeAllowedProcessNames = names;
            _save(_draft.Clone());
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حفظ الإعدادات: " : "Could not save settings: ") + ex.Message;
        }
    }
}
