using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>Creates saved prompts and returns a chosen prompt for review; never executes a task itself.</summary>
public sealed class SavedRoutineManagerWindow : Window
{
    private readonly SavedRoutineService _service;
    private readonly bool _isArabic;
    private readonly Action<string> _onUse;
    private readonly TextBox _nameInput;
    private readonly TextBox _goalInput;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ObservableCollection<RoutineListEntry> _entries = new();

    public SavedRoutineManagerWindow(SavedRoutineService service, bool isArabic, Action<string> onUse)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _onUse = onUse ?? throw new ArgumentNullException(nameof(onUse));
        _isArabic = isArabic;
        Title = isArabic ? "الروتينات المحفوظة" : "Saved routines";
        Width = 820;
        Height = 710;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "الروتين قالب نصي فقط، وليس سكربتًا. اختياره يملأ مسودة المهمة للمراجعة ولا يشغّلها تلقائيًا."
            : "A routine is a text template, not a script. Choosing one fills the task draft for review and never runs it automatically.");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = isArabic ? "روتينات آمنة قابلة للمراجعة" : "Reviewable saved routines",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var nameLabel = new TextBlock { Text = isArabic ? "اسم الروتين" : "Routine name" };
        Grid.SetRow(nameLabel, 0);
        form.Children.Add(nameLabel);

        _nameInput = new TextBox
        {
            MinHeight = 32,
            MaxLength = SavedRoutineService.MaximumNameCharacters,
            Margin = new Thickness(0, 3, 0, 8)
        };
        AutomationProperties.SetName(_nameInput, isArabic ? "اسم الروتين المحفوظ" : "Saved routine name");
        Grid.SetRow(_nameInput, 1);
        form.Children.Add(_nameInput);

        var goalPanel = new StackPanel();
        goalPanel.Children.Add(new TextBlock
        {
            Text = isArabic
                ? "تعليمات المهمة (حتى 2000 حرف). ستظهر الموافقات المعتادة قبل أي إجراء."
                : "Task prompt (up to 2,000 characters). Normal approvals still appear before any action.",
            TextWrapping = TextWrapping.Wrap
        });
        _goalInput = new TextBox
        {
            MinHeight = 85,
            MaxHeight = 140,
            MaxLength = SavedRoutineService.MaximumGoalCharacters,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 3, 0, 8)
        };
        AutomationProperties.SetName(_goalInput, isArabic ? "تعليمات الروتين المحفوظ" : "Saved routine task prompt");
        AutomationProperties.SetHelpText(_goalInput, isArabic
            ? "اكتب الهدف فقط. لن تُنفذ أوامر أو سكربتات مخزنة."
            : "Store a goal only. No stored commands or scripts are executed.");
        goalPanel.Children.Add(_goalInput);
        var saveButton = new Button
        {
            Content = isArabic ? "حفظ قالب الروتين" : "Save routine template",
            MinWidth = 160,
            MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsDefault = false
        };
        AutomationProperties.SetName(saveButton, isArabic ? "حفظ قالب الروتين المشفر" : "Save encrypted routine template");
        AutomationProperties.SetHelpText(saveButton, isArabic
            ? "يحفظ الاسم والهدف محليًا بتشفير DPAPI دون تشغيلهما."
            : "Stores the name and goal locally with DPAPI encryption without executing them.");
        saveButton.Click += SaveButton_Click;
        goalPanel.Children.Add(saveButton);
        Grid.SetRow(goalPanel, 2);
        form.Children.Add(goalPanel);
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _list = new ListBox
        {
            ItemsSource = _entries,
            DisplayMemberPath = nameof(RoutineListEntry.Description),
            Padding = new Thickness(6),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray
        };
        AutomationProperties.SetName(_list, isArabic ? "قائمة الروتينات المحفوظة" : "Saved routines list");
        AutomationProperties.SetHelpText(_list, isArabic
            ? "حدد قالبًا ثم أدرجه في مسودة المهمة أو احذفه."
            : "Select a template to load into the task draft or delete it.");
        _list.SelectionChanged += (_, _) => UpdateSelectedPreview();
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(_status, isArabic ? "حالة الروتينات" : "Routine status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var useButton = new Button
        {
            Content = isArabic ? "استخدام في مسودة المهمة" : "Use in task draft",
            MinWidth = 190,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        AutomationProperties.SetName(useButton, isArabic ? "استخدام الروتين بعد مراجعته" : "Use routine after review");
        AutomationProperties.SetHelpText(useButton, isArabic
            ? "يملأ حقل وصف المهمة فقط؛ لا يبدأ التنفيذ ولا يتجاوز أي موافقة."
            : "Fills only the task description; it does not start execution or bypass approvals.");
        useButton.Click += UseButton_Click;

        var deleteButton = new Button
        {
            Content = isArabic ? "حذف المحدد" : "Delete selected",
            MinWidth = 120,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(deleteButton, isArabic ? "حذف الروتين المحدد" : "Delete selected routine");
        deleteButton.Click += DeleteButton_Click;

        var closeButton = new Button
        {
            Content = isArabic ? "إغلاق" : "Close",
            MinWidth = 90,
            MinHeight = 34,
            IsCancel = true
        };
        footer.Children.Add(useButton);
        footer.Children.Add(deleteButton);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        RefreshList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var record = _service.Create(_nameInput.Text, _goalInput.Text);
            _nameInput.Clear();
            _goalInput.Clear();
            RefreshList(record.Id);
            _status.Text = _isArabic
                ? "تم حفظ الروتين محليًا بتشفير DPAPI. لم يتم تشغيله."
                : "Routine saved locally with DPAPI. It was not executed.";
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حفظ الروتين: " : "Could not save routine: ") + ex.Message;
        }
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _status.Text = _isArabic ? "اختر روتينًا أولًا." : "Select a routine first.";
            return;
        }

        _onUse(selected.Record.Goal);
        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _status.Text = _isArabic ? "اختر روتينًا أولًا." : "Select a routine first.";
            return;
        }

        if (_service.Delete(selected.Record.Id))
            _status.Text = _isArabic ? "تم حذف الروتين المحدد." : "Selected routine deleted.";
        else
            _status.Text = _isArabic ? "لم يعد الروتين موجودًا." : "The routine no longer exists.";
        RefreshList();
    }

    private void UpdateSelectedPreview()
    {
        if (_list.SelectedItem is RoutineListEntry selected)
            _status.Text = (_isArabic ? "معاينة الهدف: " : "Goal preview: ") + selected.Record.Goal;
    }

    private void RefreshList(string? selectedId = null)
    {
        _entries.Clear();
        foreach (var record in _service.GetAll())
            _entries.Add(new RoutineListEntry(record, $"{record.Name} — {record.UpdatedAt.ToLocalTime():g}"));

        if (!string.IsNullOrWhiteSpace(selectedId))
            _list.SelectedItem = _entries.FirstOrDefault(entry => entry.Record.Id == selectedId);

        if (_entries.Count == 0)
            _status.Text = _isArabic ? "لا توجد روتينات محفوظة." : "No saved routines.";
    }

    private sealed record RoutineListEntry(SavedRoutineRecord Record, string Description);
}
