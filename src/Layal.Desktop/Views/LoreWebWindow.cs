using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>
/// A local-only 3D map of encrypted memory notes. Links use user-defined category, title-term overlap,
/// and chronological adjacency; they are navigation hints, not AI-generated facts or semantic guarantees.
/// </summary>
public sealed class LoreWebWindow : Window
{
    // Keep inferred links sparse even when many notes share generic title words.
    // User-authored relationships are always retained separately.
    private const int MaxHeuristicEdgesPerNote = 6;
    private readonly bool _isArabic;
    private readonly Action<MemoryNote> _openNote;
    private readonly Func<MemoryNote, bool> _deleteNote;
    private readonly Func<MemoryNote, bool> _toggleArchiveNote;
    private readonly Func<string, string, string, bool> _linkNotes;
    private readonly Func<string, string, bool> _unlinkNotes;
    private readonly List<MemoryNote> _notes;
    private readonly Viewport3D _viewport;
    private readonly ListBox _noteList;
    private readonly TextBlock _selectedTitle;
    private readonly TextBlock _selectedContent;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _deleteButton;
    private readonly Button _archiveButton;
    private readonly ComboBox _linkTargetSelector;
    private readonly TextBox _linkLabelInput;
    private readonly Button _linkButton;
    private readonly Button _unlinkButton;
    private MemoryNote? _selectedNote;
    private readonly PerspectiveCamera _camera;
    private readonly Dictionary<ModelVisual3D, MemoryNote> _visualNotes = new();
    private readonly Dictionary<string, ModelVisual3D> _noteVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeometryModel3D> _noteModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point3D> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<ModelVisual3D, (string FromId, string ToId)> _edgeVisuals = new();
    private readonly List<NoteListEntry> _entries = new();
    private Point _dragStart;
    private bool _dragging;
    private bool _dragMoved;
    private double _yaw = 0.45;
    private double _pitch = 0.2;
    private double _distance = 235;

