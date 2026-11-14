using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCrop;

public sealed class ImageSurface : FrameworkElement
{
    public BitmapSource? Bitmap { get; private set; }
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public bool IsCropping { get; private set; }
    public bool IsCropPreview { get; private set; }
    public bool CanUndoCrop => cropHistory.Count > 0;
    public bool Circle { get; set; }
    public bool ShowGrid { get; set; } = true;
    public double Ratio { get; set; }
    public bool FixedPixels { get; set; }
    public Rect Selection { get; private set; }
    public double Zoom { get; private set; } = 1;
    public double DisplayScale => FitScale * Zoom;
    private double FitScale => PixelWidth == 0 ? 1 : Math.Max(.001, Math.Min(Math.Max(1, ActualWidth - 48) / PixelWidth, Math.Max(1, ActualHeight - 48) / PixelHeight));
    private Vector pan;
    private Point start;
    private Point anchor;
    private Rect startRect;
    private Vector startPan;
    private double editZoom;
    private Vector editPan;
    private readonly List<Rect> cropHistory = [];
    private int drag = -2;
    private Rect ImageRect => new((ActualWidth - PixelWidth * DisplayScale) / 2 + pan.X, (ActualHeight - PixelHeight * DisplayScale) / 2 + pan.Y, PixelWidth * DisplayScale, PixelHeight * DisplayScale);
    private static readonly Brush Checker = MakeChecker();
    public event Action? SelectionChanged;
    public event Action? ZoomChanged;
    public event Action? CropStateChanged;

