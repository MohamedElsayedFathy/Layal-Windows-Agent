using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
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
    public const long MaximumScreenPixels = 40_000_000;

    /// <summary>
    /// Rejects invalid or excessively large display bounds before allocating a full-screen bitmap.
    /// Exposed as a pure policy check so the memory-safety boundary can be regression-tested.
    /// </summary>
    public static void ValidateDisplayBounds(Rectangle bounds, bool isArabic)
    {
        if (bounds.Width > 0 && bounds.Height > 0 &&
            (long)bounds.Width * bounds.Height <= MaximumScreenPixels)
            return;

        throw new InvalidOperationException(isArabic
            ? "دقة الشاشة تتجاوز الحد الآمن لالتقاط الذاكرة. استخدم أداة قص Windows ثم أرفق الصورة."
            : "This display exceeds the safe capture memory limit. Use Windows Snipping Tool and attach the saved image.");
    }

    public ScreenRegionCaptureResult? CaptureRegionUnderCursor(
        bool isArabic,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screen = Screen.FromPoint(Cursor.Position);
        var bounds = screen.Bounds;
        ValidateDisplayBounds(bounds, isArabic);

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
        cancellationToken.ThrowIfCancellationRequested();

        using var dialog = new RegionSelectionDialog(screenshot, isArabic, screen.WorkingArea);
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            // The preview is a modal WinForms dialog on the UI thread. BeginInvoke keeps
            // cancellation safe when it is requested from another dispatcher callback.
            try
            {
                // If cancellation races native-handle creation, BeginInvoke may fail;
                // the Shown handler below observes the token and closes the dialog.
                dialog.BeginInvoke((Action)(() =>
                {
                    if (dialog.IsDisposed)
                        return;
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                }));
            }
            catch (InvalidOperationException)
            {
                // The dialog may have closed while the cancellation callback was queued.
            }
        });
        dialog.Shown += (_, _) =>
        {
            // Covers cancellation racing with the creation of the dialog's native handle.
            if (cancellationToken.IsCancellationRequested)
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selected = dialog.SelectedRegion;
        var png = EncodeCropPng(screenshot, selected);
        if (cancellationToken.IsCancellationRequested)
        {
            Array.Clear(png, 0, png.Length);
            cancellationToken.ThrowIfCancellationRequested();
        }
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
        private readonly LiveRegionStatusLabel _status;
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
                AccessibleRole = AccessibleRole.Graphic,
                AccessibleName = isArabic ? "معاينة الشاشة وتحديد المنطقة" : "Screen preview and region selection",
                AccessibleDescription = isArabic
                    ? "اسحب لتحديد المنطقة، أو اضغط Space لإنشاء تحديد في الوسط. الأسهم لتحريك التحديد وShift مع الأسهم لتغيير حجمه. Ctrl مع الأسهم لتحريك دقيق. Enter للاختيار وEscape للإلغاء."
                    : "Drag to select, or press Space to create a centered crop. Arrow keys move it; Shift+arrows resize it; Ctrl+arrows move one pixel. Enter chooses; Escape cancels.",
                TabStop = true
            };
            _canvas.Paint += Canvas_Paint;
            _canvas.GotFocus += (_, _) => _canvas.Invalidate();
            _canvas.LostFocus += (_, _) => _canvas.Invalidate();
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += Canvas_MouseUp;
            _canvas.KeyDown += Canvas_KeyDown;
            Shown += (_, _) => _canvas.Focus();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(12, 8, 12, 8) };
            _status = new LiveRegionStatusLabel
            {
                Dock = DockStyle.Fill,
                Text = isArabic
                    ? "اسحب لتحديد الجزء المطلوب. المعاينة محلية ولا يحدث إرسال تلقائي."
                    : "Drag to select a region. Preview stays local; nothing is sent automatically.",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AccessibleRole = AccessibleRole.StaticText,
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

        private RectangleF ImageBounds() =>
            ScreenRegionSelectionGeometry.FitImageToCanvas(_screenshot.Size, _canvas.ClientSize);

        private bool TryMapPoint(Point point, bool clamp, out Point mapped) =>
            ScreenRegionSelectionGeometry.TryMapPreviewPoint(
                point, ImageBounds(), _screenshot.Size, clamp, out mapped);

        private void Canvas_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !TryMapPoint(e.Location, clamp: false, out var point))
                return;
            _dragStart = point;
            _selection = Rectangle.Empty;
            _dragging = true;
            _useSelection.Enabled = false;
            AnnounceStatus(_isArabic
                ? "استمر بالسحب لتحديد المنطقة. المعاينة محلية ولن يحدث إرسال تلقائي."
                : "Keep dragging to select the region. Preview stays local; nothing is sent automatically.");
            _canvas.Invalidate();
        }

        private void Canvas_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_dragging || !TryMapPoint(e.Location, clamp: true, out var point))
                return;
            UpdateSelection(point, announce: false);
        }

        private void Canvas_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_dragging || e.Button != MouseButtons.Left)
                return;
            _dragging = false;
            if (TryMapPoint(e.Location, clamp: true, out var point))
                UpdateSelection(point);
        }

        private void UpdateSelection(Point end, bool announce = true)
        {
            _selection = ScreenRegionSelectionGeometry.CreateSelection(
                _dragStart, end, _screenshot.Size);
            _useSelection.Enabled = _selection.Width >= MinimumRegionPixels &&
                                    _selection.Height >= MinimumRegionPixels;
            if (announce)
                UpdateSelectionStatus();
            else
                _canvas.Invalidate();
        }

        private void Canvas_KeyDown(object? sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Space && !e.Control && !e.Alt && !e.Shift)
            {
                StartCenteredKeyboardSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && _useSelection.Enabled)
            {
                _useSelection.PerformClick();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down))
                return;

            if (_selection.Width < MinimumRegionPixels || _selection.Height < MinimumRegionPixels)
                StartCenteredKeyboardSelection(announce: false);

            if (e.Shift)
            {
                var deltaWidth = 0;
                var deltaHeight = 0;
                if (e.KeyCode == Keys.Left) deltaWidth = -10;
                if (e.KeyCode == Keys.Right) deltaWidth = 10;
                if (e.KeyCode == Keys.Up) deltaHeight = -10;
                if (e.KeyCode == Keys.Down) deltaHeight = 10;
                _selection = ScreenRegionSelectionGeometry.ResizeSelection(
                    _selection, _screenshot.Size, deltaWidth, deltaHeight);
            }
            else
            {
                var step = e.Control ? 1 : 10;
                var deltaX = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
                var deltaY = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                _selection = ScreenRegionSelectionGeometry.MoveSelection(
                    _selection, _screenshot.Size, deltaX, deltaY);
            }

            UpdateSelectionStatus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void StartCenteredKeyboardSelection(bool announce = true)
        {
            _selection = ScreenRegionSelectionGeometry.CreateCenteredSelection(_screenshot.Size);
            if (announce)
                UpdateSelectionStatus();
            else
            {
                _useSelection.Enabled = true;
                _canvas.Invalidate();
            }
        }

        private void UpdateSelectionStatus()
        {
            _useSelection.Enabled = _selection.Width >= MinimumRegionPixels &&
                                    _selection.Height >= MinimumRegionPixels;
            var coordinates = $"X={_selection.X}, Y={_selection.Y}, W={_selection.Width}, H={_selection.Height}";
            var message = _useSelection.Enabled
                ? (_isArabic
                    ? $"التحديد الحالي: الموضع ({_selection.X}, {_selection.Y}) والحجم {_selection.Width} × {_selection.Height} بكسل. الأسهم للتحريك، وShift مع الأسهم لتغيير الحجم، وCtrl مع الأسهم لتحريك بكسل واحد. Enter للاختيار وEscape للإلغاء. لن تُرسل الصورة حتى تضغط «إرسال» في ليال."
                    : $"Current crop: position ({_selection.X}, {_selection.Y}), size {_selection.Width} × {_selection.Height} pixels. Arrows move; Shift+arrows resize; Ctrl+arrows move one pixel. Enter selects; Escape cancels. Nothing is sent until you press Send in Layal.")
                : (_isArabic
                    ? $"الحد الأدنى للتحديد {MinimumRegionPixels} × {MinimumRegionPixels} بكسل."
                    : $"The selection must be at least {MinimumRegionPixels} × {MinimumRegionPixels} pixels.");
            _canvas.AccessibleDescription = (_isArabic
                ? "معاينة الشاشة. اسحب لتحديد المنطقة. Space لإنشاء تحديد في الوسط. الأسهم للتحريك، وShift مع الأسهم لتغيير الحجم، وCtrl مع الأسهم للتحريك الدقيق، Enter للاختيار، وEscape للإلغاء. "
                : "Screen preview. Drag to select. Space creates a centered selection. Arrow keys move; Shift+arrows resize; Ctrl+arrows move precisely; Enter chooses; Escape cancels. ") + coordinates;
            AnnounceStatus(message);
            _canvas.Invalidate();
        }

        private void AnnounceStatus(string message) => _status.Announce(message);

        private void Canvas_Paint(object? sender, PaintEventArgs e)
        {
            var imageBounds = ImageBounds();
            e.Graphics.Clear(_canvas.BackColor);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_screenshot, imageBounds);
            if (_canvas.Focused && _canvas.ClientRectangle.Width > 8 && _canvas.ClientRectangle.Height > 8)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(_canvas.ClientRectangle, -4, -4));

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

        private sealed class LiveRegionStatusLabel : Label
        {
            public void Announce(string message)
            {
                Text = message;
                AccessibleName = message;
                AccessibleDescription = message;
                // WinForms exposes NameChange (not a LiveRegionChanged enum member).
                // Updating the accessible name makes the current selection announcement observable.
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            }
        }

        private sealed class SelectionCanvas : Panel
        {
            public SelectionCanvas()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }
    }
}

