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
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(_details, isArabic ? "معاينة العنصر المحدد" : "Selected item preview");
        AutomationProperties.SetHelpText(_details, isArabic
            ? "معاينة محلية فقط. الملفات تعرض بياناتها الوصفية ولا تُقرأ محتوياتها تلقائيًا."
            : "Local-only preview. Files show metadata; their contents are not read automatically.");
        Grid.SetRow(_details, 0); right.Children.Add(_details);

        var note = new TextBlock
        {
            Text = isArabic
                ? "مهم: فهرسة الملفات لا تفتحها ولا تشغّلها. تصدير مهمة أو ملاحظة ينشئ ملف نصي غير مشفّر في المسار الذي تختاره."
                : "Important: file indexing does not open or execute files. Exporting a task or memory creates an unencrypted text file at the path you choose.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        Grid.SetRow(note, 1); right.Children.Add(note);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _openButton = new Button { Content = isArabic ? "فتح / عرض" : "Open / show", MinHeight = 34, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        AutomationProperties.SetName(_openButton, isArabic ? "فتح العنصر المحدد أو عرضه" : "Open or show selected item");
        _openButton.Click += OpenButton_Click;
        actions.Children.Add(_openButton);
        _exportButton = new Button { Content = isArabic ? "تصدير…" : "Export…", MinHeight = 34, MinWidth = 100, IsEnabled = false };
        AutomationProperties.SetName(_exportButton, isArabic ? "تصدير نتيجة المهمة أو الملاحظة المحددة" : "Export selected task result or memory");
        AutomationProperties.SetHelpText(_exportButton, isArabic ? "ينشئ ملفًا نصيًا غير مشفّر بعد اختيار المسار." : "Creates an unencrypted text file after you choose the path.");
        _exportButton.Click += ExportButton_Click;
        actions.Children.Add(_exportButton);
        Grid.SetRow(actions, 2); right.Children.Add(actions);

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
            var searchable = task.Goal + "\n" + task.Status + "\n" + task.Result;
            entries.Add(new WorkspaceEntry("task", task.Id, task.Goal, task.Status,
                task.UpdatedAt, searchable, task.Result, task, null, null));
        }
        foreach (var note in _memoryService.GetAll())
        {
            entries.Add(new WorkspaceEntry("memory", note.Id, note.Title,
                _isArabic ? "ملاحظة" : "Memory", note.UpdatedAt,
                note.Title + "\n" + note.Content, note.Content, null, note, null));
        }
        entries.AddRange(IndexWorkspaceFiles());
        _all = entries.OrderByDescending(entry => entry.UpdatedAt).ToList();
        ApplyFilter();
        _status.Text = _isArabic
            ? $"الفهرس محلي: {_all.Count(entry => entry.Kind == "task")} مهمة، {_all.Count(entry => entry.Kind == "memory")} ملاحظة، {_all.Count(entry => entry.Kind == "file")} ملف/مجلد. حد الملفات {MaximumWorkspaceFiles}."
            : $"Local index: {_all.Count(entry => entry.Kind == "task")} tasks, {_all.Count(entry => entry.Kind == "memory")} memories, {_all.Count(entry => entry.Kind == "file")} files/folders. File cap: {MaximumWorkspaceFiles}.";
    }

    private IEnumerable<WorkspaceEntry> IndexWorkspaceFiles()
    {
        var output = new List<WorkspaceEntry>();
        if (!Directory.Exists(_workspaceRoot))
            return output;

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
                    if (info.Name is ".git" or ".vs" or "bin" or "obj" or "node_modules" or ".cache")
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
        if (_items.SelectedItem is not WorkspaceEntry entry)
        {
            _details.Text = string.Empty;
            _openButton.IsEnabled = false;
            _exportButton.IsEnabled = false;
            return;
        }
        _details.Text = entry.Kind switch
        {
            "task" => (_isArabic ? "مهمة\n" : "Task\n") + entry.Title +
                "\n" + (_isArabic ? "الحالة: " : "Status: ") + entry.Subtitle +
                "\n" + (_isArabic ? "آخر تحديث: " : "Updated: ") + entry.UpdatedAt.ToLocalTime().ToString("g") +
                "\n\n" + Truncate(entry.Content, 8000),
            "memory" => (_isArabic ? "ملاحظة\n" : "Memory\n") + entry.Title +
                "\n" + (_isArabic ? "آخر تحديث: " : "Updated: ") + entry.UpdatedAt.ToLocalTime().ToString("g") +
                "\n\n" + Truncate(entry.Content, 8000),
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
                if (MessageBox.Show(this, question, _isArabic ? "عرض العنصر في Explorer" : "Reveal item in Explorer",
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
        if (MessageBox.Show(this, warning, _isArabic ? "تأكيد التصدير" : "Confirm export",
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
