using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Layal.Models;

namespace Layal.Views;

/// <summary>
/// A local-only 3D map of encrypted memory notes. Links use title-term overlap and chronological
/// adjacency; they are visual navigation hints, not AI-generated facts or semantic guarantees.
/// </summary>
public sealed class LoreWebWindow : Window
{
    private readonly bool _isArabic;
    private readonly Action<MemoryNote> _openNote;
    private readonly List<MemoryNote> _notes;
    private readonly Viewport3D _viewport;
    private readonly ListBox _noteList;
    private readonly TextBlock _selectedTitle;
    private readonly TextBlock _selectedContent;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly PerspectiveCamera _camera;
    private readonly Dictionary<ModelVisual3D, MemoryNote> _visualNotes = new();
    private readonly Dictionary<string, ModelVisual3D> _noteVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeometryModel3D> _noteModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point3D> _positions = new(StringComparer.Ordinal);
    private readonly List<NoteListEntry> _entries = new();
    private Point _dragStart;
    private bool _dragging;
    private bool _dragMoved;
    private double _yaw = 0.45;
    private double _pitch = 0.2;
    private double _distance = 235;

    public LoreWebWindow(IReadOnlyList<MemoryNote> notes, bool isArabic, Action<MemoryNote> openNote)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _openNote = openNote ?? throw new ArgumentNullException(nameof(openNote));
        _isArabic = isArabic;
        _notes = notes.Take(100).OrderBy(note => note.CreatedAt).ToList();

        Title = isArabic ? "خريطة الذاكرة ثلاثية الأبعاد" : "3D memory web";
        Width = 1100;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "خريطة محلية للملاحظات المحفوظة. الروابط تقريبية حسب كلمات العنوان والترتيب الزمني. لا يُرسل أي محتوى إلى الذكاء الاصطناعي."
            : "A local map of saved notes. Links are approximate, based on title terms and chronology. No content is sent to AI.");

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
                ? "اسحب بالماوس لتدوير المشهد، واستخدم عجلة الفأرة للتقريب. اختر نقطة من الرسم أو القائمة لعرض ملاحظة. الروابط تقريبية فقط."
                : "Drag to rotate and use the mouse wheel to zoom. Select a node or list item to inspect a note. Links are approximate only.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        };
        AutomationProperties.SetName(privacy, isArabic ? "شرح التنقل وخصوصية خريطة الذاكرة" : "Memory map navigation and privacy");
        header.Children.Add(privacy);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _viewport = new Viewport3D
        {
            ClipToBounds = true,
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            IsHitTestVisible = true,
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(_viewport, isArabic ? "رسم شبكة الذاكرة ثلاثية الأبعاد" : "3D memory web graph");
        AutomationProperties.SetHelpText(_viewport, isArabic
            ? "انقر على عقدة لعرض الملاحظة، واسحب لتدوير المشهد، واستخدم عجلة الفأرة للتقريب. يمكن أيضًا استخدام قائمة الملاحظات بلوحة المفاتيح."
            : "Click a node to inspect the note, drag to rotate, and use the mouse wheel to zoom. The note list is keyboard accessible.");
        _camera = new PerspectiveCamera
        {
            FieldOfView = 50,
            UpDirection = new Vector3D(0, 1, 0)
        };
        _viewport.Camera = _camera;
        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseWheel += Viewport_MouseWheel;
        Grid.SetColumn(_viewport, 0);
        contentGrid.Children.Add(_viewport);

        var side = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.9, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });
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
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray
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
            TextTrimming = TextTrimming.CharacterEllipsis,
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
        Grid.SetRow(_openButton, 4);
        side.Children.Add(_openButton);

        Grid.SetColumn(side, 1);
        contentGrid.Children.Add(side);
        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
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
            var model = new GeometryModel3D
            {
                Geometry = CreateSphere(position, radius, 12, 9),
                Material = CreateMaterial(Color.FromRgb(55, 164, 181)),
                BackMaterial = CreateMaterial(Color.FromRgb(55, 164, 181))
            };
            var visual = new ModelVisual3D { Content = model };
            _viewport.Children.Add(visual);
            _visualNotes[visual] = note;
            _noteVisuals[note.Id] = visual;
            _noteModels[note.Id] = model;
            _entries.Add(new NoteListEntry(note, $"{note.Title} — {note.UpdatedAt.LocalDateTime:g}"));
        }

        foreach (var edge in BuildEdges(_notes))
        {
            if (!_positions.TryGetValue(edge.FromId, out var start) ||
                !_positions.TryGetValue(edge.ToId, out var end))
                continue;
            var geometry = CreateCylinder(start, end, 0.28, 7);
            var material = CreateMaterial(Color.FromArgb(135, 95, 145, 165));
            _viewport.Children.Add(new ModelVisual3D
            {
                Content = new GeometryModel3D { Geometry = geometry, Material = material, BackMaterial = material }
            });
        }
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
        var edges = new HashSet<(string FromId, string ToId)>();
        for (var index = 0; index < notes.Count - 1; index++)
            AddEdge(edges, notes[index].Id, notes[index + 1].Id);

        var titleTokens = notes.ToDictionary(note => note.Id, note => Tokenize(note.Title), StringComparer.Ordinal);
        for (var first = 0; first < notes.Count; first++)
        {
            for (var second = first + 1; second < notes.Count; second++)
            {
                if (titleTokens[notes[first].Id].Overlaps(titleTokens[notes[second].Id]))
                    AddEdge(edges, notes[first].Id, notes[second].Id);
            }
        }

        return edges.ToList();
    }

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

    private static Material CreateMaterial(Color color) =>
        new DiffuseMaterial(new SolidColorBrush(color));

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
        foreach (var item in _notes)
        {
            if (!_noteModels.TryGetValue(item.Id, out var model))
                continue;
            var color = string.Equals(item.Id, note.Id, StringComparison.Ordinal)
                ? Color.FromRgb(244, 177, 72)
                : Color.FromRgb(55, 164, 181);
            model.Material = CreateMaterial(color);
            model.BackMaterial = CreateMaterial(color);
        }

        if (_noteList.SelectedItem is not NoteListEntry selected ||
            !string.Equals(selected.Note.Id, note.Id, StringComparison.Ordinal))
        {
            _noteList.SelectedItem = _entries.FirstOrDefault(entry => string.Equals(entry.Note.Id, note.Id, StringComparison.Ordinal));
        }

        _selectedTitle.Text = note.Title;
        _selectedContent.Text = note.Content;
        _openButton.IsEnabled = true;
        _status.Text = _isArabic
            ? $"الملاحظة المحددة: {note.Title}. لم يتم إرسال المحتوى."
            : $"Selected note: {note.Title}. No content was sent.";
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