    public LoreWebWindow(
        IReadOnlyList<MemoryNote> notes,
        bool isArabic,
        Action<MemoryNote> openNote,
        Func<MemoryNote, bool> deleteNote,
        Func<MemoryNote, bool> toggleArchiveNote,
        Func<string, string, string, bool> linkNotes,
        Func<string, string, bool> unlinkNotes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _openNote = openNote ?? throw new ArgumentNullException(nameof(openNote));
        _deleteNote = deleteNote ?? throw new ArgumentNullException(nameof(deleteNote));
        _toggleArchiveNote = toggleArchiveNote ?? throw new ArgumentNullException(nameof(toggleArchiveNote));
        _linkNotes = linkNotes ?? throw new ArgumentNullException(nameof(linkNotes));
        _unlinkNotes = unlinkNotes ?? throw new ArgumentNullException(nameof(unlinkNotes));
        _isArabic = isArabic;
        _notes = notes.Take(100).OrderBy(note => note.CreatedAt).ToList();

        Title = isArabic ? "خريطة الذاكرة ثلاثية الأبعاد" : "3D memory web";
        Width = 1100;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = GetThemeBrush("Layal.Background", Brushes.White);
        Foreground = GetThemeBrush("Layal.Text", Brushes.Black);
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "خريطة محلية للملاحظات النشطة والمؤرشفة. التصنيفات تحددها أنت، والروابط مبنية على التصنيف وكلمات العنوان والترتيب الزمني؛ لا يُرسل المحتوى إلى AI."
            : "A local map of active and archived notes. Categories are user-defined; links use category, shared title terms and chronology. No content is sent to AI.");

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = isArabic ? "شبكة الملاحظات — محلية بالكامل" : "Memory web — entirely local",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        var privacy = new TextBlock
        {
            Text = isArabic
                ? "اسحب لتدوير المشهد واستخدم عجلة الفأرة للتقريب. تعرض القائمة التصنيف وحالة الأرشفة. يمكنك أرشفة الملاحظة أو استعادتها دون حذفها."
                : "Drag to rotate and use the mouse wheel to zoom. The list shows category and archive state. You can archive or restore a note without deleting it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = GetThemeBrush("Layal.Muted", Brushes.DimGray)
        };
        AutomationProperties.SetName(privacy, isArabic ? "شرح التنقل وخصوصية خريطة الذاكرة" : "Memory map navigation and privacy");
        header.Children.Add(privacy);
        var legend = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        legend.Inlines.Add(new Run
        {
            Text = isArabic ? "● نشطة" : "● Active",
            Foreground = new SolidColorBrush(Color.FromRgb(55, 164, 181))
        });
        legend.Inlines.Add(new Run { Text = "   " });
        legend.Inlines.Add(new Run
        {
            Text = isArabic ? "● مؤرشفة" : "● Archived",
            Foreground = new SolidColorBrush(Color.FromRgb(125, 132, 145))
        });
        legend.Inlines.Add(new Run { Text = "   " });
        legend.Inlines.Add(new Run
        {
            Text = isArabic ? "━ روابط تقريبية" : "━ Inferred links",
            Foreground = new SolidColorBrush(Color.FromRgb(95, 145, 165))
        });
        legend.Inlines.Add(new Run { Text = "   " });
        legend.Inlines.Add(new Run
        {
            Text = isArabic ? "━ علاقات صريحة" : "━ Explicit relationships",
            Foreground = new SolidColorBrush(Color.FromRgb(228, 157, 73))
        });
        AutomationProperties.SetName(legend, isArabic
            ? "مفتاح الألوان: الأزرق للملاحظات النشطة، والرمادي للمؤرشفة، والرمادي المزرق للروابط التقريبية، والذهبي للعلاقات التي حددها المستخدم."
            : "Color legend: teal nodes are active, gray nodes are archived, blue-gray edges are inferred, and gold edges are user-defined.");
        header.Children.Add(legend);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _viewport = new Viewport3D
        {
            ClipToBounds = true,
            IsHitTestVisible = true,
            Focusable = true,
            Cursor = Cursors.Hand,
            ToolTip = isArabic
                ? "لوحة المفاتيح: الأسهم للتدوير، + و- للتقريب، Home لإعادة ضبط المشهد."
                : "Keyboard: Arrow keys rotate, + and - zoom, Home resets the view."
        };
        AutomationProperties.SetName(_viewport, isArabic ? "رسم شبكة الذاكرة ثلاثية الأبعاد" : "3D memory web graph");
        AutomationProperties.SetHelpText(_viewport, isArabic
            ? "انقر على عقدة لعرض الملاحظة، واسحب لتدوير المشهد، واستخدم عجلة الفأرة للتقريب. عند تركيز الرسم استخدم الأسهم للتدوير و+ أو - للتقريب وHome لإعادة الضبط. ويمكن استخدام قائمة الملاحظات بلوحة المفاتيح."
            : "Click a node to inspect it, drag or use Arrow keys to rotate, use the mouse wheel or + / - to zoom, and press Home to reset. The note list is keyboard accessible.");
        _camera = new PerspectiveCamera
        {
            FieldOfView = 50,
            UpDirection = new Vector3D(0, 1, 0)
        };
        _viewport.Camera = _camera;
        KeyboardNavigation.SetIsTabStop(_viewport, true);
        KeyboardNavigation.SetTabIndex(_viewport, 0);
        _viewport.PreviewKeyDown += Viewport_PreviewKeyDown;
        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseWheel += Viewport_MouseWheel;
        var viewportFrame = new Border
        {
            Background = GetThemeBrush("Layal.Panel", Brushes.White),
            BorderBrush = GetThemeBrush("Layal.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Child = _viewport
        };
        Grid.SetColumn(viewportFrame, 0);
        contentGrid.Children.Add(viewportFrame);

        var side = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.9, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        side.Children.Add(new TextBlock
        {
            Text = isArabic ? "الملاحظات المحفوظة" : "Saved notes",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        _noteList = new ListBox
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(5),
            Background = GetThemeBrush("Layal.Panel", Brushes.White),
            Foreground = GetThemeBrush("Layal.Text", Brushes.Black),
            BorderBrush = GetThemeBrush("Layal.Border", Brushes.Gray),
            DisplayMemberPath = nameof(NoteListEntry.Description)
        };
        _noteList.ItemsSource = _entries;
        AutomationProperties.SetName(_noteList, isArabic ? "قائمة عقد الذاكرة" : "Memory note nodes");
        AutomationProperties.SetHelpText(_noteList, isArabic
            ? "استخدم الأسهم لاختيار الملاحظة. اختيار عقدة لا يرسل محتواها."
            : "Use the arrow keys to select a note. Selecting a node does not send its contents.");
        _noteList.SelectionChanged += NoteList_SelectionChanged;
        Grid.SetRow(_noteList, 1);
        side.Children.Add(_noteList);

        _selectedTitle = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 5)
        };
        AutomationProperties.SetName(_selectedTitle, isArabic ? "عنوان الملاحظة المحددة" : "Selected note title");
        Grid.SetRow(_selectedTitle, 2);
        side.Children.Add(_selectedTitle);

