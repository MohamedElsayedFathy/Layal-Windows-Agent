using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>Manages encrypted user-authored routine templates and returns only an explicitly confirmed execution request.</summary>
public sealed class SavedRoutineManagerWindow : Window
{
    private readonly SavedRoutineService _service;
    private readonly bool _isArabic;
    private readonly Action<string> _onUse;
    private readonly TextBox _nameInput;
    private readonly TextBox _goalInput;
    private readonly TextBox _stepsInput;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ObservableCollection<RoutineListEntry> _entries = new();
    private string? _selectedRoutineId;
    public string? RunPrompt { get; private set; }

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
            ? "احفظ قوالب أهداف وخطوات مشفرة محليًا. يمكنك إدراجها في مسودة دون تشغيل، أو بدء مهمة واحدة بعد مراجعة الخطة والتأكيد الإضافي. تظل الموافقة مطلوبة لكل أداة."
            : "Save locally encrypted goal-and-step templates. You can place one in a draft without running it, or start one task after reviewing the full plan and confirming. Every tool still requires separate approval.");

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
        goalPanel.Children.Add(new TextBlock
        {
            Text = isArabic
                ? "خطوات اختيارية (سطر لكل خطوة، حتى 20 خطوة/250 حرفًا لكل خطوة). تُعرض في مسودة المهمة للمراجعة فقط."
                : "Optional steps (one per line, up to 20 steps/250 characters each). They are added to the task draft for review only.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        _stepsInput = new TextBox
        {
            MinHeight = 90,
            MaxHeight = 150,
            MaxLength = SavedRoutineService.MaximumTotalStepCharacters,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 3, 0, 8)
        };
        AutomationProperties.SetName(_stepsInput, isArabic ? "خطوات الروتين الاختيارية" : "Optional routine steps");
        AutomationProperties.SetHelpText(_stepsInput, isArabic
            ? "أدخل خطوة واحدة في كل سطر. تُحفظ محليًا مشفرة ولا تُنفذ تلقائيًا."
            : "Enter one step per line. Steps are stored encrypted locally and never run automatically.");
        goalPanel.Children.Add(_stepsInput);
        var saveButton = new Button
        {
            Content = isArabic ? "حفظ كقالب جديد" : "Save as new template",
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
        var updateButton = new Button
        {
            Content = isArabic ? "تحديث القالب المحدد" : "Update selected template",
            MinWidth = 170,
            MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetName(updateButton, isArabic ? "حفظ التعديلات على الروتين المحدد" : "Save edits to selected routine");
        AutomationProperties.SetHelpText(updateButton, isArabic
            ? "يعدّل الاسم والهدف للقالب المحدد محليًا دون تشغيله."
            : "Updates the selected template name and goal locally without running it.");
        updateButton.Click += UpdateSelectedButton_Click;
        goalPanel.Children.Add(updateButton);
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
            ? "حدد روتينًا لمراجعة بياناته، أو إدراجه في مسودة، أو تشغيله بعد تأكيد الخطة. الحذف يتطلب تأكيدًا."
            : "Select a routine to review its details, load it into a draft, or run it after plan confirmation. Deletion requires confirmation.");
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

        var runButton = new Button
        {
            Content = isArabic ? "تشغيل الروتين بموافقات" : "Run routine with approvals",
            MinWidth = 205, MinHeight = 34, Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(runButton, isArabic ? "تشغيل الروتين بعد التأكيد" : "Run routine after confirmation");
        AutomationProperties.SetHelpText(runButton, isArabic
            ? "يعرض الخطة كاملة في نافذة قابلة للتمرير. لن يبدأ التشغيل إلا بعد تأكيدك؛ وتظهر موافقة منفصلة قبل كل أداة أو أثر."
            : "Shows the complete plan in a scrollable confirmation window. Execution starts only after you confirm; each tool or side effect still requires separate approval.");
        runButton.Click += RunButton_Click;

        var deleteButton = new Button
        {
            Content = isArabic ? "حذف المحدد" : "Delete selected",
            MinWidth = 120,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(deleteButton, isArabic ? "حذف الروتين المحدد" : "Delete selected routine");
        AutomationProperties.SetHelpText(deleteButton, isArabic
            ? "يعرض تأكيدًا قبل حذف الروتين من التخزين المحلي المشفر."
            : "Asks for confirmation before removing the routine from encrypted local storage.");
        deleteButton.Click += DeleteButton_Click;

        var closeButton = new Button
        {
            Content = isArabic ? "إغلاق" : "Close",
            MinWidth = 90,
            MinHeight = 34,
            IsCancel = true
        };
        footer.Children.Add(useButton);
        footer.Children.Add(runButton);
        footer.Children.Add(deleteButton);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        RefreshList();
    }

    private List<string> ParseSteps()
    {
        return _stepsInput.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(step => step.Trim())
            .Where(step => step.Length > 0)
            .ToList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var record = _service.Create(_nameInput.Text, _goalInput.Text, ParseSteps());
            _nameInput.Clear();
            _goalInput.Clear();
            _stepsInput.Clear();
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

    private void UpdateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedRoutineId))
        {
            _status.Text = _isArabic ? "اختر قالبًا من القائمة أولًا." : "Select a template from the list first.";
            return;
        }

        try
        {
            var updated = _service.Update(_selectedRoutineId, _nameInput.Text, _goalInput.Text, ParseSteps());
            if (updated is null)
            {
                _status.Text = _isArabic ? "لم يعد القالب موجودًا؛ حدّث القائمة." : "The template no longer exists; refresh the list.";
                RefreshList();
                return;
            }

            RefreshList(updated.Id);
            _status.Text = _isArabic
                ? "تم تحديث الروتين محليًا بتشفير DPAPI. لم يتم تشغيله."
                : "Routine updated locally with DPAPI. It was not executed.";
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر تحديث الروتين: " : "Could not update routine: ") + ex.Message;
        }
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _status.Text = _isArabic ? "اختر روتينًا أولًا." : "Select a routine first.";
            return;
        }

        _onUse(SavedRoutineService.BuildReviewPrompt(selected.Record, _isArabic));
        DialogResult = true;
        Close();
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _status.Text = _isArabic ? "اختر روتينًا أولًا." : "Select a routine first.";
            return;
        }
        var prompt = SavedRoutineService.BuildExecutionPrompt(selected.Record, _isArabic);
        if (!ConfirmRun(prompt))
        {
            _status.Text = _isArabic ? "لم يتم تشغيل الروتين؛ لم يبدأ أي إجراء." : "The routine was not started; no action was performed.";
            return;
        }

        RunPrompt = prompt;
        DialogResult = true;
        Close();
    }

    private bool ConfirmRun(string prompt)
    {
        var confirmation = new Window
        {
            Title = _isArabic ? "مراجعة خطة الروتين والتأكيد" : "Review routine plan and confirm",
            Owner = this,
            Width = 760,
            Height = 640,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Background = Background,
            Foreground = Foreground
        };
        AutomationProperties.SetName(confirmation, confirmation.Title);
        AutomationProperties.SetHelpText(confirmation, _isArabic
            ? "اقرأ الخطة كاملة قبل بدء مهمة واحدة. زر الإلغاء هو الخيار الافتراضي."
            : "Review the complete plan before starting one task. Cancel is the default.");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var notice = new TextBlock
        {
            Text = _isArabic
                ? "سيُرسل نص الروتين إلى مزود الذكاء الاصطناعي المُعدّ لبدء مهمة واحدة. الروتين لا يمنح أي صلاحيات؛ ستظهر موافقة محلية منفصلة قبل كل أداة أو إجراء. لا يمكن التراجع تلقائيًا عن الآثار التي تمت."
                : "The routine text is sent to the configured AI provider for one task. It grants no permissions; a separate local approval appears before each tool or action. Completed effects are not automatically undone.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(notice, 0);
        root.Children.Add(notice);

        var plan = new TextBox
        {
            Text = prompt,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 280,
            Padding = new Thickness(10),
            IsTabStop = true
        };
        AutomationProperties.SetName(plan, _isArabic ? "الخطة الكاملة للروتين" : "Complete routine execution plan");
        AutomationProperties.SetHelpText(plan, _isArabic
            ? "الخطة كاملة للقراءة والتمرير والنسخ؛ هذه المعاينة لا تشغّلها."
            : "The complete read-only plan; scroll or copy it. This preview does not run it.");
        Grid.SetRow(plan, 1);
        root.Children.Add(plan);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = _isArabic ? "إلغاء" : "Cancel",
            MinWidth = 110,
            MinHeight = 36,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(cancelButton, _isArabic ? "إلغاء تشغيل الروتين" : "Cancel routine execution");
        var confirmButton = new Button
        {
            Content = _isArabic ? "تأكيد وبدء مهمة واحدة" : "Confirm and start one task",
            MinWidth = 205,
            MinHeight = 36,
            IsDefault = false
        };
        AutomationProperties.SetName(confirmButton, _isArabic ? "تأكيد خطة الروتين وبدء التنفيذ" : "Confirm routine plan and start execution");
        confirmButton.Click += (_, _) => confirmation.DialogResult = true;
        actions.Children.Add(cancelButton);
        actions.Children.Add(confirmButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        confirmation.Content = root;
        confirmation.Loaded += (_, _) => cancelButton.Focus();
        return confirmation.ShowDialog() == true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _status.Text = _isArabic ? "اختر روتينًا أولًا." : "Select a routine first.";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            _isArabic
                ? $"هل تريد حذف الروتين «{selected.Record.Name}» من التخزين المحلي؟"
                : $"Delete the routine “{selected.Record.Name}” from local storage?",
            _isArabic ? "تأكيد حذف الروتين" : "Confirm routine deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            _status.Text = _isArabic ? "تم إلغاء الحذف؛ الروتين ما زال محفوظًا." : "Deletion cancelled; the routine is still saved.";
            return;
        }

        if (_service.Delete(selected.Record.Id))
        {
            _selectedRoutineId = null;
            _nameInput.Clear();
            _goalInput.Clear();
            _stepsInput.Clear();
            _status.Text = _isArabic ? "تم حذف الروتين المحدد." : "Selected routine deleted.";
        }
        else
        {
            _status.Text = _isArabic ? "لم يعد الروتين موجودًا." : "The routine no longer exists.";
        }
        RefreshList();
    }

    private void UpdateSelectedPreview()
    {
        if (_list.SelectedItem is not RoutineListEntry selected)
        {
            _selectedRoutineId = null;
            return;
        }

        _selectedRoutineId = selected.Record.Id;
        _nameInput.Text = selected.Record.Name;
        _goalInput.Text = selected.Record.Goal;
        _stepsInput.Text = string.Join(Environment.NewLine, selected.Record.Steps ?? new System.Collections.Generic.List<string>());
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
