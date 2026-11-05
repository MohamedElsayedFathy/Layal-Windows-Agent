using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>Manages persistent, encrypted local reminders without sending their contents to an AI provider.</summary>
public sealed class ReminderManagerWindow : Window
{
    private readonly ReminderService _service;
    private readonly ReminderTaskSchedulerService? _scheduler;
    private readonly TaskHistoryService? _taskHistory;
    private readonly bool _isArabic;
    private readonly TextBox _titleInput;
    private readonly DatePicker _datePicker;
    private readonly TextBox _timeInput;
    private readonly ComboBox _recurrenceSelector;
    private readonly ComboBox _relatedTaskSelector;
    private readonly ListBox _remindersList;
    private readonly TextBlock _status;
    private readonly ObservableCollection<ReminderListEntry> _entries = new();
    private string? _selectedReminderId;

    public ReminderManagerWindow(
        ReminderService service,
        bool isArabic,
        ReminderTaskSchedulerService? scheduler = null,
        TaskHistoryService? taskHistory = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _scheduler = scheduler;
        _taskHistory = taskHistory;
        _isArabic = isArabic;
        Title = isArabic ? "التذكيرات المحلية" : "Local reminders";
        Width = 760;
        Height = 650;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "التذكيرات محفوظة محليًا بتشفير DPAPI. لا تُرسل إلى أي مزود ذكاء اصطناعي."
            : "Reminders are stored locally with DPAPI encryption. They are not sent to any AI provider.");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = isArabic ? "التذكيرات — محلية ومشفرة" : "Reminders — local and encrypted",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _titleInput = new TextBox { MinHeight = 34, MaxLength = ReminderService.MaximumTitleCharacters, Margin = new Thickness(0, 3, 0, 8) };
        _titleInput.ToolTip = isArabic ? "اكتب نص التذكير (حتى 200 حرف)." : "Reminder text (up to 200 characters).";
        AutomationProperties.SetName(_titleInput, isArabic ? "نص التذكير" : "Reminder text");
        form.Children.Add(new TextBlock { Text = isArabic ? "ما الذي تريد تذكّره؟" : "What should you be reminded about?" });
        form.Children.Add(_titleInput);

        var schedule = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var dateStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        dateStack.Children.Add(new TextBlock { Text = isArabic ? "التاريخ" : "Date" });
        _datePicker = new DatePicker { SelectedDate = DateTime.Now.AddHours(1).Date, MinWidth = 120 };
        AutomationProperties.SetName(_datePicker, isArabic ? "تاريخ التذكير" : "Reminder date");
        dateStack.Children.Add(_datePicker);
        Grid.SetColumn(dateStack, 0);
        schedule.Children.Add(dateStack);