    public ImageSurface()
    {
        Focusable = true; ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => { if (IsCropPreview) FitPreview(); InvalidateVisual(); ZoomChanged?.Invoke(); };
    }
    private static Brush MakeChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(44, 47, 47)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var fill = new SolidColorBrush(Color.FromRgb(56, 59, 59));
        group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze(); return brush;
    }
    public void SetImage(BitmapSource? bitmap, int width = 0, int height = 0)
    {
        EndDrag();
        Bitmap = bitmap; PixelWidth = width; PixelHeight = height; IsCropping = false; IsCropPreview = false;
        cropHistory.Clear(); Selection = Rect.Empty; Fit(); CropStateChanged?.Invoke();
    }
    public void ReplaceBitmap(BitmapSource bitmap) { Bitmap = bitmap; InvalidateVisual(); }
    public void BeginCrop()
    {
        IsCropping = true; IsCropPreview = false; cropHistory.Clear();
        Fit(); Selection = CropMath.Fit(PixelWidth, PixelHeight, Ratio); Changed(); CropStateChanged?.Invoke();
    }
    public void EndCrop()
    {
        EndDrag(); IsCropping = false; IsCropPreview = false; cropHistory.Clear();
        InvalidateVisual(); CropStateChanged?.Invoke();
    }
    public void SetSelection(Rect selection)
    {
        ReturnToCrop();
        if (selection == Selection) return;
        RememberSelection(Selection); Selection = selection; Changed();
    }
    public void ResetSelection()
    {
        ReturnToCrop(); cropHistory.Clear(); Selection = CropMath.Fit(PixelWidth, PixelHeight, Ratio);
        Fit(); Changed(); CropStateChanged?.Invoke();
    }
    public void PreviewCrop()
    {
        if (!IsCropping || IsCropPreview || Selection.IsEmpty || Selection.Width < 1 || Selection.Height < 1) return;
        EndDrag(); editZoom = Zoom; editPan = pan; IsCropPreview = true;
        FitPreview(); InvalidateVisual(); ZoomChanged?.Invoke(); CropStateChanged?.Invoke();
    }
    public void ReturnToCrop()
    {
        if (!IsCropPreview) return;
        EndDrag(); IsCropPreview = false; Zoom = editZoom; pan = editPan;
        InvalidateVisual(); ZoomChanged?.Invoke(); CropStateChanged?.Invoke();
    }
    public void UndoCrop()
    {
        if (!IsCropping) return;
        ReturnToCrop(); EndDrag();
        if (!CanUndoCrop) return;
        Selection = cropHistory[^1]; cropHistory.RemoveAt(cropHistory.Count - 1);
        Changed(); CropStateChanged?.Invoke();
    }
    private void RememberSelection(Rect selection)
    {
        if (!IsCropping || selection.IsEmpty || selection.Width < 1 || selection.Height < 1) return;
        if (cropHistory.Count == 64) cropHistory.RemoveAt(0);
        cropHistory.Add(selection); CropStateChanged?.Invoke();
    }
    private void FitPreview()
    {
        double scale = Math.Min(Math.Max(1, ActualWidth - 48) / Selection.Width, Math.Max(1, ActualHeight - 48) / Selection.Height);
        Zoom = scale / FitScale;
        CenterOn(new Point(Selection.X + Selection.Width / 2, Selection.Y + Selection.Height / 2));
    }
    private void CenterOn(Point point) => pan = new Vector((PixelWidth / 2d - point.X) * DisplayScale, (PixelHeight / 2d - point.Y) * DisplayScale);
    public void Fit()
    {
        if (IsCropPreview) FitPreview(); else { Zoom = 1; pan = new Vector(); }
        InvalidateVisual(); ZoomChanged?.Invoke();
    }
    public void ActualPixels()
    {
        if (PixelWidth <= 0) return;
        Zoom = 1 / FitScale;
        CenterOn(IsCropPreview ? new Point(Selection.X + Selection.Width / 2, Selection.Y + Selection.Height / 2) : new Point(PixelWidth / 2d, PixelHeight / 2d));
        InvalidateVisual(); ZoomChanged?.Invoke();
    }
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2));
    internal void ZoomAt(double factor, Point pivot)
    {
        if (Bitmap is null || !double.IsFinite(factor) || factor <= 0 || !double.IsFinite(pivot.X) || !double.IsFinite(pivot.Y)) return;
        if (drag is >= 0 and <= 9) return;
        Point imagePoint = ToImage(pivot);
        Zoom = Math.Clamp(Zoom * factor, .05, Math.Max(Zoom, Math.Max(32, 8 / FitScale)));
        // Keep the image pixel under the pointer stationary; selection remains in source pixels.
        pan += pivot - new Point(ImageRect.X + imagePoint.X * DisplayScale, ImageRect.Y + imagePoint.Y * DisplayScale);
        InvalidateVisual(); ZoomChanged?.Invoke();
    }
    private void Changed() { InvalidateVisual(); SelectionChanged?.Invoke(); }
    private Point ToImage(Point p) => new((p.X - ImageRect.X) / DisplayScale, (p.Y - ImageRect.Y) / DisplayScale);
    private Rect ToScreen(Rect r) => new(ImageRect.X + r.X * DisplayScale, ImageRect.Y + r.Y * DisplayScale, r.Width * DisplayScale, r.Height * DisplayScale);
    private static Point[] Handles(Rect r) => [r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2), r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2)];

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 22, 22)), null, new Rect(RenderSize));
        if (Bitmap is null) return;
        Rect image = ImageRect;
        if (IsCropPreview)
        {
            Rect preview = ToScreen(Selection);
            dc.PushClip(Circle ? new EllipseGeometry(preview) : new RectangleGeometry(preview));
            dc.DrawRectangle(Checker, null, image); dc.DrawImage(Bitmap, image); dc.Pop(); return;
        }
        dc.DrawRectangle(Checker, null, image); dc.DrawImage(Bitmap, image);
        if (!IsCropping || Selection.IsEmpty) return;
        Rect selected = ToScreen(Selection);
        Geometry aperture = Circle ? new EllipseGeometry(selected) : new RectangleGeometry(selected);
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(image), aperture);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165, 0, 0, 0)), null, shade);
        var white = new Pen(Brushes.White, 1);
        if (ShowGrid)
        {
            dc.PushClip(aperture);
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
            for (int i = 1; i <= 2; i++)
            {
                double x = selected.Left + selected.Width * i / 3, y = selected.Top + selected.Height * i / 3;
                dc.DrawLine(gridPen, new Point(x, selected.Top), new Point(x, selected.Bottom));
                dc.DrawLine(gridPen, new Point(selected.Left, y), new Point(selected.Right, y));
            }
            dc.Pop();
        }
        dc.DrawGeometry(null, white, aperture);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1), selected);
        foreach (Point point in Handles(selected)) dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(point.X - 4, point.Y - 4, 8, 8));
        var pixels = CropMath.Pixels(Selection, PixelWidth, PixelHeight);
        var text = new FormattedText($"{pixels.Width} × {pixels.Height}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double labelY = selected.Top >= 30 ? selected.Top - 27 : selected.Top + 9;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 22, 26, 24)), null, new Rect(selected.Left, labelY, text.Width + 16, 23), 3, 3);
        dc.DrawText(text, new Point(selected.Left + 8, labelY + 3));
    }

    private int HitHandle(Point p)
    {
        if (!IsCropping || IsCropPreview || Selection.IsEmpty || FixedPixels) return -1;
        var points = Handles(ToScreen(Selection));
        for (int i = 0; i < points.Length; i++) if ((points[i] - p).Length <= 11) return i;
        return -1;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (Bitmap is null) return;
        if (e.ClickCount == 2 && !IsCropping) { Fit(); return; }
        start = e.GetPosition(this); startRect = Selection; startPan = pan;
        if (!IsCropping || IsCropPreview) drag = 10;
        else
        {
            var point = ToImage(start);
            if (!new Rect(0, 0, PixelWidth, PixelHeight).Contains(point)) return;
            int handle = HitHandle(start);
            if (handle >= 0) { drag = handle; anchor = Handles(Selection)[(handle + 4) % 8]; }
            else if (Selection.Contains(point) || FixedPixels) { drag = 8; }
            else { drag = 9; anchor = point; Selection = new Rect(point, point); }
        }
        if (!CaptureMouse()) drag = -2;
        e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || Bitmap is null || IsMouseCaptured) return;
        Focus(); start = e.GetPosition(this); startPan = pan; drag = 10;
        if (!CaptureMouse()) drag = -2;
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            int handle = HitHandle(point);
            Cursor = handle switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => IsCropPreview || !IsCropping ? Cursors.Hand : !Selection.IsEmpty && Selection.Contains(ToImage(point)) ? Cursors.SizeAll : Cursors.Cross };
            return;
        }
        if (drag == 10) { pan = startPan + (point - start); InvalidateVisual(); return; }
        Point imagePoint = ToImage(point);
        if (drag == 8) Selection = CropMath.Move(startRect, (point - start) / DisplayScale, PixelWidth, PixelHeight);
        else if (drag == 9 || drag % 2 == 0) Selection = CropMath.FromPoints(anchor, imagePoint, Ratio, PixelWidth, PixelHeight);
        else
        {
            double l = startRect.Left, t = startRect.Top, r = startRect.Right, b = startRect.Bottom;
            if (drag == 1) t = Math.Clamp(imagePoint.Y, 0, b - 1);
            if (drag == 5) b = Math.Clamp(imagePoint.Y, t + 1, PixelHeight);
            if (drag == 7) l = Math.Clamp(imagePoint.X, 0, r - 1);
            if (drag == 3) r = Math.Clamp(imagePoint.X, l + 1, PixelWidth);
            if (Ratio > 0)
            {
                if (drag is 1 or 5)
                {
                    double w = Math.Min((b - t) * Ratio, 2 * Math.Min(startRect.X + startRect.Width / 2, PixelWidth - startRect.X - startRect.Width / 2));
                    l = startRect.X + startRect.Width / 2 - w / 2; r = l + w;
                    if (drag == 1) t = b - w / Ratio; else b = t + w / Ratio;
                }
                else
                {
                    double h = Math.Min((r - l) / Ratio, 2 * Math.Min(startRect.Y + startRect.Height / 2, PixelHeight - startRect.Y - startRect.Height / 2));
                    t = startRect.Y + startRect.Height / 2 - h / 2; b = t + h;
                    if (drag == 7) l = r - h * Ratio; else r = l + h * Ratio;
                }
            }
            Selection = new Rect(new Point(l, t), new Point(r, b));
        }
        Changed();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        int completedDrag = drag; EndDrag();
        if (completedDrag is >= 0 and <= 9) CompleteCropDrag(startRect);
        e.Handled = true;
    }
    private void CompleteCropDrag(Rect previous)
    {
        if (!IsCropping || Selection == previous) return;
        if (Selection.Width < 1 || Selection.Height < 1) { Selection = previous; Changed(); return; }
        RememberSelection(previous); Changed(); PreviewCrop();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && IsMouseCaptured && drag == 10) { EndDrag(); e.Handled = true; }
    }
    private void EndDrag()
    {
        drag = -2;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (drag is >= 0 and <= 9) { Selection = startRect; Changed(); }
        drag = -2;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Bitmap is null) return;
        Point pivot = e.GetPosition(this);
        ZoomAt(Math.Pow(1.15, e.Delta / 120d), pivot);
        if (IsMouseCaptured && drag == 10) { start = pivot; startPan = pan; }
        e.Handled = true;
    }
}