/// <summary>
/// Pure crop-selection geometry shared by mouse input, keyboard input, and deterministic tests.
/// All coordinates and dimensions are in captured-image pixels, not Windows logical DIPs.
/// </summary>
public static class ScreenRegionSelectionGeometry
{
    public static RectangleF FitImageToCanvas(Size imageSize, Size canvasSize)
    {
        ValidateSize(imageSize, nameof(imageSize));
        ValidateSize(canvasSize, nameof(canvasSize));
        var scale = Math.Min((float)canvasSize.Width / imageSize.Width, (float)canvasSize.Height / imageSize.Height);
        var width = imageSize.Width * scale;
        var height = imageSize.Height * scale;
        return new RectangleF(
            (canvasSize.Width - width) / 2f,
            (canvasSize.Height - height) / 2f,
            width,
            height);
    }

    public static bool TryMapPreviewPoint(
        Point point,
        RectangleF imageBounds,
        Size imageSize,
        bool clamp,
        out Point mapped)
    {
        ValidateSize(imageSize, nameof(imageSize));
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0)
        {
            mapped = Point.Empty;
            return false;
        }
        if (!clamp && !imageBounds.Contains(point))
        {
            mapped = Point.Empty;
            return false;
        }

        var x = Math.Clamp((point.X - imageBounds.Left) / imageBounds.Width * imageSize.Width,
            0f, imageSize.Width - 1f);
        var y = Math.Clamp((point.Y - imageBounds.Top) / imageBounds.Height * imageSize.Height,
            0f, imageSize.Height - 1f);
        mapped = new Point((int)Math.Round(x), (int)Math.Round(y));
        return true;
    }

    public static Rectangle CreateSelection(Point start, Point end, Size imageSize)
    {
        ValidateSize(imageSize, nameof(imageSize));
        var left = Math.Clamp(Math.Min(start.X, end.X), 0, imageSize.Width - 1);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, imageSize.Height - 1);
        var right = Math.Clamp(Math.Max(start.X, end.X), 0, imageSize.Width - 1) + 1;
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, imageSize.Height - 1) + 1;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static Rectangle CreateCenteredSelection(Size imageSize)
    {
        ValidateSize(imageSize, nameof(imageSize));
        if (imageSize.Width < ScreenRegionCaptureService.MinimumRegionPixels ||
            imageSize.Height < ScreenRegionCaptureService.MinimumRegionPixels)
            throw new ArgumentOutOfRangeException(nameof(imageSize), "The captured image is too small for a selectable crop.");

        var width = Math.Max(ScreenRegionCaptureService.MinimumRegionPixels, imageSize.Width / 3);
        var height = Math.Max(ScreenRegionCaptureService.MinimumRegionPixels, imageSize.Height / 3);
        return new Rectangle(
            (imageSize.Width - width) / 2,
            (imageSize.Height - height) / 2,
            width,
            height);
    }

    public static Rectangle MoveSelection(Rectangle selection, Size imageSize, int deltaX, int deltaY)
    {
        ValidateSelection(selection, imageSize);
        var left = (int)Math.Clamp((long)selection.Left + deltaX, 0L, (long)imageSize.Width - selection.Width);
        var top = (int)Math.Clamp((long)selection.Top + deltaY, 0L, (long)imageSize.Height - selection.Height);
        return new Rectangle(left, top, selection.Width, selection.Height);
    }

    public static Rectangle ResizeSelection(
        Rectangle selection,
        Size imageSize,
        int deltaWidth,
        int deltaHeight)
    {
        ValidateSelection(selection, imageSize);
        var maxWidth = imageSize.Width - selection.Left;
        var maxHeight = imageSize.Height - selection.Top;
        var width = (int)Math.Clamp(
            (long)selection.Width + deltaWidth,
            ScreenRegionCaptureService.MinimumRegionPixels,
            maxWidth);
        var height = (int)Math.Clamp(
            (long)selection.Height + deltaHeight,
            ScreenRegionCaptureService.MinimumRegionPixels,
            maxHeight);
        return new Rectangle(selection.Left, selection.Top, width, height);
    }

    private static void ValidateSelection(Rectangle selection, Size imageSize)
    {
        ValidateSize(imageSize, nameof(imageSize));
        if (selection.Width < ScreenRegionCaptureService.MinimumRegionPixels ||
            selection.Height < ScreenRegionCaptureService.MinimumRegionPixels ||
            selection.Left < 0 || selection.Top < 0 ||
            (long)selection.Left + selection.Width > imageSize.Width ||
            (long)selection.Top + selection.Height > imageSize.Height)
            throw new ArgumentOutOfRangeException(nameof(selection), "The selection must be at least 16 by 16 pixels and remain inside the captured image.");
    }

    private static void ValidateSize(Size size, string parameterName)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Image and canvas dimensions must be positive.");
    }
}

public sealed record ScreenRegionCaptureResult(byte[] PngBytes, int Width, int Height);
