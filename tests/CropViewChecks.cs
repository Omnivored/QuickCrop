using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickCrop;

internal static class CropViewChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Crop zoom keeps the pointer pixel and source selection stationary", () =>
        {
            var surface = Surface();
            var selection = surface.Selection;
            var pivot = new Point(210, 155);
            var pixel = ToImage(surface, pivot);
            surface.ZoomAt(2, pivot);
            Near(surface.Zoom, 2); SamePoint(ToImage(surface, pivot), pixel);
            Assert(surface.Selection == selection && surface.IsCropping);
            surface.ZoomAt(.5, pivot);
            Near(surface.Zoom, 1); SamePoint(ToImage(surface, pivot), pixel);
        });
        check("Crop zoom handles extreme and invalid factors without corrupting coordinates", () =>
        {
            var surface = Surface(); var selection = surface.Selection;
            surface.ZoomBy(double.NaN); surface.ZoomBy(0); surface.ZoomBy(double.PositiveInfinity);
            Near(surface.Zoom, 1);
            surface.ZoomBy(double.MaxValue); Assert(double.IsFinite(surface.Zoom) && surface.Zoom <= 32);
            surface.ZoomBy(double.Epsilon); Near(surface.Zoom, .05);
            Assert(surface.Selection == selection);
        });
        check("Crop preview fills the viewport with the selection and preserves original pixels", () =>
        {
            var surface = Surface();
            surface.SetSelection(new Rect(120, 80, 160, 120));
            var selection = surface.Selection;
            surface.PreviewCrop();
            Assert(surface.IsCropPreview && surface.IsCropping && surface.Selection == selection);
            var screen = (Rect)Call(surface, "ToScreen", selection)!;
            Near(screen.Width, 576); Near(screen.Height, 432);
            SamePoint(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2), new Point(320, 240));
            var previewPixel = ToImage(surface, new Point(300, 200));
            surface.ZoomAt(1.5, new Point(300, 200));
            SamePoint(ToImage(surface, new Point(300, 200)), previewPixel);
            surface.Fit(); Near(surface.DisplayScale, 3.6);
        });
        check("Returning from preview restores the edit zoom and pan without losing the selection", () =>
        {
            var surface = Surface(); surface.ZoomAt(1.6, new Point(200, 150));
            var zoom = surface.Zoom; var pixel = ToImage(surface, new Point(100, 100));
            var selection = surface.Selection;
            surface.PreviewCrop(); surface.ZoomBy(2); surface.ReturnToCrop();
            Assert(!surface.IsCropPreview && surface.IsCropping && surface.Selection == selection);
            Near(surface.Zoom, zoom); SamePoint(ToImage(surface, new Point(100, 100)), pixel);
        });
        check("Completed crop gestures automatically preview and can undo the last adjustment", () =>
        {
            var surface = Surface(); var before = surface.Selection;
            var after = new Rect(100, 80, 200, 150);
            typeof(ImageSurface).GetProperty(nameof(ImageSurface.Selection))!.SetValue(surface, after);
            Call(surface, "CompleteCropDrag", before);
            Assert(surface.IsCropPreview && surface.CanUndoCrop && surface.Selection == after);
            surface.UndoCrop();
            Assert(!surface.IsCropPreview && surface.IsCropping && !surface.CanUndoCrop && surface.Selection == before);
            Call(surface, "CompleteCropDrag", before);
            Assert(!surface.IsCropPreview && !surface.CanUndoCrop);
        });
        check("Incomplete crop drags restore the previous selection without entering preview", () =>
        {
            var surface = Surface(); var before = surface.Selection;
            typeof(ImageSurface).GetProperty(nameof(ImageSurface.Selection))!.SetValue(surface, new Rect(50, 60, 0, 0));
            Call(surface, "CompleteCropDrag", before);
            Assert(surface.Selection == before && !surface.IsCropPreview && !surface.CanUndoCrop);
        });
        check("New crop modes and images cannot undo stale selections", () =>
        {
            var surface = Surface(); surface.SetSelection(new Rect(50, 40, 100, 75));
            surface.PreviewCrop(); surface.Ratio = 1; surface.ResetSelection();
            Assert(!surface.CanUndoCrop && !surface.IsCropPreview); Near(surface.Selection.Width / surface.Selection.Height, 1);
            surface.SetSelection(new Rect(60, 40, 100, 100)); surface.PreviewCrop(); surface.SetImage(surface.Bitmap, 400, 300);
            Assert(!surface.IsCropping && !surface.IsCropPreview && !surface.CanUndoCrop && surface.Selection.IsEmpty);
        });
        check("Circle and fixed-pixel crops support preview and actual-pixel zoom", () =>
        {
            var surface = Surface(); surface.Ratio = 1; surface.Circle = true; surface.FixedPixels = true; surface.ResetSelection();
            var selection = surface.Selection; surface.PreviewCrop(); surface.ActualPixels();
            Near(surface.DisplayScale, 1); Assert(surface.Selection == selection && surface.IsCropPreview);
            surface.ReturnToCrop(); surface.ZoomBy(1.25);
            Assert(surface.Selection == selection && surface.Circle && surface.FixedPixels);
        });
        check("Crop undo history is bounded and survives preview toggles", () =>
        {
            var surface = Surface();
            for (int i = 0; i < 80; i++) surface.SetSelection(new Rect(i + 1, 40, 100, 75));
            surface.PreviewCrop(); surface.ReturnToCrop();
            int undo = 0;
            while (surface.CanUndoCrop) { surface.UndoCrop(); undo++; }
            Assert(undo == 64);
        });
    }

    public static void MouseChecks()
    {
        var surface = Surface();
        var host = new Window { Content = surface, Width = 900, Height = 640, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        try
        {
            var selection = surface.Selection;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent };
            surface.RaiseEvent(wheel);
            Assert(wheel.Handled && surface.Zoom > 1 && surface.Selection == selection);
            var zoom = surface.Zoom;
            var still = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 0) { RoutedEvent = Mouse.MouseWheelEvent };
            surface.RaiseEvent(still); Near(surface.Zoom, zoom);
            var after = new Rect(90, 70, 200, 150);
            Assert(surface.CaptureMouse());
            Field(surface, "startRect").SetValue(surface, selection);
            typeof(ImageSurface).GetProperty(nameof(ImageSurface.Selection))!.SetValue(surface, after);
            Field(surface, "drag").SetValue(surface, 8);
            Call(surface, "OnMouseLeftButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
            Assert(surface.IsCropPreview && surface.CanUndoCrop && !surface.IsMouseCaptured);
            var qa = System.IO.Path.Combine(Environment.CurrentDirectory, "output", "qa");
            UiChecks.Render(surface, 900, 600, System.IO.Path.Combine(qa, "crop-preview-surface.png"));
            surface.ReturnToCrop(); surface.UndoCrop(); Assert(surface.Selection == selection);
            Call(surface, "OnMouseDown", new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) { RoutedEvent = Mouse.MouseDownEvent });
            Assert(surface.IsMouseCaptured && surface.Selection == selection);
            Call(surface, "OnMouseUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) { RoutedEvent = Mouse.MouseUpEvent });
            Assert(!surface.IsMouseCaptured && !surface.IsCropPreview && surface.Selection == selection);
        }
        finally { host.Close(); }
    }

    private static ImageSurface Surface()
    {
        var bitmap = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 1600); bitmap.Freeze();
        var surface = new ImageSurface { Ratio = 4d / 3 };
        surface.Measure(new Size(640, 480)); surface.Arrange(new Rect(0, 0, 640, 480));
        surface.SetImage(bitmap, 400, 300); surface.BeginCrop(); return surface;
    }
    private static Point ToImage(ImageSurface surface, Point point) => (Point)Call(surface, "ToImage", point)!;
    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Near(double a, double b) => Assert(Math.Abs(a - b) < .00001);
    private static void SamePoint(Point a, Point b) { Near(a.X, b.X); Near(a.Y, b.Y); }
    private static void Assert(bool value) { if (!value) throw new Exception("Unexpected crop view state"); }
}
