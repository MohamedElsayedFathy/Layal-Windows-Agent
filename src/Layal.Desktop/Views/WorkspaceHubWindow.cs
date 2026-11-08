using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using System.Diagnostics;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// Local-only index of task history, encrypted memories, and files in LayalWorkspace.
/// The file index reads metadata only; it does not execute files or send any content to AI.
/// </summary>
public sealed class WorkspaceHubWindow : Window
{
    private const int MaximumWorkspaceFiles = 300;
    private const int MaximumFolderDepth = 4;
    private readonly TaskHistoryService _taskHistory;
    private readonly MemoryService _memoryService;
    private readonly WorkspaceArtifactService _artifactService;
    private readonly string _workspaceRoot;
    private readonly bool _isArabic;
    private readonly Action<string> _openTask;
    private readonly Action<string> _openMemory;
    private readonly TextBox _search;
    private readonly ComboBox _filter;
    private readonly ListBox _items;
    private readonly TextBlock _details;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _exportButton;
    private readonly Button _backupButton;
    private readonly Button _restoreButton;
    private readonly ListBox _artifactList;
    private readonly Button _deleteArtifactButton;
    private string _workspaceIndexWarning = string.Empty;
    private readonly ObservableCollection<WorkspaceEntry> _visible = new();
    private IReadOnlyList<WorkspaceEntry> _all = Array.Empty<WorkspaceEntry>();

    public WorkspaceHubWindow(
        TaskHistoryService taskHistory,
        MemoryService memoryService,
        string workspaceRoot,
        bool isArabic,
        Action<string> openTask,
        Action<string> openMemory)
    {
        _taskHistory = taskHistory ?? throw new ArgumentNullException(nameof(taskHistory));
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        _artifactService = new WorkspaceArtifactService(_workspaceRoot);
        _isArabic = isArabic;
        _openTask = openTask ?? throw new ArgumentNullException(nameof(openTask));
        _openMemory = openMemory ?? throw new ArgumentNullException(nameof(openMemory));

        Title = isArabic ? "مساحة عمل ليال" : "Layal workspace hub";
        Width = 1050; Height = 720; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "يجمع المهام والملاحظات وملفات مساحة العمل محليًا. فهرسة الملفات تقرأ البيانات الوصفية فقط، ولا ترسل محتوى إلى AI."
            : "Locally indexes tasks, memories, and workspace files. File indexing reads metadata only and does not send content to AI.");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock { Text = isArabic ? "مساحة العمل والنتائج" : "Workspace and results", FontSize = 21, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = (isArabic ? "مجلد العمل: " : "Workspace folder: ") + _workspaceRoot +
                (isArabic ? "\nالبحث والمعاينة محليان. فتح ملف يعرضه في Explorer ولا يشغّله." : "\nSearch and preview are local. Opening a file reveals it in Explorer; it does not execute it."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        });
        Grid.SetRow(heading, 0); root.Children.Add(heading);

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search = new TextBox { MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_search, isArabic ? "البحث في مساحة العمل" : "Search workspace");
        AutomationProperties.SetHelpText(_search, isArabic
            ? "ابحث في أسماء المهام والملاحظات ومسارات الملفات؛ لا يتم إرسال البحث إلى AI."
            : "Search task titles, memory text, and file paths; the query is not sent to AI.");
        _search.TextChanged += (_, _) => ApplyFilter();
        searchRow.Children.Add(_search);

        _filter = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
        _filter.Items.Add(new ComboBoxItem { Content = isArabic ? "الكل" : "All", Tag = "all" });
        _filter.Items.Add(new ComboBoxItem { Content = isArabic ? "المهام والنتائج" : "Tasks and results", Tag = "task" });
        _filter.Items.Add(new ComboBoxItem { Content = isArabic ? "الملاحظات" : "Memories", Tag = "memory" });
        _filter.Items.Add(new ComboBoxItem { Content = isArabic ? "الملفات" : "Files", Tag = "file" });
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        AutomationProperties.SetName(_filter, isArabic ? "تصفية نوع عناصر مساحة العمل" : "Filter workspace item type");
        Grid.SetColumn(_filter, 1); searchRow.Children.Add(_filter);