        var timeStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        timeStack.Children.Add(new TextBlock { Text = isArabic ? "الوقت (24 ساعة)" : "Time (24-hour)" });
        _timeInput = new TextBox { Text = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture), MinHeight = 28 };
        AutomationProperties.SetName(_timeInput, isArabic ? "وقت التذكير بصيغة ساعة ودقيقة" : "Reminder time in hours and minutes");
        AutomationProperties.SetHelpText(_timeInput, isArabic
            ? "صيغة HH:mm بنظام 24 ساعة. إذا تكرر الوقت عند انتهاء التوقيت الصيفي فسيُختار الظهور الأول؛ الوقت غير الموجود عند بدء التوقيت الصيفي مرفوض."
            : "24-hour HH:mm. During a daylight-saving fall-back overlap, the first occurrence is selected; nonexistent spring-forward times are rejected.");
        timeStack.Children.Add(_timeInput);
        Grid.SetColumn(timeStack, 1);
        schedule.Children.Add(timeStack);

        var recurrenceStack = new StackPanel();
        recurrenceStack.Children.Add(new TextBlock { Text = isArabic ? "التكرار" : "Repeat" });
        _recurrenceSelector = new ComboBox { MinHeight = 28, SelectedIndex = 0 };
        _recurrenceSelector.Items.Add(new ComboBoxItem { Content = isArabic ? "مرة واحدة" : "Once", Tag = "None" });
        _recurrenceSelector.Items.Add(new ComboBoxItem { Content = isArabic ? "يوميًا" : "Daily", Tag = "Daily" });
        _recurrenceSelector.Items.Add(new ComboBoxItem { Content = isArabic ? "أسبوعيًا" : "Weekly", Tag = "Weekly" });
        AutomationProperties.SetName(_recurrenceSelector, isArabic ? "تكرار التذكير" : "Reminder recurrence");
        recurrenceStack.Children.Add(_recurrenceSelector);
        Grid.SetColumn(recurrenceStack, 2);
        schedule.Children.Add(recurrenceStack);

        form.Children.Add(schedule);
        form.Children.Add(new TextBlock { Text = isArabic ? "ربط التذكير بمهمة (اختياري)" : "Link reminder to a task (optional)", Margin = new Thickness(0, 0, 0, 3) });
        _relatedTaskSelector = new ComboBox { MinHeight = 30, MinWidth = 220, Margin = new Thickness(0, 0, 0, 8), DisplayMemberPath = nameof(TaskLinkEntry.Description) };
        AutomationProperties.SetName(_relatedTaskSelector, isArabic ? "المهمة المرتبطة بالتذكير" : "Task linked to this reminder");
        AutomationProperties.SetHelpText(_relatedTaskSelector, isArabic
            ? "يعرض كل اختيار حالة المهمة ووقت آخر تحديث وبادئة قصيرة من معرّفها لتمييز المهام المتشابهة."
            : "Each choice shows the task status, last-updated time, and a short stable ID prefix to distinguish similar tasks.");
        _relatedTaskSelector.Items.Add(new TaskLinkEntry(null, isArabic ? "بدون مهمة مرتبطة" : "No linked task"));
        foreach (var task in _taskHistory?.GetRecent(100) ?? Array.Empty<AgentTaskRecord>())
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                continue;

            var goal = string.Join(" ", (task.Goal ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (goal.Length > 90) goal = goal[..90] + "…";
            var updatedAt = task.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var shortId = task.Id.Length > 8 ? task.Id[..8] : task.Id;
            _relatedTaskSelector.Items.Add(new TaskLinkEntry(task.Id, $"{goal} — {task.Status} — {updatedAt} — {shortId}"));
        }
        _relatedTaskSelector.SelectedIndex = 0;
        form.Children.Add(_relatedTaskSelector);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var addButton = new Button
        {
            Content = isArabic ? "حفظ التذكير" : "Save reminder",
            MinWidth = 130,
            MinHeight = 34,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(addButton, isArabic ? "حفظ تذكير محلي" : "Save local reminder");
        addButton.Click += AddButton_Click;
        var deleteButton = new Button
        {
            Content = isArabic ? "حذف المحدد" : "Delete selected",
            MinWidth = 125,
            MinHeight = 34
        };
        AutomationProperties.SetName(deleteButton, isArabic ? "حذف التذكير المحدد" : "Delete selected reminder");
        deleteButton.Click += DeleteButton_Click;
        actions.Children.Add(addButton);
        var updateButton = new Button
        {
            Content = isArabic ? "تحديث المحدد" : "Update selected",
            MinWidth = 130,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(updateButton, isArabic ? "تحديث التذكير المحدد" : "Update selected reminder");
        AutomationProperties.SetHelpText(updateButton, isArabic
            ? "يعدّل نص وموعد وتكرار التذكير المحدد ويعيد تفعيله دون إنشاء تذكير آخر."
            : "Edits the selected reminder text, time, and recurrence and reactivates it without creating a duplicate.");
        updateButton.Click += UpdateButton_Click;
        actions.Children.Add(updateButton);
        actions.Children.Add(deleteButton);
        form.Children.Add(actions);
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _remindersList = new ListBox
        {
            ItemsSource = _entries,
            DisplayMemberPath = nameof(ReminderListEntry.Description),
            Padding = new Thickness(6),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray
        };
        AutomationProperties.SetName(_remindersList, isArabic ? "قائمة التذكيرات المحفوظة" : "Saved reminders");
        AutomationProperties.SetHelpText(_remindersList, isArabic ? "اختر تذكيرًا لتعديله أو حذفه." : "Select a reminder to edit or delete it.");
        _remindersList.SelectionChanged += (_, _) => UpdateSelectedPreview();
        Grid.SetRow(_remindersList, 2);
        root.Children.Add(_remindersList);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(_status, isArabic ? "حالة التذكيرات" : "Reminder status");
        AutomationProperties.SetHelpText(_status, isArabic
            ? "التذكيرات مشفرة محليًا. يجدول Windows تشغيل ليال في جلسة المستخدم المسجّل دخوله؛ عند تفويت موعد أثناء تسجيل الخروج يُعالج عند تسجيل الدخول أو فتح ليال."
            : "Reminders are encrypted locally. Windows schedules Layal in the signed-in user's session; reminders missed while signed out are handled at next sign-in or when Layal is opened.");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var privacy = new TextBlock
        {
            Text = isArabic
                ? "لا تُرسل التذكيرات إلى مزود AI. يلزم تسجيل الدخول إلى Windows كي يبدأ Task Scheduler تشغيل ليال؛ لا يتم إيقاظ الجهاز."
                : "Reminders are not sent to an AI provider. Windows must have the user signed in for Task Scheduler to launch Layal; the device is not woken.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        footer.Children.Add(privacy);
        var closeButton = new Button { Content = isArabic ? "إغلاق" : "Close", MinWidth = 95, MinHeight = 32, IsCancel = true };
        AutomationProperties.SetName(closeButton, isArabic ? "إغلاق مدير التذكيرات" : "Close reminder manager");
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        RefreshList();
        AppendSchedulerStatus();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = _titleInput.Text;
            if (_datePicker.SelectedDate is not DateTime selectedDate)
                throw new ArgumentException(_isArabic ? "اختر تاريخًا صالحًا." : "Choose a valid date.");
            if (!TimeOnly.TryParseExact(_timeInput.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new ArgumentException(_isArabic ? "اكتب الوقت بصيغة 24 ساعة مثل 09:30." : "Enter a 24-hour time such as 09:30.");

            var dueLocal = selectedDate.Date + time.ToTimeSpan();
            var dueAt = ReminderService.CreateLocalDueAt(dueLocal, TimeZoneInfo.Local);
            var recurrence = (_recurrenceSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            _service.Create(title, dueAt, recurrence, GetSelectedTaskId());
            _titleInput.Clear();
            _status.Text = _isArabic ? "تم حفظ التذكير محليًا بتشفير DPAPI." : "Reminder saved locally with DPAPI encryption.";
            AppendSchedulerStatus();
            RefreshList();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حفظ التذكير: " : "Could not save reminder: ") + ex.Message;
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedReminderId))
        {
            _status.Text = _isArabic ? "اختر تذكيرًا من القائمة أولًا." : "Select a reminder from the list first.";
            return;
        }

        try
        {
            if (_datePicker.SelectedDate is not DateTime selectedDate)
                throw new ArgumentException(_isArabic ? "اختر تاريخًا صالحًا." : "Choose a valid date.");
            if (!TimeOnly.TryParseExact(_timeInput.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new ArgumentException(_isArabic ? "اكتب الوقت بصيغة 24 ساعة مثل 09:30." : "Enter a 24-hour time such as 09:30.");

            var dueLocal = selectedDate.Date + time.ToTimeSpan();
            var dueAt = ReminderService.CreateLocalDueAt(dueLocal, TimeZoneInfo.Local);
            var recurrence = (_recurrenceSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            var updated = _service.Update(_selectedReminderId, _titleInput.Text, dueAt, recurrence, GetSelectedTaskId());
            if (updated is null)
            {
                _status.Text = _isArabic ? "لم يعد التذكير موجودًا؛ حدّث القائمة." : "The reminder no longer exists; refresh the list.";
                RefreshList();
                return;
            }

            RefreshList(updated.Id);
            _status.Text = _isArabic
                ? "تم تحديث التذكير وإعادة تفعيله محليًا."
                : "Reminder updated and reactivated locally.";
            AppendSchedulerStatus();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر تحديث التذكير: " : "Could not update reminder: ") + ex.Message;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remindersList.SelectedItem is not ReminderListEntry selected)
        {
            _status.Text = _isArabic ? "اختر تذكيرًا أولًا." : "Select a reminder first.";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            _isArabic
                ? $"سيتم حذف التذكير «{selected.Record.Title}» نهائيًا من قائمة التذكيرات المحلية. هل تريد المتابعة؟"
                : $"The reminder “{selected.Record.Title}” will be permanently removed from the local reminder list. Continue?",
            _isArabic ? "تأكيد حذف التذكير" : "Confirm reminder deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = _service.Delete(selected.Record.Id);
            _selectedReminderId = null;
            _titleInput.Clear();
            _status.Text = removed
                ? (_isArabic ? "تم حذف التذكير المحدد." : "Selected reminder deleted.")
                : (_isArabic ? "لم يعد التذكير موجودًا." : "The reminder no longer exists.");
            RefreshList();
            AppendSchedulerStatus();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حذف التذكير: " : "Could not delete reminder: ") + ex.Message;
        }
    }

    private void AppendSchedulerStatus()
    {
        if (_scheduler is null)
        {
            _status.Text += _isArabic
                ? Environment.NewLine + "تحذير: جدولة Windows غير متاحة حاليًا. ستُفحص التذكيرات أثناء تشغيل ليال وعند فتحه مجددًا، لكن لن يصل إشعار في موعده إذا كان التطبيق مغلقًا."
                : Environment.NewLine + "Warning: Windows scheduling is unavailable. Reminders will be checked while Layal is open and on the next launch, but on-time notifications cannot be delivered while the app is closed.";
            return;
        }

        try
        {
            AppRuntime.SynchronizeLocalNotifications();
        }
        catch (Exception ex)
        {
            _status.Text += _isArabic
                ? Environment.NewLine + "تم حفظ التذكير، لكن تعذرت جدولة تشغيل ليال من Windows: " + ex.Message
                : Environment.NewLine + "The reminder was saved, but Windows could not schedule Layal to start: " + ex.Message;
        }
    }

    private void UpdateSelectedPreview()
    {
        if (_remindersList.SelectedItem is not ReminderListEntry selected)
        {
            _selectedReminderId = null;
            return;
        }

        var reminder = selected.Record;
        _selectedReminderId = reminder.Id;
        _titleInput.Text = reminder.Title;
        _datePicker.SelectedDate = reminder.DueAt.ToLocalTime().Date;
        _timeInput.Text = reminder.DueAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        _recurrenceSelector.SelectedItem = _recurrenceSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), reminder.Recurrence, StringComparison.Ordinal));
        var linkedTask = _relatedTaskSelector.Items.OfType<TaskLinkEntry>().FirstOrDefault(item => string.Equals(item.Id, reminder.RelatedTaskId, StringComparison.Ordinal));
        if (reminder.RelatedTaskId is not null && linkedTask is null)
        {
            linkedTask = new TaskLinkEntry(reminder.RelatedTaskId, (_isArabic ? "مهمة غير موجودة في السجل: " : "Task no longer in recent history: ") + reminder.RelatedTaskId);
            _relatedTaskSelector.Items.Add(linkedTask);
        }
        _relatedTaskSelector.SelectedItem = linkedTask ?? _relatedTaskSelector.Items.OfType<TaskLinkEntry>().FirstOrDefault();
        _status.Text = _isArabic
            ? "تم تحميل التذكير في الحقول. عدّلها ثم اضغط «تحديث المحدد»، أو احذف التذكير."
            : "Reminder loaded into the form. Edit the fields then choose Update selected, or delete the reminder.";
    }

    private void RefreshList(string? selectedId = null)
    {
        _entries.Clear();
        foreach (var reminder in _service.GetAll())
        {
            var recurrence = reminder.Recurrence switch
            {
                "Daily" => _isArabic ? "يوميًا" : "Daily",
                "Weekly" => _isArabic ? "أسبوعيًا" : "Weekly",
                _ => _isArabic ? "مرة واحدة" : "Once"
            };
            var active = reminder.IsActive ? string.Empty : (_isArabic ? " — متوقف" : " — inactive");
            var due = reminder.DueAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var linked = reminder.RelatedTaskId is null ? string.Empty : (_isArabic ? " — مرتبط بمهمة" : " — linked to task");
            _entries.Add(new ReminderListEntry(reminder.Clone(), $"{reminder.Title} — {due} — {recurrence}{active}{linked}"));
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
            _remindersList.SelectedItem = _entries.FirstOrDefault(entry =>
                string.Equals(entry.Record.Id, selectedId, StringComparison.Ordinal));

        if (_entries.Count == 0)
            _status.Text = _isArabic
                ? "لا توجد تذكيرات محفوظة. أنشئ تذكيرًا جديدًا بالأعلى."
                : "No saved reminders. Create one above.";
    }

    private string? GetSelectedTaskId() => (_relatedTaskSelector.SelectedItem as TaskLinkEntry)?.Id;
    private sealed record TaskLinkEntry(string? Id, string Description);
    private sealed record ReminderListEntry(ReminderRecord Record, string Description);
}
