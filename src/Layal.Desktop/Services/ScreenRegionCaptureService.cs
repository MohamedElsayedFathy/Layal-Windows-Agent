using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Layal.Services;

/// <summary>
/// Captures one explicitly selected monitor, previews it locally, and returns only the user-selected crop.
/// The returned image is not uploaded; the caller must attach it to a chat and the user must press Send.
/// </summary>
public sealed class ScreenRegionCaptureService
{
    public const int MinimumRegionPixels = 16;
    public const int MaximumImageBytes = 10 * 1024 * 1024;
    private const long MaximumScreenPixels = 40_000_000;

    public ScreenRegionCaptureResult? CaptureRegionUnderCursor(bool isArabic)
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var bounds = screen.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            (long)bounds.Width * bounds.Height > MaximumScreenPixels)
        {
            throw new InvalidOperationException(isArabic
                ? "دقة الشاشة تتجاوز الحد الآمن لالتقاط الذاكرة. استخدم أداة قص Windows ثم أرفق الصورة."
                : "This display exceeds the safe capture memory limit. Use Windows Snipping Tool and attach the saved image.");
        }

        using var screenshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(screenshot))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        using var dialog = new RegionSelectionDialog(screenshot, isArabic, screen.WorkingArea);
        if (dialog.ShowDialog() != DialogResult.OK)
            return null;

        var selected = dialog.SelectedRegion;
        var png = EncodeCropPng(screenshot, selected);
        return new ScreenRegionCaptureResult(png, selected.Width, selected.Height);
    }

    /// <summary>Copies only the selected rectangle and re-encodes it as PNG without carrying source metadata.</summary>
    public static byte[] EncodeCropPng(Bitmap source, Rectangle selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var clipped = Rectangle.Intersect(bounds, selection);
        if (clipped.Width < MinimumRegionPixels || clipped.Height < MinimumRegionPixels)
            throw new ArgumentOutOfRangeException(nameof(selection),
                $"The selected region must be at least {MinimumRegionPixels} by {MinimumRegionPixels} pixels and intersect the image.");

        using var crop = source.Clone(clipped, PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        if (stream.Length <= 0 || stream.Length > MaximumImageBytes)
            throw new InvalidOperationException("The selected image is empty or exceeds the 10 MB attachment limit.");
        return stream.ToArray();
    }

    private sealed class RegionSelectionDialog : Form
    {
        private readonly Bitmap _screenshot;
        private readonly bool _isArabic;
        private readonly SelectionCanvas _canvas;
        private readonly Label _status;
        private readonly Button _useSelection;
        private Point _dragStart;
        private bool _dragging;
        private Rectangle _selection;

        public Rectangle SelectedRegion => _selection;

        public RegionSelectionDialog(Bitmap screenshot, bool isArabic, Rectangle workArea)
        {
            _screenshot = screenshot ?? throw new ArgumentNullException(nameof(screenshot));
            _isArabic = isArabic;
            Text = isArabic ? "معاينة وقص جزء من الشاشة" : "Preview and crop a screen region";
            AccessibleName = Text;
            AccessibleDescription = isArabic
                ? "اسحب لتحديد مستطيل. لن تُرسل الصورة قبل إغلاق المعاينة والضغط على إرسال في ليال."
                : "Drag to select a rectangle. The image is not sent until you close the preview and press Send in Layal.";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;
            ShowIcon = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            TopMost = true;
            BackColor = Color.FromArgb(28, 31, 40);
            ForeColor = Color.White;
            MinimumSize = new Size(420, 320);

            var width = Math.Min(1180, Math.Max(420, Math.Min(screenshot.Width, Math.Max(420, workArea.Width - 32))));
            var height = Math.Min(850, Math.Max(320, Math.Min(screenshot.Height + 86, Math.Max(320, workArea.Height - 32))));
            ClientSize = new Size(width, height);
            Location = new Point(
                workArea.Left + Math.Max(0, (workArea.Width - Width) / 2),
                workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));

            _canvas = new SelectionCanvas
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 20, 26),
                Cursor = Cursors.Cross,
                AccessibleName = isArabic ? "معاينة الشاشة وتحديد المنطقة" : "Screen preview and region selection",
                AccessibleDescription = isArabic
                    ? "اسحب بالماوس لتحديد المنطقة المراد إرفاقها. اضغط Escape للإلغاء."
                    : "Drag with the mouse to select the region to attach. Press Escape to cancel."
            };
            _canvas.Paint += Canvas_Paint;
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += Canvas_MouseUp;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(12, 8, 12, 8) };
            _status = new Label
            {
                Dock = DockStyle.Fill,
                Text = isArabic
                    ? "اسحب لتحديد الجزء المطلوب. المعاينة محلية ولا يحدث إرسال تلقائي."
                    : "Drag to select a region. Preview stays local; nothing is sent automatically.",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AccessibleName = isArabic ? "حالة تحديد الشاشة" : "Screen selection status"
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 210,
                FlowDirection = isArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            _useSelection = new Button
            {
                Text = isArabic ? "استخدام التحديد" : "Use selection",
                Width = 112,
                Height = 32,
                Enabled = false,
                AccessibleName = isArabic ? "اعتماد المنطقة المحددة" : "Use selected region"
            };
            _useSelection.Click += (_, _) =>
            {
                if (_selection.Width < MinimumRegionPixels || _selection.Height < MinimumRegionPixels)
                    return;
                DialogResult = DialogResult.OK;
                Close();
            };
            var cancel = new Button
            {
                Text = isArabic ? "إلغاء" : "Cancel",
                Width = 72,
                Height = 32,
                DialogResult = DialogResult.Cancel,
                AccessibleName = isArabic ? "إلغاء قص الشاشة" : "Cancel screen crop"
            };
            buttons.Controls.Add(_useSelection);
            buttons.Controls.Add(cancel);
            footer.Controls.Add(_status);
            footer.Controls.Add(buttons);
            Controls.Add(_canvas);
            Controls.Add(footer);
            AcceptButton = _useSelection;
            CancelButton = cancel;
        }

        private RectangleF ImageBounds()
        {
            var availableWidth = Math.Max(1, _canvas.ClientSize.Width);
            var availableHeight = Math.Max(1, _canvas.ClientSize.Height);
            var scale = Math.Min((float)availableWidth / _screenshot.Width, (float)availableHeight / _screenshot.Height);
            var width = _screenshot.Width * scale;
            var height = _screenshot.Height * scale;
            return new RectangleF((availableWidth - width) / 2f, (availableHeight - height) / 2f, width, height);
        }

        private bool TryMapPoint(Point point, bool clamp, out Point mapped)
        {
            var image = ImageBounds();
            if (!clamp && !image.Contains(point))
            {
                mapped = Point.Empty;
                return false;
            }

            var x = Math.Clamp((point.X - image.Left) / Math.Max(1f, image.Width) * _screenshot.Width,
                0f, _screenshot.Width - 1f);
            var y = Math.Clamp((point.Y - image.Top) / Math.Max(1f, image.Height) * _screenshot.Height,
                0f, _screenshot.Height - 1f);
            mapped = new Point((int)Math.Round(x), (int)Math.Round(y));
            return true;
        }

        private void Canvas_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !TryMapPoint(e.Location, clamp: false, out var point))
                return;
            _dragStart = point;
            _selection = Rectangle.Empty;
            _dragging = true;
            _useSelection.Enabled = false;
            _status.Text = _isArabic ? "استمر بالسحب لتحديد المنطقة." : "Keep dragging to select the region.";
            _canvas.Invalidate();
        }

        private void Canvas_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_dragging || !TryMapPoint(e.Location, clamp: true, out var point))
                return;
            UpdateSelection(point);
        }

        private void Canvas_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_dragging || e.Button != MouseButtons.Left)
                return;
            _dragging = false;
            if (TryMapPoint(e.Location, clamp: true, out var point))
                UpdateSelection(point);
        }

        private void UpdateSelection(Point end)
        {
            _selection = Rectangle.FromLTRB(
                Math.Min(_dragStart.X, end.X),
                Math.Min(_dragStart.Y, end.Y),
                Math.Max(_dragStart.X, end.X) + 1,
                Math.Max(_dragStart.Y, end.Y) + 1);
            _useSelection.Enabled = _selection.Width >= MinimumRegionPixels &&
                                    _selection.Height >= MinimumRegionPixels;
            _status.Text = _useSelection.Enabled
                ? (_isArabic
                    ? $"التحديد: {_selection.Width} × {_selection.Height} بكسل. اضغط «استخدام التحديد» ثم أرسل يدويًا."
                    : $"Selection: {_selection.Width} × {_selection.Height} pixels. Choose Use selection, then send manually.")
                : (_isArabic
                    ? $"الحد الأدنى للتحديد {MinimumRegionPixels} × {MinimumRegionPixels} بكسل."
                    : $"The selection must be at least {MinimumRegionPixels} × {MinimumRegionPixels} pixels.");
            _canvas.Invalidate();
        }

        private void Canvas_Paint(object? sender, PaintEventArgs e)
        {
            var imageBounds = ImageBounds();
            e.Graphics.Clear(_canvas.BackColor);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_screenshot, imageBounds);

            if (_selection.Width <= 0 || _selection.Height <= 0)
                return;

            var selectionOnCanvas = new RectangleF(
                imageBounds.Left + _selection.Left * imageBounds.Width / _screenshot.Width,
                imageBounds.Top + _selection.Top * imageBounds.Height / _screenshot.Height,
                _selection.Width * imageBounds.Width / _screenshot.Width,
                _selection.Height * imageBounds.Height / _screenshot.Height);
            using var fill = new SolidBrush(Color.FromArgb(36, 70, 200, 120));
            using var outline = new Pen(Color.LimeGreen, 2f);
            e.Graphics.FillRectangle(fill, selectionOnCanvas);
            e.Graphics.DrawRectangle(outline, selectionOnCanvas.X, selectionOnCanvas.Y,
                selectionOnCanvas.Width, selectionOnCanvas.Height);
        }
    }
}

public sealed record ScreenRegionCaptureResult(byte[] PngBytes, int Width, int Height);
