using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;
using QuickCrop;

internal static class DocumentationDemo
{
    public static int Run(string root)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        AppSettings.StorageDirectory = Path.Combine(root, "demo-settings");
        var app = new App(); app.InitializeComponent();
        string media = Path.Combine(Environment.CurrentDirectory, "docs", "images"); Directory.CreateDirectory(media);
        string photos = Path.Combine(root, "Photos"); Directory.CreateDirectory(photos);
        string sample = Path.Combine(Environment.CurrentDirectory, "samples", "01-lake.jpg");
        File.Copy(sample, Path.Combine(photos, "01-lake.jpg"));
        using (var portrait = new MagickImage(sample))
        {
            portrait.Crop(new MagickGeometry(500, 0, 600, 1067)); portrait.ResetPage(); portrait.Resize(900, 1600);
            portrait.Write(Path.Combine(photos, "02-portrait.jpg"));
        }
        using (var landscape = new MagickImage(sample)) { landscape.Flop(); landscape.Write(Path.Combine(photos, "03-landscape.jpg")); }
        var settings = new AppSettings { OutputDirectory = Path.Combine(root, "Cropped"), ArchiveDirectory = Path.Combine(root, "Originals"), OutputExtension = ".png" };
        var window = new MainWindow(); Field(window, "settings").SetValue(window, settings);
        var surface = (ImageSurface)window.FindName("Surface");
        var workflow = new List<string>(); var rotation = new List<string>();
        try
        {
            Wait((Task)Call(window, "OpenFolder", photos, null, true)!); Thumbnails();
            Capture("viewer"); workflow.Add("viewer"); rotation.Add("viewer");
            Wait((Task)Call(window, "TransformImage", ImageTransform.RotateRight)!); Capture("rotation-right"); rotation.Add("rotation-right");
            Wait((Task)Call(window, "SelectImage", 1)!); Capture("rotation-next"); rotation.Add("rotation-next");
            Wait((Task)Call(window, "SelectImage", 0)!); Capture("rotation-restored"); rotation.Add("rotation-restored");
            Wait((Task)Call(window, "TransformImage", ImageTransform.RotateLeft)!);
            Call(window, "BeginCrop", false); Capture("crop-max"); workflow.Add("crop-max");
            Call(window, "SelectCropTemplate", "ratio-1-1"); Capture("crop-square"); workflow.Add("crop-square");
            Call(window, "SelectCropTemplate", "ratio-9-16");
            surface.ZoomAt(1.6, new Point(surface.ActualWidth * .6, surface.ActualHeight * .4));
            Wait((Task)Field(window, "detailTask").GetValue(window)!); Capture("crop-zoom"); workflow.Add("crop-zoom"); surface.Fit();
            Rect previous = surface.Selection;
            typeof(ImageSurface).GetProperty(nameof(ImageSurface.Selection))!.SetValue(surface, CropMath.Fit(surface.PixelWidth, surface.PixelHeight, surface.Ratio, .72));
            Call(surface, "Changed"); Capture("crop-adjust"); workflow.Add("crop-adjust");
            Call(surface, "CompleteCropDrag", previous);
            Require(surface.IsCropPreview, "Mouse-release crop preview was not entered.");
            Capture("crop-preview"); workflow.Add("crop-preview");
            surface.ReturnToCrop(); Capture("crop-return"); workflow.Add("crop-return");
            surface.UndoCrop(); Capture("crop-undo"); workflow.Add("crop-undo");
            surface.SetSelection(CropMath.Fit(surface.PixelWidth, surface.PixelHeight, surface.Ratio, .72)); surface.PreviewCrop();
            Wait((Task)Call(window, "SaveCrop")!); Thumbnails(); Capture("saved-list"); workflow.Add("saved-list");
            var entries = ((ListBox)window.FindName("Filmstrip")).Items.Cast<ImageEntry>().ToArray();
            Require(entries.Length == 3 && entries[0].Path.EndsWith("01-lake.png"), "The output did not replace the source row.");
            var wallpaper = new CropTemplate { Name = "手机壁纸", Width = 1080, Height = 1920, Kind = CropTemplateKind.Pixels };
            settings.CropTemplates.Add(wallpaper); settings.PinnedCropTemplateIds[0] = wallpaper.Id;
            settings.CropTemplates.Add(new CropTemplate { Name = "方形封面", Width = 1080, Height = 1080, Kind = CropTemplateKind.Pixels });
            settings.CropTemplates.Add(new CropTemplate { Name = "电影画幅", Width = 2.39, Height = 1 });
            Call(window, "SelectCropTemplate", wallpaper.Id); Capture("template-selected");
            var fixedSize = (CheckBox)window.FindName("LockCropSizeCheck"); fixedSize.IsChecked = true;
            Call(window, "LockCropSizeClick", fixedSize, new RoutedEventArgs()); Capture("template-locked");
            Require(settings.LockedCrop?.OutputSize == new PixelSize(1080, 1920), "The exact output size was not locked.");
            Wait((Task)Call(window, "SaveCrop")!); Thumbnails(); Capture("template-next");
            using (var exact = ImageFiles.Load(entries[1].Path.Replace("Photos", "Cropped").Replace(".jpg", ".png")))
                Require(exact.Pixels.Width == 1080 && exact.Pixels.Height == 1920, "The demo pixel output has the wrong dimensions.");
            var manager = new CropTemplatesWindow(settings);
            UiChecks.Render((FrameworkElement)manager.Content, 755, 555, Path.Combine(media, "templates.png"));
            manager.Close();
            var displaySettings = new AppSettings { OutputDirectory = @"D:\Photos\Cropped", ArchiveDirectory = @"D:\Photos\Originals", OutputExtension = ".png" };
            var settingsWindow = new SettingsWindow(displaySettings, @"D:\Photos\01-lake.jpg");
            UiChecks.Render((FrameworkElement)settingsWindow.Content, 605, 710, Path.Combine(media, "settings.png")); settingsWindow.Close();
            Capture("compact", 860, 588);
            MakeGif(workflow, "crop-workflow.gif"); MakeGif(rotation, "rotation-memory.gif");
            MakeGif(["template-selected", "template-locked", "template-next"], "fixed-size-batch.gif");
            Console.WriteLine("Documentation images and three animated demonstrations: " + media);
            return 0;
        }
        finally
        {
            if (surface.IsCropping) Call(window, "CancelCrop");
            window.Close(); app.Shutdown();
        }
        void Thumbnails() => Wait(Task.WhenAll(((ListBox)window.FindName("Filmstrip")).Items.Cast<ImageEntry>().Select(entry => entry.EnsureThumbnailAsync())));
        void Capture(string name, int width = 1220, int height = 768)
        {
            // Replace temporary absolute paths with neutral example paths in documentation only.
            ((TextBlock)window.FindName("OutputPathLabel")).Text = @"D:\Photos\Cropped";
            ((TextBlock)window.FindName("ArchivePathLabel")).Text = @"D:\Photos\Originals";
            UiChecks.Render((FrameworkElement)window.Content, width, height, Path.Combine(media, name + ".png"));
        }
        void MakeGif(IEnumerable<string> frames, string name)
        {
            using var collection = new MagickImageCollection();
            foreach (string frame in frames)
            {
                var image = new MagickImage(Path.Combine(media, frame + ".png"));
                image.Resize(976, 614); image.AnimationDelay = 180; image.AnimationIterations = 0;
                collection.Add(image);
            }
            collection.Quantize(new QuantizeSettings { Colors = 192 }); collection.Optimize(); collection.Write(Path.Combine(media, name));
            using var verify = new MagickImageCollection(Path.Combine(media, name));
            Require(verify.Count == collection.Count && verify.Count >= 3 && verify.All(frame => frame.Width > 0 && frame.Height > 0), "Animated demo verification failed.");
        }
    }
    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
