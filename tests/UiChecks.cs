using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickCrop;

internal static class UiChecks
{
    public static void Run(string testRoot, bool recycle = false)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        AppSettings.StorageDirectory = System.IO.Path.Combine(testRoot, "settings");
        var app = new App(); app.InitializeComponent();
        using var keyboardSource = new HwndSource(new HwndSourceParameters("QuickCrop keyboard checks") { Width = 1, Height = 1, WindowStyle = 0 });
        string directory = System.IO.Path.Combine(testRoot, "ui-batch"); Directory.CreateDirectory(directory);
        string sample = System.IO.Path.Combine(Environment.CurrentDirectory, "samples", "01-lake.jpg");
        File.Copy(sample, System.IO.Path.Combine(directory, "image1.jpg")); File.Copy(sample, System.IO.Path.Combine(directory, "image2.jpg"));
        var settings = new AppSettings { ArchiveDirectory = System.IO.Path.Combine(directory, "archive") };
        var window = new MainWindow();
        Console.WriteLine("UI phase: initial preview");
        void Press(Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Wait((Task)Call(window, "HandleKeyDown", e, modifiers)!);
            Assert(e.Handled, $"Shortcut was not handled: {modifiers}+{key}");
        }
        Field(window, "settings").SetValue(window, settings);
        Wait((Task)Call(window, "OpenFolder", directory, null, true)!);
        var surface = (ImageSurface)window.FindName("Surface");
        Assert(surface.Bitmap is not null, "Main image is blank");
        string qa = System.IO.Path.Combine(Environment.CurrentDirectory, "output", "qa"); Directory.CreateDirectory(qa);
        Render((FrameworkElement)window.Content, 1220, 768, System.IO.Path.Combine(qa, "viewer-desktop.png"));
        Wait(Task.WhenAll(((ListBox)window.FindName("Filmstrip")).Items.Cast<ImageEntry>().Select(e => e.EnsureThumbnailAsync())));
        Render((FrameworkElement)window.Content, 1220, 768, System.IO.Path.Combine(qa, "viewer-desktop.png"));
        Assert(((ListBox)window.FindName("Filmstrip")).Items.Cast<ImageEntry>().All(e => e.Thumbnail is not null), "Thumbnails failed to decode");
        var firstPreview = (ImagePreview)Field(window, "document").GetValue(window)!;
        Wait((Task)Field(window, "prefetchTask").GetValue(window)!);
        Wait((Task)Call(window, "SelectImage", 1)!); Wait((Task)Call(window, "SelectImage", 0)!);
        Assert(ReferenceEquals(firstPreview, Field(window, "document").GetValue(window)), "Returning to a nearby image should reuse its preview");
        surface.ActualPixels(); Wait((Task)Field(window, "detailTask").GetValue(window)!);
        Assert(surface.Bitmap!.PixelWidth == firstPreview.Width, "Actual-pixel zoom did not restore full detail");
        surface.Fit();
        ((PreviewLoader)Field(window, "previews").GetValue(window)!).Clear();
        var skipped = (Task)Call(window, "SelectImage", 0)!;
        var latest = (Task)Call(window, "SelectImage", 1)!;
        Wait(Task.WhenAll(skipped, latest));
        Assert(((ImagePreview)Field(window, "document").GetValue(window)!).Path.EndsWith("image2.jpg"), "A superseded request replaced the latest selection");
        Wait((Task)Call(window, "SelectImage", 0)!);
        byte[] originalBytes = File.ReadAllBytes(firstPreview.Path);
        Press(Key.Right, ModifierKeys.Control);
        Console.WriteLine("UI phase: per-image rotation navigation");
        Assert((int)Field(window, "index").GetValue(window)! == 0, "Rotation navigated to a different image");
        Assert(surface.PixelWidth == firstPreview.Height && surface.PixelHeight == firstPreview.Width, "Viewer rotation did not swap image dimensions");
        AssertBitmap(surface.Bitmap!, new TransformedBitmap(firstPreview.Bitmap, new RotateTransform(90)), "Ctrl+Right did not rotate clockwise");
        Press(Key.Right);
        Assert(surface.PixelWidth == firstPreview.Width, "Rotation from the first image leaked to the next image");
        Press(Key.Left);
        AssertBitmap(surface.Bitmap!, new TransformedBitmap(firstPreview.Bitmap, new RotateTransform(90)), "Navigating back lost the remembered rotation");
        Press(Key.Left, ModifierKeys.Control);
        Press(Key.Right);
        Press(Key.Left, ModifierKeys.Control);
        Press(Key.Left);
        Assert(surface.PixelWidth == firstPreview.Width, "Per-image rotations were mixed together");
        Press(Key.Right);
        Assert(surface.PixelWidth == firstPreview.Height, "The second image lost its own rotation");
        Press(Key.Right, ModifierKeys.Control);
        Press(Key.Left);
        Press(Key.Right, ModifierKeys.Control);
        Wait((Task)Call(window, "OpenFolder", directory, firstPreview.Path, false)!);
        AssertBitmap(surface.Bitmap!, new TransformedBitmap(firstPreview.Bitmap, new RotateTransform(90)), "Refresh lost the remembered rotation");
        var reopened = new MainWindow();
        Wait((Task)Call(reopened, "OpenFolder", directory, firstPreview.Path, true)!);
        Assert(((ImageSurface)reopened.FindName("Surface")).PixelWidth == firstPreview.Height, "A new viewer window did not restore saved rotation");
        reopened.Close();
        Console.WriteLine("UI phase: rotation persistence restored");
        surface.ActualPixels(); Wait((Task)Field(window, "detailTask").GetValue(window)!);
        Assert(surface.Bitmap!.PixelWidth == firstPreview.Height && surface.Bitmap.PixelHeight == firstPreview.Width, "High-detail reload lost viewer rotation");
        surface.Fit();
        Call(window, "BeginCrop", false);
        Console.WriteLine("UI phase: crop transforms");
        Assert(surface.PixelWidth == firstPreview.Height, "Entering crop lost viewer rotation");
        Press(Key.Left, ModifierKeys.Control);
        Assert(surface.PixelWidth == firstPreview.Width && surface.PixelHeight == firstPreview.Height, "Ctrl+Left did not rotate within crop mode");
        Assert((int)Field(window, "index").GetValue(window)! == 0, "Crop rotation navigated to a different image");
        Call(window, "CancelCrop");
        Assert(surface.PixelWidth == firstPreview.Height && !surface.IsCropping, "Cancel crop did not restore the previous viewer orientation");
        Press(Key.Left, ModifierKeys.Control);
        AssertBitmap(surface.Bitmap!, firstPreview.Bitmap, "Opposite rotations did not restore the original preview");
        for (int i = 0; i < 4; i++) Press(Key.Right, ModifierKeys.Control);
        AssertBitmap(surface.Bitmap!, firstPreview.Bitmap, "Four clockwise rotations did not restore the original preview");
        Assert(File.ReadAllBytes(firstPreview.Path).SequenceEqual(originalBytes), "Viewer rotation or cancel modified the source file");
        Press(Key.Right);
        Assert((int)Field(window, "index").GetValue(window)! == 1, "Plain Right should still navigate");
        Press(Key.Left);
        Assert((int)Field(window, "index").GetValue(window)! == 0, "Plain Left should still navigate");
        Call(window, "BeginCrop", false);
        Press(Key.Right, ModifierKeys.Control);
        Press(Key.Left, ModifierKeys.Control);
        var mode = (ComboBox)window.FindName("ModeBox"); Call(window, "SelectCropTemplate", "ratio-16-9");
        Assert(surface.IsCropping && Math.Abs(surface.Selection.Width / surface.Selection.Height - 16d / 9) < .001, "Crop mode not applied");
        var heightInput = (TextBox)window.FindName("HeightInput"); heightInput.Text = "450";
        Assert((bool)Call(window, "ApplyPixelInputs", heightInput)!, "Typed height was rejected");
        Assert(surface.Selection.Height == 450 && surface.Selection.Width == 800, "Typed height did not update constrained width");
        var widthInput = (TextBox)window.FindName("WidthInput"); widthInput.Text = "invalid";
        Assert(!(bool)Call(window, "ApplyPixelInputs", widthInput)!, "Invalid input should block saving");
        surface.ResetSelection();
        var unzoomedSelection = surface.Selection;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent };
        surface.RaiseEvent(wheel);
        Assert(wheel.Handled && surface.Zoom > 1 && surface.Selection == unzoomedSelection, "Crop wheel zoom changed source coordinates or did not zoom");
        Wait((Task)Field(window, "detailTask").GetValue(window)!);
        double editZoom = surface.Zoom;
        Call(window, "CropPreviewClick", window, new RoutedEventArgs());
        Assert(surface.IsCropPreview, "Preview button did not enter crop preview");
        Render((FrameworkElement)window.Content, 1220, 768, System.IO.Path.Combine(qa, "crop-preview-desktop.png"));
        Render((FrameworkElement)window.Content, 860, 588, System.IO.Path.Combine(qa, "crop-preview-compact.png"));
        Press(Key.Escape);
        Assert(surface.IsCropping && !surface.IsCropPreview && surface.Selection == unzoomedSelection && Math.Abs(surface.Zoom - editZoom) < .00001, "Escape did not return to the previous crop view");
        surface.SetSelection(new Rect(200, 180, 800, 450));
        surface.PreviewCrop(); Press(Key.Z, ModifierKeys.Control);
        Assert(surface.Selection == unzoomedSelection && surface.IsCropping && !surface.IsCropPreview, "Ctrl+Z did not undo the last crop adjustment");
        surface.Fit();
        Render((FrameworkElement)window.Content, 1220, 768, System.IO.Path.Combine(qa, "crop-desktop.png"));
        Render((FrameworkElement)window.Content, 860, 588, System.IO.Path.Combine(qa, "crop-compact.png"));
        Render((FrameworkElement)window.Content, 1600, 950, System.IO.Path.Combine(qa, "crop-large.png"));
        var keyboardSettings = new SettingsWindow(settings, System.IO.Path.Combine(directory, "image1.jpg"));
        Render((FrameworkElement)keyboardSettings.Content, 605, 710, System.IO.Path.Combine(qa, "settings-desktop.png"));
        Render((FrameworkElement)keyboardSettings.Content, 465, 520, System.IO.Path.Combine(qa, "settings-compact.png"));
        keyboardSettings.Close();
        Console.WriteLine("UI phase: settings rendered; saving batch");
        surface.PreviewCrop();
        Wait((Task)Call(window, "SaveCrop")!);
        var nextDocument = (ImagePreview?)Field(window, "document").GetValue(window);
        Assert(nextDocument?.Path.EndsWith("image2.jpg") == true, "Save did not navigate to next source");
        Assert(surface.IsCropping, "Next image should remain in crop mode");
        Assert(settings.SelectedCropTemplateId == "ratio-16-9", "Crop template should persist across saves");
        Assert(File.Exists(System.IO.Path.Combine(directory, "archive", "image1.jpg")), "First original was not archived");
        using (var output = ImageFiles.Load(System.IO.Path.Combine(directory, "image1.jpg"))) Assert(output.Pixels.Width == 1600 && output.Pixels.Height == 900, "Crop output did not use the maximum original-resolution crop");
        Wait((Task)Call(window, "SaveCrop")!);
        Assert(((TextBlock)window.FindName("CounterLabel")).Text == "2 / 2" && !surface.IsCropping, "Batch did not finish on the last output");
        Assert(File.Exists(System.IO.Path.Combine(directory, "archive", "image2.jpg")), "Second original was not archived");
        Wait((Task)Call(window, "OpenFolder", directory, System.IO.Path.Combine(directory, "image2.jpg"), false)!);
        Assert(((TextBlock)window.FindName("CounterLabel")).Text == "2 / 2" && !surface.IsCropping, "Refresh did not retain completed crops for viewing");
        string rotationDirectory = System.IO.Path.Combine(testRoot, "ui-rotation-save"); Directory.CreateDirectory(rotationDirectory);
        string rotatedPath = System.IO.Path.Combine(rotationDirectory, "photo.jpg"); File.Copy(sample, rotatedPath);
        settings.ArchiveDirectory = System.IO.Path.Combine(rotationDirectory, "archive");
        Wait((Task)Call(window, "OpenFolder", rotationDirectory, null, true)!);
        Press(Key.Right, ModifierKeys.Control);
        Call(window, "BeginCrop", false);
        mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(item => item.Content?.ToString() == "自由裁剪");
        surface.SetSelection(new Rect(0, 0, surface.PixelWidth, surface.PixelHeight));
        Press(Key.S, ModifierKeys.Control);
        using (var rotated = ImageFiles.Load(rotatedPath)) Assert(rotated.Pixels.Width == firstPreview.Height && rotated.Pixels.Height == firstPreview.Width, "Saving did not apply viewer rotation at original resolution");
        Assert(File.ReadAllBytes(System.IO.Path.Combine(rotationDirectory, "archive", "photo.jpg")).SequenceEqual(originalBytes), "Rotation save did not archive the untouched original");
        Wait((Task)Call(window, "OpenFolder", rotationDirectory, rotatedPath, true)!);
        Assert((int)Field(window, "viewQuarterTurns").GetValue(window)! == 0 && surface.PixelWidth == firstPreview.Height, "Exported rotation was incorrectly applied twice");
        if (recycle)
        {
            string recycleDirectory = System.IO.Path.Combine(testRoot, "ui-recycle"); Directory.CreateDirectory(recycleDirectory);
            string first = System.IO.Path.Combine(recycleDirectory, "image1.jpg"), second = System.IO.Path.Combine(recycleDirectory, "image2.jpg");
            File.Copy(sample, first); File.Copy(sample, second);
            // Exercise a pre-upgrade settings file that previously enabled deletion confirmations.
            Directory.CreateDirectory(AppSettings.StorageDirectory);
            File.WriteAllText(System.IO.Path.Combine(AppSettings.StorageDirectory, "settings.json"), "{\"ConfirmDelete\":true}");
            Field(window, "settings").SetValue(window, AppSettings.Load());
            Wait((Task)Call(window, "OpenFolder", recycleDirectory, null, true)!);
            Press(Key.Delete);
            Assert(!File.Exists(first) && File.Exists(second), "Delete did not recycle only the selected test image");
            Assert(((ImagePreview?)Field(window, "document").GetValue(window))?.Path == second, "Delete did not display the next image");
            Press(Key.Delete);
            Assert(!File.Exists(second) && ((TextBlock)window.FindName("CounterLabel")).Text == "0 / 0", "Deleting the last test image did not clear the viewer");
            Console.WriteLine("UI recycle shortcut: generated test images removed without confirmation");
        }
        CropEntryChecks.Run(testRoot, sample);
        TemplateUiChecks.Run(testRoot, sample);
        BatchListChecks.Run(testRoot, sample);
        CropViewChecks.MouseChecks();
        window.Close(); app.Shutdown();
        Console.WriteLine("UI snapshots: " + qa);
    }
    private static void AssertBitmap(BitmapSource actual, BitmapSource expected, string message)
    {
        var a = new FormatConvertedBitmap(actual, PixelFormats.Bgra32, null, 0);
        var b = new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0);
        Assert(a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight, message);
        int stride = a.PixelWidth * 4;
        byte[] aPixels = new byte[stride * a.PixelHeight], bPixels = new byte[stride * b.PixelHeight];
        a.CopyPixels(aPixels, stride, 0); b.CopyPixels(bPixels, stride, 0);
        Assert(aPixels.SequenceEqual(bPixels), message);
    }
    internal static void Render(FrameworkElement root, int width, int height, string path)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 27, 27)), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        byte[] pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Assert(pixels.Where((_, i) => i % 4 == 3).Any(a => a != 0), "Snapshot is blank");
    }
    private static object? Call(object instance, string method, params object?[] arguments) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
