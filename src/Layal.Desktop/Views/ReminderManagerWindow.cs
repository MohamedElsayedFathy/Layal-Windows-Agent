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
    private readonly bool _isArabic;
    private readonly TextBox _titleInput;
    private readonly DatePicker _datePicker;
    private readonly TextBox _timeInput;
    private readonly ComboBox _recurrenceSelector;
    private readonly ListBox _remindersList;
    private readonly TextBlock _status;
    private readonly ObservableCollection<ReminderListEntry> _entries = new();

    public ReminderManagerWindow(ReminderService service, bool isArabic)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
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
        _datePicker = new DatePicker { SelectedDate = DateTime.Today, MinWidth = 120 };
        AutomationProperties.SetName(_datePicker, isArabic ? "تاريخ التذكير" : "Reminder date");
        dateStack.Children.Add(_datePicker);
        Grid.SetColumn(dateStack, 0);
        schedule.Children.Add(dateStack);

        var timeStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        timeStack.Children.Add(new TextBlock { Text = isArabic ? "الوقت (24 ساعة)" : "Time (24-hour)" });
        _timeInput = new TextBox { Text = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture), MinHeight = 28 };
        AutomationProperties.SetName(_timeInput, isArabic ? "وقت التذكير بصيغة ساعة ودقيقة" : "Reminder time in hours and minutes");
        AutomationProperties.SetHelpText(_timeInput, "HH:mm");
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
        AutomationProperties.SetHelpText(_remindersList, isArabic ? "اختر تذكيرًا ثم اضغط حذف المحدد." : "Select a reminder, then choose Delete selected.");
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
            ? "التذكيرات محفوظة محليًا. الإشعار يعتمد على تشغيل ليال وWindows."
            : "Reminders are stored locally. Notifications require Layal and Windows to be running.");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var privacy = new TextBlock
        {
            Text = isArabic
                ? "لا تُرسل التذكيرات إلى مزود AI. يجب أن تكون ليال قيد التشغيل لعرض الإشعار."
                : "Reminders are not sent to an AI provider. Layal must be running to display notifications.",
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

            var dueLocal = DateTime.SpecifyKind(selectedDate.Date + time.ToTimeSpan(), DateTimeKind.Local);
            var dueAt = new DateTimeOffset(dueLocal);
            var recurrence = (_recurrenceSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            _service.Create(title, dueAt, recurrence);
            _titleInput.Clear();
            _status.Text = _isArabic ? "تم حفظ التذكير محليًا بتشفير DPAPI." : "Reminder saved locally with DPAPI encryption.";
            RefreshList();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حفظ التذكير: " : "Could not save reminder: ") + ex.Message;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remindersList.SelectedItem is not ReminderListEntry selected)
        {
            _status.Text = _isArabic ? "اختر تذكيرًا أولًا." : "Select a reminder first.";
            return;
        }

        try
        {
            var removed = _service.Delete(selected.Record.Id);
            _status.Text = removed
                ? (_isArabic ? "تم حذف التذكير المحدد." : "Selected reminder deleted.")
                : (_isArabic ? "لم يعد التذكير موجودًا." : "The reminder no longer exists.");
            RefreshList();
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حذف التذكير: " : "Could not delete reminder: ") + ex.Message;
        }
    }

    private void RefreshList()
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
            _entries.Add(new ReminderListEntry(reminder.Clone(), $"{reminder.Title} — {due} — {recurrence}{active}"));
        }

        if (_entries.Count == 0)
            _status.Text = _isArabic
                ? "لا توجد تذكيرات محفوظة. أنشئ تذكيرًا جديدًا بالأعلى."
                : "No saved reminders. Create one above.";
    }

    private sealed record ReminderListEntry(ReminderRecord Record, string Description);
}
