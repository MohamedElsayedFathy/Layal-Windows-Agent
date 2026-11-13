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
    private void RefreshRecentTasks(string? selectTaskId = null)
    {
        if (!PrivateChatModePolicy.CanAccessTaskWorkspace(_incognitoMode))
        {
            RecentTasks.Clear();
            RecentTasksList.SelectedItem = null;
            TaskEvents.Clear();
            _rawTaskEvents.Clear();
            TaskResult.Clear();
            TaskCopyStatus.Text = string.Empty;
            return;
        }

        var previouslySelectedId = (RecentTasksList.SelectedItem as TaskHistoryListItem)?.Id;
        var idToSelect = string.IsNullOrWhiteSpace(selectTaskId) ? previouslySelectedId : selectTaskId;
        _refreshingRecentTasks = true;
        try
        {
            RecentTasks.Clear();
            // A reminder may target any retained task, not only the 30 most recent rows.
            // Pin that exact ID into the list when necessary; never fall back to a different task.
            foreach (var task in AppRuntime.TaskHistoryService.GetRecentWithSelectedTask(idToSelect, 30))
                RecentTasks.Add(new TaskHistoryListItem(task, IsArabic));
            if (!string.IsNullOrWhiteSpace(idToSelect))
                RecentTasksList.SelectedItem = RecentTasks.FirstOrDefault(t => t.Id == idToSelect);
        }
        finally { _refreshingRecentTasks = false; }

        // Programmatic selection is intentionally suppressed while rebuilding the list;
        // explicitly refresh the detail pane afterward so notification deep-links show
        // the selected task's result instead of leaving stale details from another task.
        if (RecentTasksList.SelectedItem is TaskHistoryListItem selectedTask)
            DisplayTaskHistoryRecord(selectedTask.Record);
    }

    private void RecentTasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked()) return;
        if (_refreshingRecentTasks || RecentTasksList.SelectedItem is not TaskHistoryListItem selectedTask) return;
        DisplayTaskHistoryRecord(selectedTask.Record);
    }

    private void DisplayTaskHistoryRecord(AgentTaskRecord task)
    {
        if (IsPrivateTaskWorkspaceAccessBlocked())
        {
            TaskEvents.Clear();
            _rawTaskEvents.Clear();
            TaskResult.Clear();
            return;
        }

        TaskEvents.Clear();
        _rawTaskEvents.Clear();
        foreach (var line in task.Events.TakeLast(300)) AddTaskEvent(line);
        SetAccessibleStatusText(TaskResult, UiLocalization.FormatTaskResult(task, IsArabic));

        TaskResult.Foreground = task.Status == "Completed" ? ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen) :
            task.Status is "CompletedWithWarnings" or "Stopped" or "Cancelled" ? ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod) :
            ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        AppRuntime.MascotWindow?.SetMood(task.Status switch
        {
            "Completed" => "success",
            "CompletedWithWarnings" or "Stopped" => "warning",
            "Cancelled" => "paused",
            _ => "error"
        });
    }

    private bool IsPrivateTaskWorkspaceAccessBlocked()
    {
        if (PrivateChatModePolicy.CanAccessTaskWorkspace(_incognitoMode))
            return false;

        SetChatActivityStatus(IsArabic
            ? "مساحة المهام وسجلها والروتينات والتذكيرات المرتبطة بها غير متاحة أثناء المحادثة الخاصة."
            : "The task workspace, history, saved routines, and task-linked reminders are unavailable during private chat.");
        return true;
    }

    private bool IsPrivateMemoryAccessBlocked()
    {
        if (PrivateChatModePolicy.CanAccessPersonalMemory(_incognitoMode))
            return false;

        SetChatActivityStatus(IsArabic
            ? "الوصول إلى الذاكرة الشخصية متوقف أثناء المحادثة الخاصة."
            : "Personal memory is unavailable during private chat.");
        return true;
    }

    private bool MemoryEditorHasUnsavedChanges =>
        !string.Equals(MemoryTitleInput.Text, _memoryEditorBaselineTitle, StringComparison.Ordinal) ||
        !string.Equals(MemoryContentInput.Text, _memoryEditorBaselineContent, StringComparison.Ordinal) ||
        !string.Equals(MemoryCategoryInput.Text, _memoryEditorBaselineCategory, StringComparison.Ordinal);

    private bool ConfirmDiscardMemoryDraft(bool clearDraft = false)
    {
        if (IsPrivateMemoryAccessBlocked()) return false;
        if (!MemoryEditorHasUnsavedChanges) return true;
        var answer = LayalDialogService.Show(this,
            IsArabic
                ? "توجد تغييرات غير محفوظة في محرر الذاكرة. هل تريد تجاهلها والمتابعة؟"
                : "The memory editor contains unsaved changes. Discard them and continue?",
            IsArabic ? "تغييرات غير محفوظة" : "Unsaved memory changes",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return false;

        if (clearDraft)
        {
            var saved = _selectedMemoryId is null
                ? null
                : AppRuntime.MemoryService.GetAll(includeArchived: true)
                    .FirstOrDefault(note => string.Equals(note.Id, _selectedMemoryId, StringComparison.Ordinal));
            if (saved is null) ResetMemoryEditor();
            else LoadMemoryEditor(saved);
        }
        return true;
    }

    private void SetMemoryEditorBaseline(MemoryNote? note)
    {
        _memoryEditorBaselineTitle = note?.Title ?? string.Empty;
        _memoryEditorBaselineContent = note?.Content ?? string.Empty;
        _memoryEditorBaselineCategory = note?.Category ?? "General";
    }

    private void LoadMemoryEditor(MemoryNote note)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        _selectedMemoryId = note.Id;
        _selectedMemoryIsArchived = note.IsArchived;
        _selectedMemoryUpdatedAt = note.UpdatedAt;
        MemoryCategoryInput.Text = note.Category;
        MemoryTitleInput.Text = note.Title;
        MemoryContentInput.Text = note.Content;
        ArchiveMemoryButton.Content = note.IsArchived
            ? (IsArabic ? "استعادة" : "Restore")
            : (IsArabic ? "أرشفة" : "Archive");
        SetMemoryEditorBaseline(note);
        UpdateMemoryEditorButtons();
        SetAccessibleStatusText(MemoryResult, UiLocalization.FormatDateTime(note.UpdatedAt.LocalDateTime, IsArabic));
    }

    private void ResetMemoryEditor()
    {
        _selectedMemoryId = null;
        _selectedMemoryIsArchived = false;
        _selectedMemoryUpdatedAt = null;
        _refreshingMemoryCollection = true;
        try { MemoryList.SelectedItem = null; }
        finally { _refreshingMemoryCollection = false; }
        MemoryCategoryInput.Text = "General";
        MemoryTitleInput.Clear();
        MemoryContentInput.Clear();
        ArchiveMemoryButton.Content = IsArabic ? "أرشفة" : "Archive";
        SetMemoryEditorBaseline(null);
        UpdateMemoryEditorButtons();
    }

    private void UpdateMemoryEditorButtons()
    {
        var hasSelection = PrivateChatModePolicy.CanAccessPersonalMemory(_incognitoMode) &&
            !string.IsNullOrWhiteSpace(_selectedMemoryId);
        ArchiveMemoryButton.IsEnabled = hasSelection;
        DeleteMemoryButton.IsEnabled = hasSelection;
        AutomationProperties.SetHelpText(ArchiveMemoryButton, IsArabic
            ? (hasSelection ? "أرشف الملاحظة المحددة دون حذفها، أو استعدها إلى القائمة النشطة." : "حدد ملاحظة أولًا.")
            : (hasSelection ? "Archive the selected note without deleting it, or restore it to active memory." : "Select a note first."));
        AutomationProperties.SetHelpText(DeleteMemoryButton, IsArabic
            ? (hasSelection ? "افتح معاينة الحذف النهائي للملاحظة المحددة." : "حدد ملاحظة أولًا.")
            : (hasSelection ? "Review the selected note before permanent deletion." : "Select a note first."));
    }

    private void RefreshMemoryCategoryFilter(IReadOnlyList<MemoryNote> allNotes)
    {
        var selectedCategory = (MemoryCategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _refreshingMemoryCategoryFilter = true;
        try
        {
            MemoryCategoryFilter.Items.Clear();
            var allItem = new ComboBoxItem
            {
                Content = IsArabic ? "كل التصنيفات" : "All categories",
                Tag = string.Empty
            };
            MemoryCategoryFilter.Items.Add(allItem);
            foreach (var category in allNotes
                .Select(note => MemoryService.NormalizeCategory(note.Category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            {
                MemoryCategoryFilter.Items.Add(new ComboBoxItem { Content = category, Tag = category });
            }

            MemoryCategoryFilter.SelectedItem = MemoryCategoryFilter.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString() ?? string.Empty, selectedCategory, StringComparison.OrdinalIgnoreCase))
                ?? allItem;
        }
        finally { _refreshingMemoryCategoryFilter = false; }
    }

    private void UpdateMemoryCountDisplay()
    {
        if (_incognitoMode)
        {
            MemoryCountText.Text = string.Empty;
            MemoryFilterNotice.Text = string.Empty;
            return;
        }

        try
        {
            var allNotes = AppRuntime.MemoryService.GetAll(includeArchived: true);
            var activeCount = allNotes.Count(note => !note.IsArchived);
            var archivedCount = allNotes.Count - activeCount;
            MemoryCountText.Text = IsArabic
                ? $"{MemoryNotes.Count} معروضة • نشطة {activeCount} • مؤرشفة {archivedCount}"
                : $"{MemoryNotes.Count} shown • active {activeCount} • archived {archivedCount}";
            AutomationProperties.SetName(MemoryCountText, IsArabic
                ? $"عدد الملاحظات المعروضة {MemoryNotes.Count}، النشطة {activeCount}، المؤرشفة {archivedCount}"
                : $"{MemoryNotes.Count} notes shown, {activeCount} active, {archivedCount} archived");
        }
        catch (Exception)
        {
            MemoryCountText.Text = IsArabic ? "تعذر قراءة عدد الملاحظات." : "Could not read note count.";
        }
    }

    private void RefreshMemoryNotes(string? query = null)
    {
        if (IsPrivateMemoryAccessBlocked())
        {
            _refreshingMemoryCollection = true;
            try
            {
                MemoryNotes.Clear();
                MemoryList.SelectedItem = null;
                MemoryCategoryFilter.Items.Clear();
                MemoryCountText.Text = string.Empty;
                MemoryFilterNotice.Text = string.Empty;
            }
            finally { _refreshingMemoryCollection = false; }
            ResetMemoryEditor();
            return;
        }

        var allNotes = AppRuntime.MemoryService.GetAll(includeArchived: true);
        RefreshMemoryCategoryFilter(allNotes);
        var includeArchived = MemoryShowArchivedCheckBox.IsChecked == true;
        var effectiveQuery = (query ?? MemorySearchInput.Text ?? string.Empty).Trim();
        var selectedCategory = (MemoryCategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        IEnumerable<MemoryNote> filtered = includeArchived
            ? allNotes
            : allNotes.Where(note => !note.IsArchived);

        if (effectiveQuery.Length > 0)
        {
            filtered = filtered.Where(note =>
                note.Title.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase) ||
                note.Content.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase) ||
                note.Category.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase));
        }
        if (selectedCategory.Length > 0)
            filtered = filtered.Where(note => string.Equals(note.Category, selectedCategory, StringComparison.OrdinalIgnoreCase));

        var visibleNotes = filtered.OrderByDescending(note => note.UpdatedAt).ToList();
        _refreshingMemoryCollection = true;
        try
        {
            MemoryNotes.Clear();
            foreach (var note in visibleNotes) MemoryNotes.Add(note);
            MemoryList.SelectedItem = _selectedMemoryId is null
                ? null
                : MemoryNotes.FirstOrDefault(note => string.Equals(note.Id, _selectedMemoryId, StringComparison.Ordinal));
        }
        finally { _refreshingMemoryCollection = false; }

        var activeCount = allNotes.Count(note => !note.IsArchived);
        var archivedCount = allNotes.Count - activeCount;
        MemoryCountText.Text = IsArabic
            ? $"{visibleNotes.Count} معروضة • نشطة {activeCount} • مؤرشفة {archivedCount}"
            : $"{visibleNotes.Count} shown • active {activeCount} • archived {archivedCount}";
        AutomationProperties.SetName(MemoryCountText, IsArabic
            ? $"عدد الملاحظات المعروضة {visibleNotes.Count}، النشطة {activeCount}، المؤرشفة {archivedCount}"
            : $"{visibleNotes.Count} notes shown, {activeCount} active, {archivedCount} archived");

        if (allNotes.Count == 0)
        {
            MemoryEmptyStateText.Text = IsArabic
                ? "لا توجد ملاحظات محفوظة. أنشئ ملاحظة جديدة للبدء."
                : "No saved notes yet. Create a note to get started.";
        }
        else if (visibleNotes.Count == 0 && activeCount == 0 && archivedCount > 0 && !includeArchived)
        {
            MemoryEmptyStateText.Text = IsArabic
                ? "كل الملاحظات محفوظة في الأرشيف. فعّل عرض الملاحظات المؤرشفة لاستعادتها."
                : "All notes are archived. Turn on Include archived notes to restore them.";
        }
        else if (visibleNotes.Count == 0)
        {
            MemoryEmptyStateText.Text = IsArabic
                ? "لا توجد ملاحظات تطابق البحث وعوامل التصفية. امسح عوامل التصفية لعرض كل الملاحظات."
                : "No notes match the search and filters. Clear filters to show all notes.";
        }

        var selectedHiddenByFilters = _selectedMemoryId is not null &&
            !visibleNotes.Any(note => string.Equals(note.Id, _selectedMemoryId, StringComparison.Ordinal));
        MemoryFilterNotice.Text = selectedHiddenByFilters
            ? (IsArabic
                ? "الملاحظة المفتوحة في المحرر مخفية بواسطة عوامل التصفية الحالية؛ لم تُحذف. امسح عوامل التصفية للعودة إليها في القائمة."
                : "The note open in the editor is hidden by the current filters; it has not been deleted. Clear filters to show it in the list.")
            : string.Empty;
    }

    private void MemorySearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        RefreshMemoryNotes(MemorySearchInput.Text);
    }

    private void MemorySearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MemorySearchButton_Click(MemorySearchButton, new RoutedEventArgs(Button.ClickEvent, MemorySearchButton));
        e.Handled = true;
    }

    private void MemoryShowArchivedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingMemoryCollection || IsPrivateMemoryAccessBlocked()) return;
        RefreshMemoryNotes();
    }

    private void MemoryCategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMemoryCategoryFilter || IsPrivateMemoryAccessBlocked()) return;
        RefreshMemoryNotes();
    }

    private void ClearMemoryFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        MemorySearchInput.Clear();
        MemoryShowArchivedCheckBox.IsChecked = false;
        MemoryCategoryFilter.SelectedIndex = 0;
        RefreshMemoryNotes(string.Empty);
        MemorySearchInput.Focus();
    }

    private void LoreWebButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        try
        {
            var notes = AppRuntime.MemoryService.GetAll(includeArchived: true);
            var window = new LoreWebWindow(notes, IsArabic, selectedNote =>
            {
                if (!ConfirmDiscardMemoryDraft()) return;
                MainTabs.SelectedItem = MemoryTab;
                MemorySearchInput.Clear();
                MemoryCategoryFilter.SelectedIndex = 0;
                if (selectedNote.IsArchived) MemoryShowArchivedCheckBox.IsChecked = true;
                RefreshMemoryNotes(string.Empty);
                var current = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(note =>
                    string.Equals(note.Id, selectedNote.Id, StringComparison.Ordinal));
                if (current is null) return;
                MemoryList.SelectedItem = MemoryNotes.FirstOrDefault(note =>
                    string.Equals(note.Id, current.Id, StringComparison.Ordinal));
                LoadMemoryEditor(current);
                SetAccessibleStatusText(MemoryResult,
                    IsArabic
                        ? "تم فتح الملاحظة من خريطة الذاكرة المحلية."
                        : "Note opened from the local memory web.");
                MemoryTitleInput.Focus();
            },
            note => AppRuntime.MemoryService.Delete(note.Id, note.UpdatedAt),
            note => AppRuntime.MemoryService.SetArchived(note.Id, !note.IsArchived),
            (sourceId, targetId, label) => AppRuntime.MemoryService.LinkNotes(sourceId, targetId, label),
            (sourceId, targetId) => AppRuntime.MemoryService.UnlinkNotes(sourceId, targetId))
            {
                Owner = this
            };
            window.ShowDialog();
            RefreshMemoryNotes();
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError(
                "تعذر فتح خريطة الذاكرة", "Could not open the memory web", ex, IsArabic));
        }
    }

    private void NewMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked() || !ConfirmDiscardMemoryDraft()) return;
        ResetMemoryEditor();
        SetAccessibleStatusText(MemoryResult, IsArabic
            ? "اكتب ملاحظة جديدة ثم راجع النص الكامل قبل الحفظ."
            : "Write a new note, then review the complete text before saving.");
        MemoryTitleInput.Focus();
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMemoryCollection || IsPrivateMemoryAccessBlocked()) return;
        if (MemoryList.SelectedItem is not MemoryNote note ||
            string.Equals(note.Id, _selectedMemoryId, StringComparison.Ordinal)) return;

        if (!ConfirmDiscardMemoryDraft())
        {
            _refreshingMemoryCollection = true;
            try
            {
                MemoryList.SelectedItem = _selectedMemoryId is null
                    ? null
                    : MemoryNotes.FirstOrDefault(item => string.Equals(item.Id, _selectedMemoryId, StringComparison.Ordinal));
            }
            finally { _refreshingMemoryCollection = false; }
            return;
        }

        var current = AppRuntime.MemoryService.GetAll(includeArchived: true)
            .FirstOrDefault(item => string.Equals(item.Id, note.Id, StringComparison.Ordinal));
        if (current is null)
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "لم تعد هذه الملاحظة موجودة." : "This note no longer exists.");
            RefreshMemoryNotes();
            return;
        }
        LoadMemoryEditor(current);
    }

    private void SaveMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        try
        {
            var title = MemoryService.NormalizeTitle(MemoryTitleInput.Text);
            var content = MemoryService.NormalizeContent(MemoryContentInput.Text);
            var category = MemoryService.NormalizeCategory(MemoryCategoryInput.Text);
            var existing = _selectedMemoryId is null
                ? null
                : AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(note =>
                    string.Equals(note.Id, _selectedMemoryId, StringComparison.Ordinal));
            if (_selectedMemoryId is not null && existing is null)
                throw new InvalidOperationException(IsArabic
                    ? "لم تعد الملاحظة الأصلية موجودة؛ لم يتم إنشاء ملاحظة بديلة. حدّث القائمة وابدأ ملاحظة جديدة."
                    : "The original note no longer exists; no replacement was created. Refresh the list and start a new note.");
            if (existing is not null && _selectedMemoryUpdatedAt != existing.UpdatedAt)
                throw new InvalidOperationException(IsArabic
                    ? "تغيرت الملاحظة بعد فتحها. أعد تحميلها قبل حفظ التعديلات حتى لا تستبدل نسخة أحدث."
                    : "The note changed after it was opened. Reload it before saving so a newer version is not overwritten.");

            var draft = new MemoryNote
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                Title = title,
                Content = content,
                Category = category,
                IsArchived = existing?.IsArchived ?? false,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.Now,
                UpdatedAt = existing?.UpdatedAt ?? DateTimeOffset.Now
            };
            var action = existing is null ? MemoryNoteReviewAction.SaveNew : MemoryNoteReviewAction.SaveChanges;
            var review = new MemoryNoteReviewWindow(draft, IsArabic, action) { Owner = this };
            if (review.ShowDialog() != true) return;

            var saved = AppRuntime.MemoryService.Save(title, content, _selectedMemoryId, category, _selectedMemoryUpdatedAt);
            _selectedMemoryId = saved.Id;
            _selectedMemoryIsArchived = saved.IsArchived;
            _selectedMemoryUpdatedAt = saved.UpdatedAt;
            MemorySearchInput.Clear();
            MemoryCategoryFilter.SelectedIndex = 0;
            if (saved.IsArchived) MemoryShowArchivedCheckBox.IsChecked = true;
            RefreshMemoryNotes(string.Empty);
            MemoryList.SelectedItem = MemoryNotes.FirstOrDefault(note => string.Equals(note.Id, saved.Id, StringComparison.Ordinal));
            LoadMemoryEditor(saved);
            SetAccessibleStatusText(MemoryResult, IsArabic
                ? "تم حفظ الملاحظة محليًا بصورة مشفرة. لم يتم إرسالها إلى AI."
                : "Note saved locally with encryption. It was not sent to AI.");
            MemoryResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError("تعذر حفظ الملاحظة", "Could not save the note", ex, IsArabic));
            MemoryResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private void ArchiveMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked() || !ConfirmDiscardMemoryDraft()) return;
        if (string.IsNullOrWhiteSpace(_selectedMemoryId))
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "حدد ملاحظة لأرشفتها أو استعادتها." : "Select a note to archive or restore.");
            return;
        }

        var note = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(item =>
            string.Equals(item.Id, _selectedMemoryId, StringComparison.Ordinal));
        if (note is null)
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "الملاحظة غير موجودة." : "The note no longer exists.");
            ResetMemoryEditor();
            RefreshMemoryNotes();
            return;
        }
        if (_selectedMemoryUpdatedAt != note.UpdatedAt)
        {
            LoadMemoryEditor(note);
            SetAccessibleStatusText(MemoryResult, IsArabic
                ? "تغيرت الملاحظة منذ فتحها؛ تم تحديث المحرر. راجع الحالة الجديدة ثم أعد المحاولة."
                : "The note changed since it was opened. The editor has been refreshed; review it and try again.");
            return;
        }

        var shouldArchive = !note.IsArchived;
        var answer = LayalDialogService.Show(this,
            shouldArchive
                ? (IsArabic
                    ? $"سيتم نقل «{note.Title}» إلى الأرشيف. لن تُحذف ويمكن استعادتها لاحقًا. هل تريد المتابعة؟"
                    : $"“{note.Title}” will be moved to the archive. It will not be deleted and can be restored later. Continue?")
                : (IsArabic
                    ? $"ستتم استعادة «{note.Title}» إلى قائمة الملاحظات النشطة. هل تريد المتابعة؟"
                    : $"“{note.Title}” will be restored to active memory. Continue?"),
            shouldArchive
                ? (IsArabic ? "تأكيد الأرشفة" : "Confirm archive")
                : (IsArabic ? "تأكيد الاستعادة" : "Confirm restore"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        if (!AppRuntime.MemoryService.SetArchived(note.Id, shouldArchive))
        {
            SetAccessibleStatusText(MemoryResult,
                IsArabic ? "تعذر تحديث حالة الأرشفة." : "Could not update the archive state.");
            return;
        }

        var updated = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(item =>
            string.Equals(item.Id, note.Id, StringComparison.Ordinal));
        if (updated is null) return;
        MemorySearchInput.Clear();
        MemoryCategoryFilter.SelectedIndex = 0;
        MemoryShowArchivedCheckBox.IsChecked = true;
        RefreshMemoryNotes(string.Empty);
        MemoryList.SelectedItem = MemoryNotes.FirstOrDefault(item => string.Equals(item.Id, updated.Id, StringComparison.Ordinal));
        LoadMemoryEditor(updated);
        SetAccessibleStatusText(MemoryResult, shouldArchive
            ? (IsArabic ? "تمت أرشفة الملاحظة دون حذفها. يمكنك استعادتها من زر استعادة." : "Note archived without deletion. Use Restore to return it.")
            : (IsArabic ? "تمت استعادة الملاحظة إلى قائمة الذاكرة النشطة." : "Note restored to active memory."));
        MemoryResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
    }

    private void DeleteMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked() || !ConfirmDiscardMemoryDraft()) return;
        if (string.IsNullOrWhiteSpace(_selectedMemoryId))
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "حدد ملاحظة لحذفها." : "Select a note to delete.");
            return;
        }

        var id = _selectedMemoryId;
        var note = AppRuntime.MemoryService.GetAll(includeArchived: true).FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));
        if (note is null)
        {
            SetAccessibleStatusText(MemoryResult, IsArabic ? "الملاحظة لم تعد موجودة." : "The note no longer exists.");
            ResetMemoryEditor();
            RefreshMemoryNotes();
            return;
        }
        if (_selectedMemoryUpdatedAt != note.UpdatedAt)
        {
            LoadMemoryEditor(note);
            SetAccessibleStatusText(MemoryResult, IsArabic
                ? "تغيرت الملاحظة منذ فتحها؛ لم يتم حذفها. راجع النسخة الحالية ثم أعد المحاولة."
                : "The note changed since it was opened; it was not deleted. Review the current version and try again.");
            return;
        }

        var review = new MemoryNoteReviewWindow(note, IsArabic, MemoryNoteReviewAction.Delete) { Owner = this };
        if (review.ShowDialog() != true) return;
        if (!AppRuntime.MemoryService.Delete(id, note.UpdatedAt))
        {
            SetAccessibleStatusText(MemoryResult, IsArabic
                ? "تغيرت الملاحظة أو حُذفت قبل تأكيد الإجراء؛ لم يُحذف أي محتوى أحدث."
                : "The note changed or was deleted before confirmation; no newer content was deleted.");
            RefreshMemoryNotes();
            return;
        }

        ResetMemoryEditor();
        RefreshMemoryNotes();
        SetAccessibleStatusText(MemoryResult, IsArabic
            ? "تم حذف الملاحظة من الذاكرة المحلية. لا يشمل ذلك النسخ المصدّرة أو التي سبق إرسالها."
            : "The note was deleted from local memory. Previously exported or shared copies are not affected.");
        MemoryResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
    }

    private async void ImportMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        var dialog = new OpenFileDialog
        {
            Title = IsArabic ? "استيراد نسخة ذاكرة JSON" : "Import memory JSON backup",
            Filter = IsArabic ? "ملفات JSON (*.json)|*.json" : "JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            byte[] bytes;
            await using (var input = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length is < 1 or > MaxMemoryBackupBytes)
                    throw new InvalidOperationException(IsArabic
                        ? "حجم النسخة الاحتياطية يجب أن يكون بين 1 بايت و2 MiB."
                        : "The memory backup must be between 1 byte and 2 MiB.");
                bytes = new byte[checked((int)input.Length)];
                await input.ReadExactlyAsync(bytes.AsMemory());
                if (input.Length != bytes.Length)
                    throw new IOException(IsArabic
                        ? "تغير حجم ملف النسخة الاحتياطية أثناء القراءة."
                        : "The backup file changed size while being read.");
            }
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var json = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            var imported = JsonSerializer.Deserialize<List<MemoryNote>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 32
            }) ?? throw new InvalidOperationException(IsArabic
                ? "ملف النسخة الاحتياطية لا يحتوي قائمة ملاحظات JSON صحيحة."
                : "The backup does not contain a valid JSON note list.");

            AppRuntime.MemoryService.ValidateImportMany(imported);
            var existingIds = AppRuntime.MemoryService.GetAll(includeArchived: true)
                .Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            var additions = imported.Where(note => !existingIds.Contains(note.Id)).ToList();
            if (additions.Count == 0)
            {
                SetAccessibleStatusText(MemoryResult, IsArabic
                    ? "لا توجد ملاحظات جديدة للاستيراد؛ الملاحظات ذات المعرّفات الموجودة لم تُستبدل."
                    : "There are no new notes to import. Existing note IDs were not replaced.");
                return;
            }

            var previewTitles = string.Join(Environment.NewLine, additions.Take(10)
                .Select(note => "• " + note.Title + " — " + MemoryService.NormalizeCategory(note.Category)));
            if (additions.Count > 10)
                previewTitles += Environment.NewLine + (IsArabic ? $"… و{additions.Count - 10} ملاحظات أخرى" : $"… and {additions.Count - 10} more");
            var duplicateCount = imported.Count - additions.Count;
            var message = IsArabic
                ? $"سيتم دمج {additions.Count} ملاحظة جديدة من الملف «{Path.GetFileName(dialog.FileName)}». سيتم تخطي {duplicateCount} ملاحظة بمعرّفات موجودة؛ لن تُستبدل أي ملاحظة حالية. تأكد من أنك تثق بمصدر الملف. محتوى JSON المستورد سيُحفظ مشفرًا محليًا.\n\n{previewTitles}\n\nهل تريد المتابعة؟"
                : $"Merge {additions.Count} new notes from “{Path.GetFileName(dialog.FileName)}”. {duplicateCount} notes with existing IDs will be skipped; no existing note will be replaced. Only import a file you trust. Imported JSON content will be stored encrypted locally.\n\n{previewTitles}\n\nContinue?";
            var confirm = LayalDialogService.Show(this, message,
                IsArabic ? "مراجعة دمج الذاكرة" : "Review memory merge",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            if (!ConfirmDiscardMemoryDraft(clearDraft: true)) return;

            var added = AppRuntime.MemoryService.ImportMany(imported);
            MemorySearchInput.Clear();
            MemoryCategoryFilter.SelectedIndex = 0;
            MemoryShowArchivedCheckBox.IsChecked = true;
            RefreshMemoryNotes(string.Empty);
            SetAccessibleStatusText(MemoryResult, IsArabic
                ? $"تم استيراد {added} ملاحظة جديدة. لم تُستبدل الملاحظات الموجودة."
                : $"Imported {added} new notes. Existing notes were not replaced.");
            MemoryResult.Foreground = ThemeBrush("Layal.Success", System.Windows.Media.Brushes.ForestGreen);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError(
                "تعذر استيراد نسخة الذاكرة", "Could not import memory backup", ex, IsArabic));
            MemoryResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    private async void ExportMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivateMemoryAccessBlocked()) return;
        var dialog = new SaveFileDialog
        {
            Title = IsArabic ? "تصدير الذاكرة إلى JSON غير مشفر" : "Export memory to unencrypted JSON",
            Filter = IsArabic ? "ملفات JSON (*.json)|*.json" : "JSON files (*.json)|*.json",
            FileName = "layal-memory-export.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var confirm = LayalDialogService.Show(this,
            IsArabic
                ? "سيُصدّر JSON جميع الملاحظات النشطة والمؤرشفة دون تشفير. قد يحتوي على معلومات شخصية؛ احفظه محليًا في مكان موثوق ولا تشاركه إلا بعد مراجعته. هل تريد المتابعة؟"
                : "This unencrypted JSON export includes all active and archived notes and may contain personal information. Store it in a trusted local location and review it before sharing. Continue?",
            IsArabic ? "تنبيه خصوصية" : "Privacy warning",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, AppRuntime.MemoryService.ExportJson(), new UTF8Encoding(false));
            SetAccessibleStatusText(MemoryResult, IsArabic ? "تم تصدير كل الملاحظات. ملف JSON غير مشفر." : "All notes were exported. The JSON file is unencrypted.");
            MemoryResult.Foreground = ThemeBrush("Layal.Warning", System.Windows.Media.Brushes.DarkGoldenrod);
        }
        catch (Exception ex)
        {
            SetAccessibleStatusText(MemoryResult, UiLocalization.FormatTechnicalError("تعذر تصدير الذاكرة", "Could not export memory", ex, IsArabic));
            MemoryResult.Foreground = ThemeBrush("Layal.Danger", System.Windows.Media.Brushes.Firebrick);
        }
    }

    /// <summary>
    /// Cancels operations that may be waiting on the browser or other services before application shutdown
    /// begins awaiting resource cleanup. The normal Closing handler still owns final window cleanup.
    /// </summary>
}