        var refresh = new Button { Content = isArabic ? "تحديث" : "Refresh", MinHeight = 32, MinWidth = 85 };
        AutomationProperties.SetName(refresh, isArabic ? "تحديث فهرس مساحة العمل" : "Refresh workspace index");
        refresh.Click += (_, _) => RefreshIndex();
        Grid.SetColumn(refresh, 2); searchRow.Children.Add(refresh);
        Grid.SetRow(searchRow, 1); root.Children.Add(searchRow);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        _items = new ListBox
        {
            ItemsSource = _visible,
            DisplayMemberPath = nameof(WorkspaceEntry.Display),
            Padding = new Thickness(6),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray
        };
        AutomationProperties.SetName(_items, isArabic ? "عناصر مساحة العمل" : "Workspace items");
        AutomationProperties.SetHelpText(_items, isArabic
            ? "حدد مهمة أو ملاحظة أو ملفًا لعرض بياناته المحلية."
            : "Select a task, memory, or file to see its local details.");
        _items.SelectionChanged += Items_SelectionChanged;
        content.Children.Add(_items);

        var right = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(_details, isArabic ? "معاينة العنصر المحدد" : "Selected item preview");
        AutomationProperties.SetHelpText(_details, isArabic
            ? "المعاينة كاملة وقابلة للتمرير. الملفات تعرض بياناتها الوصفية فقط ولا تُقرأ محتوياتها تلقائيًا."
            : "The full preview is scrollable. Files show metadata only; their contents are not read automatically.");
        var detailsScroll = new ScrollViewer
        {
            Content = _details,
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetName(detailsScroll, isArabic ? "منطقة تمرير معاينة مساحة العمل" : "Workspace preview scroll area");
        Grid.SetRow(detailsScroll, 0); right.Children.Add(detailsScroll);

        var artifactsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        artifactsPanel.Children.Add(new TextBlock
        {
            Text = isArabic ? "ملفات ناتجة عن المهمة المحددة" : "Files produced by the selected task",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _artifactList = new ListBox
        {
            MinHeight = 58,
            MaxHeight = 92,
            Padding = new Thickness(4),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray
        };
        AutomationProperties.SetName(_artifactList, isArabic ? "ملفات المهمة المرتبطة" : "Task-linked workspace files");
        AutomationProperties.SetHelpText(_artifactList, isArabic
            ? "حدد ملفًا واحدًا ثم انقله إلى سلة محذوفات Windows. لن يُحذف شيء قبل التأكيد."
            : "Select one file to move it to the Windows Recycle Bin. Nothing is deleted before confirmation.");
        _artifactList.SelectionChanged += (_, _) =>
            _deleteArtifactButton.IsEnabled = _items.SelectedItem is WorkspaceEntry { Kind: "task" } &&
                _artifactList.SelectedItem is string;
        artifactsPanel.Children.Add(_artifactList);

        _deleteArtifactButton = new Button
        {
            Content = isArabic ? "نقل الملف المحدد إلى سلة المحذوفات" : "Move selected file to Recycle Bin",
            MinHeight = 32,
            MinWidth = 220,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false
        };
        AutomationProperties.SetName(_deleteArtifactButton, isArabic ? "حذف ملف المهمة المحدد إلى سلة محذوفات Windows" : "Move the selected task file to the Windows Recycle Bin");
        AutomationProperties.SetHelpText(_deleteArtifactButton, isArabic
            ? "يتحقق من بقاء الملف داخل LayalWorkspace ومن عدم مروره عبر رابط رمزي، ويطلب تأكيدًا قبل الحذف."
            : "Verifies that the file remains inside LayalWorkspace and is not reached through a reparse point, then asks for confirmation.");
        _deleteArtifactButton.Click += DeleteArtifactButton_Click;
        artifactsPanel.Children.Add(_deleteArtifactButton);
        Grid.SetRow(artifactsPanel, 1); right.Children.Add(artifactsPanel);

        var note = new TextBlock
        {
            Text = _isArabic
                ? "مهم: النسخة الاحتياطية ملف JSON غير مشفّر وقد يحتوي على نص المهام والملاحظات؛ احفظه في مكان موثوق. فهرس الملفات وصفي فقط ولا تُنسخ ملفات العمل أو تُشغّل."
                : "Important: the workspace backup is an unencrypted JSON file and may contain task and memory text; store it securely. The file manifest is metadata only; workspace files are not copied or executed.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        Grid.SetRow(note, 2); right.Children.Add(note);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _openButton = new Button { Content = isArabic ? "فتح / عرض" : "Open / show", MinHeight = 34, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        AutomationProperties.SetName(_openButton, isArabic ? "فتح العنصر المحدد أو عرضه" : "Open or show selected item");
        _openButton.Click += OpenButton_Click;
        actions.Children.Add(_openButton);
        _exportButton = new Button { Content = isArabic ? "تصدير…" : "Export…", MinHeight = 34, MinWidth = 100, IsEnabled = false };
        AutomationProperties.SetName(_exportButton, isArabic ? "تصدير نتيجة المهمة أو الملاحظة المحددة" : "Export selected task result or memory");
        AutomationProperties.SetHelpText(_exportButton, isArabic ? "ينشئ ملفًا نصيًا غير مشفّر بعد اختيار المسار." : "Creates an unencrypted text file after you choose the path.");
        _exportButton.Click += ExportButton_Click;
        actions.Children.Add(_exportButton);

        _backupButton = new Button
        {
            Content = isArabic ? "نسخة احتياطية…" : "Backup…",
            MinHeight = 34,
            MinWidth = 125,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(_backupButton, isArabic ? "تصدير نسخة احتياطية كاملة لمساحة العمل" : "Export a full workspace backup");
        AutomationProperties.SetHelpText(_backupButton, isArabic
            ? "ينشئ ملف JSON غير مشفّر للمهام والذاكرة. يمكن تضمين محتوى ملفات صغيرة من LayalWorkspace بموافقة منفصلة وحدود حجم، أو الاكتفاء بالبيانات الوصفية."
            : "Creates an unencrypted JSON backup of tasks and memory. Small LayalWorkspace files may be embedded after separate confirmation and within strict size limits, or metadata-only can be used.");
        _backupButton.Click += BackupButton_Click;
        actions.Children.Add(_backupButton);

        _restoreButton = new Button
        {
            Content = isArabic ? "استعادة نسخة…" : "Restore backup…",
            MinHeight = 34,
            MinWidth = 135,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(_restoreButton, isArabic ? "دمج نسخة مساحة عمل احتياطية" : "Merge a workspace backup");
        AutomationProperties.SetHelpText(_restoreButton, isArabic
            ? "يفحص ملف JSON ثم يعرض السجلات ومحتويات الملفات المضمنة؛ دمج السجلات واستعادة الملفات عمليتان منفصلتان بموافقة."
            : "Validates a JSON backup and previews records and embedded files; merging records and restoring files are separate consented actions.");
        _restoreButton.Click += RestoreButton_Click;
        actions.Children.Add(_restoreButton);

        Grid.SetRow(actions, 3); right.Children.Add(actions);

        Grid.SetColumn(right, 1); content.Children.Add(right);
        Grid.SetRow(content, 2); root.Children.Add(content);

        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray };
        AutomationProperties.SetName(_status, isArabic ? "حالة مساحة العمل" : "Workspace status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Content = root;
        RefreshIndex();
    }

    private void RefreshIndex()
    {
        var entries = new List<WorkspaceEntry>();
        foreach (var task in _taskHistory.GetRecent(100))
        {
            var artifactText = string.Join("\n", task.Artifacts ?? new List<string>());
            var searchable = task.Goal + "\n" + task.Status + "\n" + task.Result + "\n" + artifactText;
            entries.Add(new WorkspaceEntry("task", task.Id, task.Goal, task.Status,
                task.UpdatedAt, searchable, task.Result, task, null, null));
        }
        foreach (var note in _memoryService.GetAll(includeArchived: true))
        {
            var noteState = note.IsArchived
                ? (_isArabic ? "مؤرشفة" : "Archived")
                : (_isArabic ? "نشطة" : "Active");
            entries.Add(new WorkspaceEntry("memory", note.Id, note.Title,
                $"{note.Category} · {noteState}", note.UpdatedAt,
                note.Category + "\n" + note.Title + "\n" + note.Content, note.Content, null, note, null));
        }
        entries.AddRange(IndexWorkspaceFiles());
        _all = entries.OrderByDescending(entry => entry.UpdatedAt).ToList();
        ApplyFilter();
        var indexSummary = _isArabic
            ? $"الفهرس محلي: {_all.Count(entry => entry.Kind == "task")} مهمة، {_all.Count(entry => entry.Kind == "memory")} ملاحظة (بما فيها المؤرشفة)، {_all.Count(entry => entry.Kind == "file")} ملف/مجلد. حد الملفات {MaximumWorkspaceFiles}."
            : $"Local index: {_all.Count(entry => entry.Kind == "task")} tasks, {_all.Count(entry => entry.Kind == "memory")} memories (including archived), {_all.Count(entry => entry.Kind == "file")} files/folders. File cap: {MaximumWorkspaceFiles}.";
        _status.Text = string.IsNullOrWhiteSpace(_workspaceIndexWarning)
            ? indexSummary
            : indexSummary + Environment.NewLine + _workspaceIndexWarning;
    }

    private IEnumerable<WorkspaceEntry> IndexWorkspaceFiles()
    {
        var output = new List<WorkspaceEntry>();
        if (!Directory.Exists(_workspaceRoot))
            return output;

        try
        {
            _artifactService.ValidateWorkspaceRoot();
            _workspaceIndexWarning = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _workspaceIndexWarning = _isArabic
                ? "توقف فهرس الملفات لأن مسار LayalWorkspace أو أحد مجلداته الأب يحتوي على رابط رمزي أو مسار غير آمن."
                : "File indexing stopped because LayalWorkspace or one of its parent directories is a symbolic link or unsafe path.";
            return output;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_workspaceRoot, 0));
        var rootPrefix = Path.TrimEndingDirectorySeparator(_workspaceRoot) + Path.DirectorySeparatorChar;
        while (pending.Count > 0 && output.Count < MaximumWorkspaceFiles)
        {
            var (directory, depth) = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).Take(MaximumWorkspaceFiles).ToArray();
                files = Directory.EnumerateFiles(directory).Take(MaximumWorkspaceFiles).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in files)
            {
                if (output.Count >= MaximumWorkspaceFiles) break;
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var fullPath = Path.GetFullPath(path);
                    if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
                    var detail = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:N0} bytes · {1:g}", info.Length, info.LastWriteTime);
                    output.Add(new WorkspaceEntry("file", fullPath, Path.GetFileName(path), detail,
                        new DateTimeOffset(info.LastWriteTime), relative + "\n" + detail,
                        string.Empty, null, null, fullPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Skip entries that disappear or become inaccessible while indexing.
                }
            }

            if (depth >= MaximumFolderDepth) continue;
            foreach (var path in childDirectories)
            {
                if (output.Count >= MaximumWorkspaceFiles) break;
                try
                {
                    var info = new DirectoryInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals(".cache", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var fullPath = Path.GetFullPath(path);
                    if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push((fullPath, depth + 1));
                    output.Add(new WorkspaceEntry("file", fullPath, info.Name,
                        _isArabic ? "مجلد" : "Folder", new DateTimeOffset(info.LastWriteTime),
                        Path.GetRelativePath(_workspaceRoot, fullPath), string.Empty,
                        null, null, fullPath, IsDirectory: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }
        }
        return output;
    }

    private void ApplyFilter()
    {
        var filter = (_filter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var query = _search.Text.Trim();
        _visible.Clear();
        foreach (var entry in _all)
        {
            if (filter != "all" && !string.Equals(filter, entry.Kind, StringComparison.Ordinal)) continue;
            if (query.Length > 0 && !entry.Searchable.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            _visible.Add(entry);
        }
        _items.SelectedItem = null;
        _details.Text = string.Empty;
        _openButton.IsEnabled = false;
        _exportButton.IsEnabled = false;
        _status.Text = _isArabic ? $"عدد النتائج: {_visible.Count}" : $"Results: {_visible.Count}";
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _artifactList.Items.Clear();
        _deleteArtifactButton.IsEnabled = false;
        if (_items.SelectedItem is not WorkspaceEntry entry)
        {
            _details.Text = string.Empty;
            _openButton.IsEnabled = false;
            _exportButton.IsEnabled = false;
            return;
        }
        if (entry.Kind == "task" && entry.Task?.Artifacts is { Count: > 0 } taskArtifactPaths)
        {
            foreach (var artifact in taskArtifactPaths)
                _artifactList.Items.Add(artifact);
        }
        _details.Text = entry.Kind switch
        {
            "task" => (_isArabic ? "مهمة\n" : "Task\n") + entry.Title +
                "\n" + (_isArabic ? "الحالة: " : "Status: ") + entry.Subtitle +
                "\n" + (_isArabic ? "آخر تحديث: " : "Updated: ") + entry.UpdatedAt.ToLocalTime().ToString("g") +
                (entry.Task?.Artifacts is { Count: > 0 } artifacts
                    ? "\n\n" + (_isArabic ? "الملفات المرتبطة بالمهمة:" : "Task artifacts:") + "\n• " + string.Join("\n• ", artifacts)
                    : string.Empty) +
                "\n\n" + entry.Content,
            "memory" => (_isArabic ? "ملاحظة\n" : "Memory\n") + entry.Title +
                "\n" + (_isArabic ? "آخر تحديث: " : "Updated: ") + entry.UpdatedAt.ToLocalTime().ToString("g") +
                "\n\n" + entry.Content,
            _ => (_isArabic ? "عنصر مساحة عمل\n" : "Workspace item\n") + entry.Title +
                "\n" + entry.Path +
                "\n" + entry.Subtitle +
                (_isArabic ? "\n\nلم تتم قراءة محتوى الملف." : "\n\nFile content has not been read.")
        };
        _openButton.IsEnabled = true;
        _exportButton.IsEnabled = entry.Kind is "task" or "memory";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.SelectedItem is not WorkspaceEntry entry) return;
        switch (entry.Kind)
        {
            case "task":
                _openTask(entry.Id);
                Close();
                return;
            case "memory":
                _openMemory(entry.Id);
                Close();
                return;
            case "file":
                if (entry.Path is null) return;
                var question = _isArabic
                    ? "سيُفتح مستكشف الملفات ليعرض العنصر المحدد فقط؛ لن يتم تشغيل الملف. هل تريد المتابعة؟\n\n" + entry.Path
                    : "File Explorer will reveal the selected item only; the file will not be executed. Continue?\n\n" + entry.Path;
                if (LayalDialogService.Show(this, question, _isArabic ? "عرض العنصر في Explorer" : "Reveal item in Explorer",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                try
                {
                    if (entry.IsDirectory)
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + entry.Path + "\"") { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + entry.Path + "\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _status.Text = (_isArabic ? "تعذر فتح Explorer: " : "Could not open Explorer: ") + ex.Message;
                }
                return;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.SelectedItem is not WorkspaceEntry entry || entry.Kind is not ("task" or "memory"))
            return;
        var content = entry.Kind == "task"
            ? string.Join("\n\n", entry.Title, entry.Subtitle, entry.Content)
            : string.Join("\n\n", entry.Title, entry.Content);
        var dialog = new SaveFileDialog
        {
            Title = _isArabic ? "تصدير عنصر مساحة العمل" : "Export workspace item",
            FileName = SafeFileName(entry.Title) + ".txt",
            DefaultExt = ".txt",
            Filter = _isArabic ? "ملفات نصية (*.txt)|*.txt" : "Text files (*.txt)|*.txt",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var warning = _isArabic
            ? "سيتم حفظ النص في ملف غير مشفّر خارج مخزن ليال المشفر. احفظه في مكان موثوق. هل تريد المتابعة؟"
            : "This exports text to an unencrypted file outside Layal's encrypted store. Save it somewhere trusted. Continue?";
        if (LayalDialogService.Show(this, warning, _isArabic ? "تأكيد التصدير" : "Confirm export",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _status.Text = (_isArabic ? "تم تصدير الملف: " : "Exported file: ") + dialog.FileName;
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر التصدير: " : "Export failed: ") + ex.Message;
        }
    }

    private void DeleteArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.SelectedItem is not WorkspaceEntry { Kind: "task" } entry ||
            _artifactList.SelectedItem is not string relativePath)
            return;

        var question = _isArabic
            ? "سيُنقل الملف المحدد فقط من مساحة LayalWorkspace إلى سلة محذوفات Windows، وسيُزال رابطه من سجل المهمة. لن تُحذف المهمة أو بقية ملفاتها. هل تريد المتابعة؟\\n\\n" + relativePath
            : "Only this file will be moved from LayalWorkspace to the Windows Recycle Bin, and its task-history reference will be removed. The task and its other files will remain. Continue?\\n\\n" + relativePath;
        if (LayalDialogService.Show(this, question,
            _isArabic ? "تأكيد حذف مخرج المهمة" : "Confirm task artifact removal",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            _artifactService.SendToRecycleBin(relativePath);
            if (!_taskHistory.RemoveArtifact(entry.Id, relativePath))
            {
                _status.Text = _isArabic
                    ? "نُقل الملف إلى سلة المحذوفات، لكن تعذر تحديث رابط سجل المهمة. حدّث مساحة العمل للتحقق."
                    : "The file was moved to the Recycle Bin, but its task-history link could not be updated. Refresh the workspace to verify.";
                RefreshIndex();
                return;
            }

            RefreshIndex();
            _status.Text = _isArabic
                ? "تم نقل الملف المحدد إلى سلة محذوفات Windows وإزالة رابطه من المهمة."
                : "The selected file was moved to the Windows Recycle Bin and unlinked from the task.";
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حذف مخرج المهمة: " : "Could not remove task artifact: ") + ex.Message;
        }
    }

    private void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = _isArabic ? "إنشاء نسخة مساحة عمل احتياطية" : "Create workspace backup",
            FileName = "layal-workspace-backup.json",
            DefaultExt = ".json",
            Filter = _isArabic ? "نسخة ليال الاحتياطية (*.json)|*.json" : "Layal workspace backup (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var warning = _isArabic
            ? "سيُنشأ ملف JSON غير مشفّر يحتوي على سجل المهام ونصوص الذاكرة النشطة والمؤرشفة ومراجع الملفات. لا تُصدّر النسخة إلى مجلد مشترك أو سحابي غير موثوق. هل تريد المتابعة؟"
            : "This creates an unencrypted JSON file containing task history, active and archived memory text, and artifact references. Do not export it to an untrusted shared or cloud folder. Continue?";
        if (LayalDialogService.Show(this, warning,
            _isArabic ? "تنبيه خصوصية للنسخة الاحتياطية" : "Workspace backup privacy warning",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            var includeFileContents = LayalDialogService.Show(this,
                _isArabic
                    ? "هل تريد تضمين محتوى الملفات الصغيرة الموجودة داخل LayalWorkspace؟ سيُضمَّن بحد أقصى 100 ملف، و1 ميجابايت لكل ملف، و4 ميجابايت إجمالًا. ملفات أكبر أو خارج الحدود ستظهر كبيانات وصفية فقط. ستحتوي نسخة JSON غير المشفّرة على محتوى الملفات نفسه؛ لا ترفعها أو تشاركها دون مراجعة."
                    : "Include small files from LayalWorkspace? The backup embeds at most 100 files, 1 MiB per file, and 4 MiB total. Larger or out-of-limit files remain metadata-only. The unencrypted JSON will contain the actual file contents; do not upload or share it without review.",
                _isArabic ? "تضمين محتوى الملفات؟" : "Include file contents?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            var service = new WorkspaceSnapshotService(_taskHistory, _memoryService, _workspaceRoot);
            var json = service.ExportJson(includeFileContents);
            var preview = service.PreviewImportJson(json);

            if (includeFileContents)
            {
                var entries = preview.FileEntries ?? Array.Empty<WorkspaceSnapshotFilePreview>();
                var embedded = entries.Where(entry => entry.HasContent).ToArray();
                var omitted = entries.Where(entry => !entry.IsDirectory && !entry.HasContent &&
                    !string.IsNullOrWhiteSpace(entry.ContentOmittedReason)).ToArray();
                var review = _isArabic
                    ? $"سيتم تضمين محتوى {embedded.Length} ملف فعليًا داخل JSON غير المشفّر. راجع المسارات والأحجام قبل الكتابة:\n\n" +
                      FormatFilePreviewList(embedded, isArabic: true) +
                      (omitted.Length > 0 ? "\n\nملفات لم يُضمّن محتواها (ستظل بيانات وصفية فقط):\n" + FormatFilePreviewList(omitted, isArabic: true, includeOmissionReason: true) : string.Empty) +
                      "\n\nهل تريد حفظ النسخة بهذه الملفات؟"
                    : $"Exactly {embedded.Length} file(s) will have their contents embedded in the unencrypted JSON. Review paths and sizes before writing:\n\n" +
                      FormatFilePreviewList(embedded, isArabic: false) +
                      (omitted.Length > 0 ? "\n\nFiles whose content was omitted (metadata only):\n" + FormatFilePreviewList(omitted, isArabic: false, includeOmissionReason: true) : string.Empty) +
                      "\n\nSave the backup with these files?";
                if (LayalDialogService.Show(this, review,
                    _isArabic ? "مراجعة محتويات النسخة" : "Review embedded backup files",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            WorkspaceSnapshotService.WriteJsonAtomically(dialog.FileName, json);

            _status.Text = _isArabic
                ? $"تم إنشاء النسخة غير المشفّرة: {dialog.FileName} — محتوى {preview.FilesWithContent} ملف مضمّن."
                : $"Unencrypted backup created: {dialog.FileName} — embedded contents for {preview.FilesWithContent} file(s).";
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر إنشاء النسخة الاحتياطية: " : "Workspace backup failed: ") + ex.Message;
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _isArabic ? "اختيار نسخة مساحة عمل احتياطية" : "Select a workspace backup",
            Filter = _isArabic ? "نسخة ليال الاحتياطية (*.json)|*.json" : "Layal workspace backup (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var json = ReadBoundedBackupFile(dialog.FileName, WorkspaceSnapshotService.MaximumJsonBytes);
            var service = new WorkspaceSnapshotService(_taskHistory, _memoryService, _workspaceRoot);
            var preview = service.PreviewImportJson(json);
            var embeddedPreview = FormatFilePreviewList(
                (preview.FileEntries ?? Array.Empty<WorkspaceSnapshotFilePreview>()).Where(entry => entry.HasContent),
                _isArabic);
            var summary = _isArabic
                ? $"النسخة المؤرخة {preview.ExportedAt.ToLocalTime():g} تحتوي على:\n• {preview.TaskCount} مهمة\n• {preview.MemoryCount} ملاحظة (بما فيها المؤرشفة)\n• {preview.FileManifestCount} سجل ملف/مجلد، منها {preview.FilesWithContent} ملفًا يحتوي النسخ الاحتياطي على محتواه\n\nالملفات ذات المحتوى المضمّن (لن تُكتب قبل موافقة منفصلة):\n{embeddedPreview}\n\nسيُدمج فقط ما لا يحمل معرّفًا موجودًا بالفعل. لن تُستبدل أو تُحذف أي مهمة أو ملاحظة حالية. لن تُستعاد الملفات في هذه الخطوة. قد يتضمن JSON نصوصًا ومحتوى ملفات خاصة غير مشفّرة. هل تريد دمج السجلات؟"
                : $"Backup dated {preview.ExportedAt.ToLocalTime():g} contains:\n• {preview.TaskCount} tasks\n• {preview.MemoryCount} memories (including archived)\n• {preview.FileManifestCount} file/folder entries, including {preview.FilesWithContent} with embedded file contents\n\nFiles with embedded contents (nothing is written before separate consent):\n{embeddedPreview}\n\nOnly records with IDs not already present will be merged. Existing tasks and memories are never overwritten or deleted. No files will be restored in this step. The JSON may contain sensitive plaintext and file contents. Merge records?";
            if (LayalDialogService.Show(this, summary,
                _isArabic ? "مراجعة واستعادة النسخة الاحتياطية" : "Review and restore workspace backup",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var restoreFiles = false;
            if (preview.FilesWithContent > 0)
            {
                restoreFiles = LayalDialogService.Show(this,
                    _isArabic
                        ? $"تحتوي النسخة على محتوى {preview.FilesWithContent} ملف. هل تريد استعادة الملفات المضمنة إلى LayalWorkspace؟ لن يُستبدل أي ملف موجود؛ ستُتخطى المسارات الموجودة، وسيُرفض أي مسار غير آمن أو رابط رمزي."
                        : $"The backup contains {preview.FilesWithContent} embedded file(s). Restore these files into LayalWorkspace? Existing files will never be overwritten; existing paths are skipped, and unsafe paths or symbolic links are rejected.",
                    _isArabic ? "تأكيد استعادة الملفات" : "Confirm file restoration",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            }

            var result = service.ImportJson(json, restoreWorkspaceFiles: restoreFiles);
            RefreshIndex();
            _status.Text = _isArabic
                ? $"اكتمل الدمج: {result.TasksImported} مهمة و{result.MemoriesImported} ملاحظة. استُعيد {result.FilesRestored} ملف وتُخطّي {result.FilesSkipped} ملف."
                : $"Merge complete: {result.TasksImported} tasks and {result.MemoriesImported} memories. Restored {result.FilesRestored} file(s); skipped {result.FilesSkipped}.";
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذرت استعادة النسخة الاحتياطية: " : "Workspace backup restore failed: ") + ex.Message;
        }
    }

    private static string ReadBoundedBackupFile(string path, int maximumBytes)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > maximumBytes)
            throw new InvalidOperationException($"The selected backup exceeds the {maximumBytes / (1024 * 1024)} MB limit.");

        using var buffer = new MemoryStream((int)Math.Min(source.Length, maximumBytes));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = source.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException("The selected backup grew beyond the configured size limit while it was being read.");
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.ToArray());
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("The selected backup is not valid UTF-8 JSON.", ex);
        }
    }

    private static string FormatFilePreviewList(
        IEnumerable<WorkspaceSnapshotFilePreview> entries,
        bool isArabic,
        bool includeOmissionReason = false,
        int maximumVisible = 100)
    {
        var values = entries.Take(maximumVisible + 1).ToArray();
        if (values.Length == 0)
            return isArabic ? "لا توجد ملفات." : "No files.";

        var lines = values.Take(maximumVisible).Select(entry =>
        {
            var kind = entry.IsDirectory ? (isArabic ? "مجلد" : "folder") : (isArabic ? "ملف" : "file");
            var line = $"• {entry.RelativePath} — {entry.SizeBytes:N0} bytes ({kind})";
            if (includeOmissionReason && !string.IsNullOrWhiteSpace(entry.ContentOmittedReason))
                line += $" — {entry.ContentOmittedReason}";
            return line;
        }).ToList();

        if (values.Length > maximumVisible)
        {
            var remaining = entries.Count() - maximumVisible;
            lines.Add(isArabic ? $"… و{remaining} عنصرًا آخر." : $"… and {remaining} more item(s).");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((value ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length > 80) safe = safe[..80];
        return string.IsNullOrWhiteSpace(safe) ? "workspace-item" : safe;
    }

    private static string Truncate(string? value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? string.Empty : value[..maximum] + "…";

    private sealed record WorkspaceEntry(
        string Kind,
        string Id,
        string Title,
        string Subtitle,
        DateTimeOffset UpdatedAt,
        string Searchable,
        string Content,
        AgentTaskRecord? Task,
        MemoryNote? Memory,
        string? Path,
        bool IsDirectory = false)
    {
        public string Display => $"{Kind switch { "task" => "TASK", "memory" => "MEMORY", _ => "FILE" }} · {Title} · {Subtitle}";
    }
}