        _selectedContent = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(_selectedContent, isArabic ? "محتوى الملاحظة المحددة" : "Selected note content");
        AutomationProperties.SetHelpText(_selectedContent, isArabic
            ? "معاينة محلية للملاحظة المحددة."
            : "Local preview of the selected note.");
        var contentScroll = new ScrollViewer
        {
            Content = _selectedContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(contentScroll, 3);
        side.Children.Add(contentScroll);

        var relationshipPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var relationshipHeading = new TextBlock
        {
            Text = isArabic ? "علاقات صريحة بين الملاحظات" : "Explicit note relationships",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        AutomationProperties.SetName(relationshipHeading, isArabic ? "إدارة العلاقات الصريحة" : "Manage explicit relationships");
        relationshipPanel.Children.Add(relationshipHeading);
        var relationshipInputs = new WrapPanel { Orientation = Orientation.Horizontal };
        _linkTargetSelector = new ComboBox
        {
            MinWidth = 125, MaxWidth = 210, MinHeight = 30,
            Margin = new Thickness(0, 0, 5, 5),
            DisplayMemberPath = nameof(RelationshipTargetEntry.Description)
        };
        AutomationProperties.SetName(_linkTargetSelector, isArabic ? "الملاحظة المرتبطة" : "Related note target");
        _linkTargetSelector.SelectionChanged += LinkTargetSelector_SelectionChanged;
        _linkLabelInput = new TextBox
        {
            MinWidth = 90, MaxWidth = 150, MinHeight = 30,
            MaxLength = MemoryService.MaxRelationshipLabelCharacters,
            Margin = new Thickness(0, 0, 5, 5),
            Text = isArabic ? "مرتبط بـ" : "related to",
            FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(_linkLabelInput, isArabic ? "اسم العلاقة" : "Relationship label");
        _linkButton = new Button { Content = isArabic ? "ربط" : "Link", MinWidth = 60, MinHeight = 30, Margin = new Thickness(0, 0, 5, 5), IsEnabled = false };
        AutomationProperties.SetName(_linkButton, isArabic ? "إنشاء علاقة بين الملاحظتين" : "Create relationship between notes");
        _linkButton.Click += LinkButton_Click;
        _unlinkButton = new Button { Content = isArabic ? "إزالة العلاقة" : "Unlink", MinWidth = 82, MinHeight = 30, Margin = new Thickness(0, 0, 0, 5), IsEnabled = false };
        AutomationProperties.SetName(_unlinkButton, isArabic ? "إزالة العلاقة المحددة" : "Remove selected relationship");
        _unlinkButton.Click += UnlinkButton_Click;
        relationshipInputs.Children.Add(_linkTargetSelector);
        relationshipInputs.Children.Add(_linkLabelInput);
        relationshipInputs.Children.Add(_linkButton);
        relationshipInputs.Children.Add(_unlinkButton);
        relationshipPanel.Children.Add(relationshipInputs);
        Grid.SetRow(relationshipPanel, 4);
        side.Children.Add(relationshipPanel);

        _openButton = new Button
        {
            Content = isArabic ? "فتح الملاحظة في المحرر" : "Open note in editor",
            MinHeight = 34,
            MinWidth = 175,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            IsEnabled = false
        };
        AutomationProperties.SetName(_openButton, isArabic ? "فتح الملاحظة المحددة في محرر الذاكرة" : "Open selected note in memory editor");
        AutomationProperties.SetHelpText(_openButton, isArabic
            ? "يعيدك إلى محرر الذاكرة المحلي. لا يشارك الملاحظة مع الذكاء الاصطناعي."
            : "Opens the selected note in the local memory editor. It does not share the note with AI.");
        _openButton.Click += OpenButton_Click;
        _deleteButton = new Button
        {
            Content = isArabic ? "حذف الملاحظة" : "Delete note",
            MinHeight = 34,
            MinWidth = 115,
            Margin = new Thickness(8, 8, 0, 0),
            IsEnabled = false
        };
        AutomationProperties.SetName(_deleteButton, isArabic ? "حذف الملاحظة المحددة من الذاكرة" : "Delete selected memory note");
        AutomationProperties.SetHelpText(_deleteButton, isArabic
            ? "يحذف الملاحظة المحددة نهائيًا من مخزن الذاكرة المحلي بعد تأكيد منفصل. لا يحذف نسخًا موجودة في المحادثات."
            : "Permanently deletes the selected note from local memory after a separate confirmation. Copies in chat conversations are not deleted.");
        _deleteButton.Click += DeleteButton_Click;
        _archiveButton = new Button
        {
            Content = isArabic ? "أرشفة" : "Archive",
            MinHeight = 34,
            MinWidth = 100,
            Margin = new Thickness(8, 8, 0, 0),
            IsEnabled = false
        };
        AutomationProperties.SetName(_archiveButton, isArabic ? "أرشفة الملاحظة أو استعادتها" : "Archive or restore selected note");
        AutomationProperties.SetHelpText(_archiveButton, isArabic
            ? "ينقل الملاحظة إلى الأرشيف أو يستعيدها دون حذف محتواها."
            : "Archives or restores the note without deleting its content.");
        _archiveButton.Click += ArchiveButton_Click;
        var actionButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        actionButtons.Children.Add(_openButton);
        actionButtons.Children.Add(_archiveButton);
        actionButtons.Children.Add(_deleteButton);
        Grid.SetRow(actionButtons, 5);
        side.Children.Add(actionButtons);

        Grid.SetColumn(side, 1);
        contentGrid.Children.Add(side);
        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetThemeBrush("Layal.Muted", Brushes.DimGray)
        };
        AutomationProperties.SetName(_status, isArabic ? "حالة خريطة الذاكرة" : "Memory web status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        Content = root;
        BuildGraph();
        UpdateCamera();
        _status.Text = _notes.Count == 0
            ? (isArabic ? "لا توجد ملاحظات لإنشاء خريطة. أضف ملاحظة من تبويب الذاكرة." : "No notes to map. Add a note in the Memory tab.")
            : (isArabic
                ? $"تم تحميل {_notes.Count} ملاحظة محليًا. الروابط تقريبية ولا تعني بالضرورة علاقة دلالية."
                : $"Loaded {_notes.Count} notes locally. Connections are approximate and do not guarantee semantic relationships.");
    }

    private void BuildGraph()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(165, 175, 190)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -3)));
        _viewport.Children.Add(new ModelVisual3D { Content = lights });

        for (var index = 0; index < _notes.Count; index++)
        {
            var note = _notes[index];
            var position = PositionOnSphere(index, _notes.Count, 58);
            _positions[note.Id] = position;
            var radius = 2.5 + Math.Min(2.0, note.Title.Length / 55.0 + note.Content.Length / 6000.0);
            var nodeColor = note.IsArchived
                ? Color.FromRgb(125, 132, 145)
                : Color.FromRgb(55, 164, 181);
            var model = new GeometryModel3D
            {
                Geometry = CreateSphere(position, radius, 12, 9),
                Material = CreateMaterial(nodeColor),
                BackMaterial = CreateMaterial(nodeColor)
            };
            var visual = new ModelVisual3D { Content = model };
            _viewport.Children.Add(visual);
            _visualNotes[visual] = note;
            _noteVisuals[note.Id] = visual;
            _noteModels[note.Id] = model;
            var archiveLabel = note.IsArchived ? (_isArabic ? "مؤرشفة" : "Archived") : (_isArabic ? "نشطة" : "Active");
            var relationshipCount = note.Relationships?.Count ?? 0;
            _entries.Add(new NoteListEntry(note, $"{note.Title} · {note.Category} · {archiveLabel} · {relationshipCount} " + (_isArabic ? "علاقات" : "links")));
        }

        var explicitEdges = BuildExplicitEdges(_notes);
        foreach (var edge in BuildEdges(_notes))
        {
            if (!_positions.TryGetValue(edge.FromId, out var start) ||
                !_positions.TryGetValue(edge.ToId, out var end))
                continue;
            var geometry = CreateCylinder(start, end, 0.32, 7);
            var isExplicit = explicitEdges.Contains(edge);
            var material = CreateMaterial(isExplicit ? Color.FromArgb(225, 228, 157, 73) : Color.FromArgb(115, 95, 145, 165));
            var edgeVisual = new ModelVisual3D
            {
                Content = new GeometryModel3D { Geometry = geometry, Material = material, BackMaterial = material }
            };
            _viewport.Children.Add(edgeVisual);
            _edgeVisuals[edgeVisual] = (edge.FromId, edge.ToId);
        }
    }

    private static Brush GetThemeBrush(string resourceKey, Brush fallback)
    {
        // Lore Web can be constructed by isolated WPF tests without creating a second
        // Application instance. Only read application resources from their owning dispatcher.
        var application = Application.Current;
        if (application is null || !application.Dispatcher.CheckAccess())
            return fallback;

        return application.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private static Point3D PositionOnSphere(int index, int count, double radius)
    {
        if (count <= 1)
            return new Point3D(0, 0, 0);

        var y = 1.0 - (2.0 * (index + 0.5) / count);
        var horizontalRadius = Math.Sqrt(Math.Max(0, 1 - y * y));
        var angle = Math.PI * (3.0 - Math.Sqrt(5.0)) * index;
        return new Point3D(
            Math.Cos(angle) * horizontalRadius * radius,
            y * radius,
            Math.Sin(angle) * horizontalRadius * radius);
    }

    private static IReadOnlyList<(string FromId, string ToId)> BuildEdges(IReadOnlyList<MemoryNote> notes)
    {
        // Explicit user-authored links are never subject to the inferred-edge density cap.
        var edges = BuildExplicitEdges(notes);
        var heuristicDegree = notes
            .Select(note => note.Id)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => 0, StringComparer.Ordinal);

        void AddHeuristicEdge(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal))
                return;

            var edge = NormalizeEdge(left, right);
            if (edges.Contains(edge) ||
                !heuristicDegree.TryGetValue(left, out var leftDegree) ||
                !heuristicDegree.TryGetValue(right, out var rightDegree) ||
                leftDegree >= MaxHeuristicEdgesPerNote ||
                rightDegree >= MaxHeuristicEdgesPerNote)
                return;

            edges.Add(edge);
            heuristicDegree[left] = leftDegree + 1;
            heuristicDegree[right] = rightDegree + 1;
        }

        // Chronology is the most predictable navigation path, so preserve it first.
        for (var index = 0; index < notes.Count - 1; index++)
            AddHeuristicEdge(notes[index].Id, notes[index + 1].Id);

        // User-defined categories provide a local grouping signal. Connect adjacent notes
        // per category, subject to the same per-note cap as all other inferred edges.
        foreach (var categoryGroup in notes
                     .Select((note, index) => (Note: note, Index: index))
                     .GroupBy(item => MemoryService.NormalizeCategory(item.Note.Category), StringComparer.OrdinalIgnoreCase))
        {
            var categoryNotes = categoryGroup.OrderBy(item => item.Index).Select(item => item.Note).ToArray();
            for (var index = 0; index < categoryNotes.Length - 1; index++)
                AddHeuristicEdge(categoryNotes[index].Id, categoryNotes[index + 1].Id);
        }

        // Shared title words are only hints. Cap their contribution to prevent generic
        // words (for example "project" or "note") from producing a near-complete graph.
        var titleTokens = notes.Select(note => Tokenize(note.Title)).ToArray();
        for (var first = 0; first < notes.Count; first++)
        {
            if (!heuristicDegree.TryGetValue(notes[first].Id, out var firstDegree) ||
                firstDegree >= MaxHeuristicEdgesPerNote)
                continue;

            for (var second = first + 1; second < notes.Count; second++)
            {
                if (heuristicDegree[notes[first].Id] >= MaxHeuristicEdgesPerNote)
                    break;
                if (heuristicDegree[notes[second].Id] >= MaxHeuristicEdgesPerNote)
                    continue;

                if (titleTokens[first].Overlaps(titleTokens[second]))
                    AddHeuristicEdge(notes[first].Id, notes[second].Id);
            }
        }

        return edges
            .OrderBy(edge => edge.FromId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<(string FromId, string ToId)> BuildExplicitEdges(IReadOnlyList<MemoryNote> notes)
    {
        var edges = new HashSet<(string FromId, string ToId)>();
        foreach (var note in notes)
            foreach (var relationship in note.Relationships ?? new List<MemoryNoteRelationship>())
                if (relationship is not null && !string.IsNullOrWhiteSpace(relationship.TargetNoteId))
                    edges.Add(NormalizeEdge(note.Id, relationship.TargetNoteId));
        return edges;
    }

    private static (string FromId, string ToId) NormalizeEdge(string left, string right) =>
        string.CompareOrdinal(left, right) < 0 ? (left, right) : (right, left);

    private static HashSet<string> Tokenize(string title)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = new System.Text.StringBuilder();
        foreach (var character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else
            {
                if (token.Length >= 3)
                    tokens.Add(token.ToString());
                token.Clear();
            }
        }
        if (token.Length >= 3)
            tokens.Add(token.ToString());
        return tokens;
    }

    private static void AddEdge(HashSet<(string FromId, string ToId)> edges, string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return;
        edges.Add(string.CompareOrdinal(left, right) < 0 ? (left, right) : (right, left));
    }

    private static Geometry3D CreateSphere(Point3D center, double radius, int slices, int stacks)
    {
        var mesh = new MeshGeometry3D();
        for (var stack = 0; stack <= stacks; stack++)
        {
            var phi = Math.PI * stack / stacks;
            for (var slice = 0; slice <= slices; slice++)
            {
                var theta = 2 * Math.PI * slice / slices;
                mesh.Positions.Add(new Point3D(
                    center.X + radius * Math.Sin(phi) * Math.Cos(theta),
                    center.Y + radius * Math.Cos(phi),
                    center.Z + radius * Math.Sin(phi) * Math.Sin(theta)));
            }
        }

        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var a = stack * (slices + 1) + slice;
                var b = a + slices + 1;
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b + 1);
            }
        }
        return mesh;
    }

    private static Geometry3D CreateCylinder(Point3D start, Point3D end, double radius, int segments)
    {
        var axis = end - start;
        if (axis.Length < 0.001)
            return new MeshGeometry3D();
        axis.Normalize();

        var helper = Math.Abs(Vector3D.DotProduct(axis, new Vector3D(0, 1, 0))) > 0.9
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 1, 0);
        var u = Vector3D.CrossProduct(axis, helper);
        u.Normalize();
        var v = Vector3D.CrossProduct(axis, u);
        v.Normalize();

        var mesh = new MeshGeometry3D();
        for (var index = 0; index < segments; index++)
        {
            var angle = 2 * Math.PI * index / segments;
            var offset = u * (Math.Cos(angle) * radius) + v * (Math.Sin(angle) * radius);
            mesh.Positions.Add(start + offset);
            mesh.Positions.Add(end + offset);
        }

        for (var index = 0; index < segments; index++)
        {
            var current = index * 2;
            var next = ((index + 1) % segments) * 2;
            mesh.TriangleIndices.Add(current);
            mesh.TriangleIndices.Add(next);
            mesh.TriangleIndices.Add(current + 1);
            mesh.TriangleIndices.Add(current + 1);
            mesh.TriangleIndices.Add(next);
            mesh.TriangleIndices.Add(next + 1);
        }
        return mesh;
    }

    private static Material CreateMaterial(Color color)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        // The emissive layer keeps links and spheres visible even when the mesh has no explicit normal buffer.
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(85, color.R, color.G, color.B))));
        return material;
    }

    private void RebuildGraph()
    {
        _viewport.Children.Clear();
        _visualNotes.Clear();
        _noteVisuals.Clear();
        _noteModels.Clear();
        _positions.Clear();
        _edgeVisuals.Clear();
        _entries.Clear();
        BuildGraph();
        _noteList.Items.Refresh();
    }

    private void Viewport_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                _yaw -= 0.12;
                break;
            case Key.Right:
                _yaw += 0.12;
                break;
            case Key.Up:
                _pitch = Math.Clamp(_pitch + 0.12, -1.35, 1.35);
                break;
            case Key.Down:
                _pitch = Math.Clamp(_pitch - 0.12, -1.35, 1.35);
                break;
            case Key.Add:
            case Key.OemPlus:
                _distance = Math.Max(110, _distance - 15);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                _distance = Math.Min(420, _distance + 15);
                break;
            case Key.Home:
                _yaw = 0.45;
                _pitch = 0.2;
                _distance = 235;
                break;
            default:
                return;
        }

        UpdateCamera();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewport.Focus();
        _dragStart = e.GetPosition(_viewport);
        _dragging = true;
        _dragMoved = false;
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(_viewport);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        if (!_dragMoved && Math.Sqrt(dx * dx + dy * dy) < 4)
            return;

        _dragMoved = true;
        _yaw += dx * 0.008;
        _pitch = Math.Clamp(_pitch - dy * 0.008, -1.35, 1.35);
        _dragStart = current;
        UpdateCamera();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        _viewport.ReleaseMouseCapture();

        if (!_dragMoved)
        {
            var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport)) as RayMeshGeometry3DHitTestResult;
            if (hit?.VisualHit is ModelVisual3D visual && _visualNotes.TryGetValue(visual, out var note))
                SelectNote(note);
        }
        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance - e.Delta * 0.12, 110, 420);
        UpdateCamera();
        e.Handled = true;
    }

    private void UpdateCamera()
    {
        var horizontal = _distance * Math.Cos(_pitch);
        var position = new Point3D(
            horizontal * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch),
            horizontal * Math.Cos(_yaw));
        _camera.Position = position;
        _camera.LookDirection = new Vector3D(-position.X, -position.Y, -position.Z);
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_noteList.SelectedItem is NoteListEntry entry)
            SelectNote(entry.Note);
    }

    private void SelectNote(MemoryNote note)
    {
        _selectedNote = note;
        RefreshRelationshipTargets(note);
        foreach (var item in _notes)
        {
            if (!_noteModels.TryGetValue(item.Id, out var model))
                continue;
            var color = string.Equals(item.Id, note.Id, StringComparison.Ordinal)
                ? Color.FromRgb(244, 177, 72)
                : item.IsArchived ? Color.FromRgb(125, 132, 145) : Color.FromRgb(55, 164, 181);
            model.Material = CreateMaterial(color);
            model.BackMaterial = CreateMaterial(color);
        }

        if (_noteList.SelectedItem is not NoteListEntry selected ||
            !string.Equals(selected.Note.Id, note.Id, StringComparison.Ordinal))
        {
            _noteList.SelectedItem = _entries.FirstOrDefault(entry => string.Equals(entry.Note.Id, note.Id, StringComparison.Ordinal));
        }

        _selectedTitle.Text = $"{note.Title} · {note.Category}" + (note.IsArchived ? (_isArabic ? " (مؤرشفة)" : " (Archived)") : string.Empty);
        var relationshipLines = (note.Relationships ?? new List<MemoryNoteRelationship>())
            .Select(link =>
            {
                var targetTitle = _notes.FirstOrDefault(item => string.Equals(item.Id, link.TargetNoteId, StringComparison.Ordinal))?.Title
                    ?? (_isArabic ? "ملاحظة غير متاحة" : "Unavailable note");
                return $"{link.Label}: {targetTitle}";
            }).ToArray();
        _selectedContent.Text = relationshipLines.Length == 0
            ? note.Content
            : note.Content + Environment.NewLine + Environment.NewLine +
              (_isArabic ? "العلاقات التي حددتها:" : "User-defined relationships:") + Environment.NewLine +
              string.Join(Environment.NewLine, relationshipLines);
        _openButton.IsEnabled = true;
        _archiveButton.IsEnabled = true;
        _archiveButton.Content = note.IsArchived ? (_isArabic ? "استعادة" : "Restore") : (_isArabic ? "أرشفة" : "Archive");
        _deleteButton.IsEnabled = true;
        _status.Text = _isArabic
            ? $"الملاحظة المحددة: {note.Title}. لم يتم إرسال المحتوى."
            : $"Selected note: {note.Title}. No content was sent.";
    }

    private void RefreshRelationshipTargets(MemoryNote note)
    {
        _linkTargetSelector.Items.Clear();
        foreach (var target in _notes.Where(item => !string.Equals(item.Id, note.Id, StringComparison.Ordinal)))
        {
            var description = $"{target.Title} · {target.Category}" + (target.IsArchived ? (_isArabic ? " (مؤرشفة)" : " (Archived)") : string.Empty);
            _linkTargetSelector.Items.Add(new RelationshipTargetEntry(target.Id, description));
        }
        var existing = note.Relationships?.FirstOrDefault();
        var preferred = existing is null ? null : _linkTargetSelector.Items.OfType<RelationshipTargetEntry>()
            .FirstOrDefault(item => string.Equals(item.Id, existing.TargetNoteId, StringComparison.Ordinal));
        _linkTargetSelector.SelectedItem = preferred ?? _linkTargetSelector.Items.OfType<RelationshipTargetEntry>().FirstOrDefault();
        _linkLabelInput.Text = existing?.Label ?? (_isArabic ? "مرتبط بـ" : "related to");
        _linkButton.IsEnabled = _linkTargetSelector.SelectedItem is RelationshipTargetEntry;
        _unlinkButton.IsEnabled = existing is not null && preferred is not null;
    }

    private void LinkTargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedNote is null || _linkTargetSelector.SelectedItem is not RelationshipTargetEntry target)
        {
            _linkButton.IsEnabled = false;
            _unlinkButton.IsEnabled = false;
            return;
        }
        var existing = _selectedNote.Relationships?.FirstOrDefault(link => string.Equals(link.TargetNoteId, target.Id, StringComparison.Ordinal));
        _linkLabelInput.Text = existing?.Label ?? (_isArabic ? "مرتبط بـ" : "related to");
        _linkButton.IsEnabled = true;
        _unlinkButton.IsEnabled = existing is not null;
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote is null || _linkTargetSelector.SelectedItem is not RelationshipTargetEntry target) return;
        try
        {
            var label = MemoryService.NormalizeRelationshipLabel(_linkLabelInput.Text);
            if (!_linkNotes(_selectedNote.Id, target.Id, label))
            {
                _status.Text = _isArabic ? "تعذر إنشاء العلاقة؛ تحقق من وجود الملاحظتين." : "Could not create the relationship; verify both notes still exist.";
                return;
            }
            var source = _notes.First(note => string.Equals(note.Id, _selectedNote.Id, StringComparison.Ordinal));
            var destination = _notes.First(note => string.Equals(note.Id, target.Id, StringComparison.Ordinal));
            source.Relationships ??= new List<MemoryNoteRelationship>();
            destination.Relationships ??= new List<MemoryNoteRelationship>();
            source.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, destination.Id, StringComparison.Ordinal));
            destination.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, source.Id, StringComparison.Ordinal));
            source.Relationships.Add(new MemoryNoteRelationship { TargetNoteId = destination.Id, Label = label });
            destination.Relationships.Add(new MemoryNoteRelationship { TargetNoteId = source.Id, Label = label });
            RebuildGraph();
            SelectNote(source);
            _status.Text = _isArabic ? "تم حفظ العلاقة بين الملاحظتين محليًا." : "The relationship was saved locally.";
        }
        catch (Exception ex) { _status.Text = (_isArabic ? "تعذر حفظ العلاقة: " : "Could not save relationship: ") + ex.Message; }
    }

    private void UnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNote is null || _linkTargetSelector.SelectedItem is not RelationshipTargetEntry target) return;
        var confirm = MessageBox.Show(this,
            _isArabic ? "إزالة العلاقة بين الملاحظتين؟ لن تُحذف أي ملاحظة." : "Remove the relationship between these notes? Neither note will be deleted.",
            _isArabic ? "إزالة العلاقة" : "Remove relationship",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (!_unlinkNotes(_selectedNote.Id, target.Id))
            {
                _status.Text = _isArabic ? "لم يتم العثور على العلاقة لإزالتها." : "The relationship was not found.";
                return;
            }
            var source = _notes.First(note => string.Equals(note.Id, _selectedNote.Id, StringComparison.Ordinal));
            var destination = _notes.First(note => string.Equals(note.Id, target.Id, StringComparison.Ordinal));
            source.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, destination.Id, StringComparison.Ordinal));
            destination.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, source.Id, StringComparison.Ordinal));
            RebuildGraph();
            SelectNote(source);
            _status.Text = _isArabic ? "تمت إزالة العلاقة دون حذف الملاحظتين." : "The relationship was removed without deleting either note.";
        }
        catch (Exception ex) { _status.Text = (_isArabic ? "تعذرت إزالة العلاقة: " : "Could not remove relationship: ") + ex.Message; }
    }

    private sealed record RelationshipTargetEntry(string Id, string Description);

    private void ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_noteList.SelectedItem is not NoteListEntry entry)
            return;

        var shouldArchive = !entry.Note.IsArchived;
        var prompt = shouldArchive
            ? (_isArabic
                ? $"سيتم نقل «{entry.Note.Title}» إلى الأرشيف. لن تُحذف ويمكن استعادتها لاحقًا. هل تريد المتابعة؟"
                : $"Move “{entry.Note.Title}” to the archive? It will not be deleted and can be restored later.")
            : (_isArabic
                ? $"ستتم استعادة «{entry.Note.Title}» إلى الملاحظات النشطة. هل تريد المتابعة؟"
                : $"Restore “{entry.Note.Title}” to active memory? Continue?");
        var confirm = MessageBox.Show(this, prompt,
            shouldArchive
                ? (_isArabic ? "تأكيد الأرشفة" : "Confirm archive")
                : (_isArabic ? "تأكيد الاستعادة" : "Confirm restore"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            if (!_toggleArchiveNote(entry.Note))
            {
                _status.Text = _isArabic
                    ? "لم يتم تحديث حالة الأرشفة؛ قد تكون الملاحظة حُذفت بالفعل."
                    : "Archive state was not updated; the note may already have been deleted.";
                return;
            }

            entry.Note.IsArchived = shouldArchive;
            RebuildGraph();
            SelectNote(entry.Note);
            _status.Text = shouldArchive
                ? (_isArabic ? "تمت أرشفة الملاحظة دون حذفها." : "The note was archived without deleting it.")
                : (_isArabic ? "تمت استعادة الملاحظة إلى الذاكرة النشطة." : "The note was restored to active memory.");
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذرت الأرشفة أو الاستعادة: " : "Could not archive or restore note: ") + ex.Message;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_noteList.SelectedItem is not NoteListEntry entry)
            return;

        var confirm = MessageBox.Show(this,
            _isArabic
                ? $"سيتم حذف الملاحظة «{entry.Note.Title}» نهائيًا من الذاكرة المحلية المشفرة. لا يشمل ذلك نسخًا في المحادثات أو ملفات سبق تصديرها. هل تريد المتابعة؟"
                : $"Permanently delete “{entry.Note.Title}” from encrypted local memory? This does not remove copies in chat history or previously exported files. Continue?",
            _isArabic ? "تأكيد حذف الملاحظة" : "Confirm note deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            if (!_deleteNote(entry.Note))
            {
                _status.Text = _isArabic
                    ? "لم يتم حذف الملاحظة؛ قد تكون حُذفت بالفعل أو تعذر حفظ التغيير."
                    : "The note was not deleted; it may already be gone or the change could not be saved.";
                return;
            }

            var deletedId = entry.Note.Id;
            _notes.RemoveAll(note => string.Equals(note.Id, deletedId, StringComparison.Ordinal));
            foreach (var note in _notes) note.Relationships?.RemoveAll(link => string.Equals(link.TargetNoteId, deletedId, StringComparison.Ordinal));
            _selectedNote = null;
            _noteList.SelectedItem = null;
            RebuildGraph();

            _selectedTitle.Text = string.Empty;
            _selectedContent.Text = string.Empty;
            _linkTargetSelector.Items.Clear();
            _linkLabelInput.Clear();
            _linkButton.IsEnabled = false;
            _unlinkButton.IsEnabled = false;
            _openButton.IsEnabled = false;
            _deleteButton.IsEnabled = false;
            _archiveButton.IsEnabled = false;
            if (_entries.Count > 0)
            {
                _noteList.SelectedItem = _entries[0];
                _status.Text = _isArabic
                    ? $"تم حذف «{entry.Note.Title}» من الذاكرة المحلية وتحديث الخريطة."
                    : $"Deleted “{entry.Note.Title}” from local memory and updated the graph.";
            }
            else
            {
                _status.Text = _isArabic
                    ? "تم حذف آخر ملاحظة. لا توجد ملاحظات لإنشاء خريطة جديدة."
                    : "The last note was deleted. There are no notes left to map.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = (_isArabic ? "تعذر حذف الملاحظة: " : "Could not delete note: ") + ex.Message;
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_noteList.SelectedItem is not NoteListEntry entry)
            return;

        _openNote(entry.Note);
        Close();
    }

    private sealed record NoteListEntry(MemoryNote Note, string Description);
}
