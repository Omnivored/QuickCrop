using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ImageMagick;
using QuickCrop;

internal static class TemplateUiChecks
{
    public static void Run(string root, string sample)
    {
        string directory = Path.Combine(root, "template-ratio-batch"); CreateBatch(directory, sample);
        var settings = new AppSettings();
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        Field(window, "settings").SetValue(window, settings);
        window.Show(); Wait(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        Wait((Task)Call(window, "OpenFolder", directory, null, true)!);
        var surface = (ImageSurface)window.FindName("Surface");
        var lockCheck = (CheckBox)window.FindName("LockCropSizeCheck");
        try
        {
            Call(window, "BeginCrop", false);
            var grid = (UniformGrid)window.FindName("TemplateGrid");
            Assert(grid.Rows == 2 && grid.Columns == 3 && grid.Children.Count == 6 && grid.Children[5] is ComboBox, "Crop sizes are not a 3 by 2 grid with a final dropdown");
            Assert(string.Join(",", grid.Children.OfType<ToggleButton>().Select(b => b.Tag)) == "ratio-9-16,ratio-2-3,ratio-1-1,ratio-16-9,ratio-3-2", "The front five sizes are incorrect");
            foreach (string id in settings.PinnedCropTemplateIds)
            {
                Call(window, "SelectCropTemplate", id);
                Assert(Math.Abs(surface.Selection.Width - surface.PixelWidth) < .00001 || Math.Abs(surface.Selection.Height - surface.PixelHeight) < .00001, "Clicking a front size left an unnecessary crop margin");
            }
            Call(window, "SelectCropTemplate", "ratio-9-16");
            SnapshotCrop(settings, directory, 1220, 768, "preset-grid-default.png");
            Call(window, "SelectCropTemplate", "ratio-2-3");
            lockCheck.IsChecked = true; Call(window, "LockCropSizeClick", lockCheck, new RoutedEventArgs());
            Call(window, "SelectCropTemplate", "ratio-16-9");
            Assert(Math.Abs(surface.Ratio - 2d / 3) < .00001 && !grid.IsEnabled, "A fixed ratio changed after selecting another preset");
            Wait((Task)Call(window, "SaveCrop")!);
            Assert(surface.IsCropping && Math.Abs(surface.Ratio - 2d / 3) < .00001 && lockCheck.IsChecked == true, "The next image lost its fixed ratio");
            Wait((Task)Call(window, "SaveCrop")!);
            foreach (string name in new[] { "image1.jpg", "image2.jpg" })
            {
                using var saved = ImageFiles.Load(Path.Combine(directory, name));
                Assert(Math.Abs((double)saved.Pixels.Width / saved.Pixels.Height - 2d / 3) < .002, "A fixed-ratio output has the wrong aspect");
            }
            string pixels = Path.Combine(root, "template-pixel-batch"); CreateBatch(pixels, sample);
            Wait((Task)Call(window, "OpenFolder", pixels, null, true)!); Call(window, "BeginCrop", false);
            lockCheck.IsChecked = false; Call(window, "LockCropSizeClick", lockCheck, new RoutedEventArgs());
            string customId = "";
            var manage = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var dialog = Application.Current.Windows.OfType<CropTemplatesWindow>().Single();
                var templates = (ObservableCollection<CropTemplate>)Field(dialog, "templates").GetValue(dialog)!;
                var custom = new CropTemplate { Name = "竖图成品", Width = 300, Height = 500, Kind = CropTemplateKind.Pixels };
                customId = custom.Id; templates.Add(custom);
                var pins = (ComboBox[])Field(dialog, "pins").GetValue(dialog)!;
                string oldFirst = (string)pins[0].SelectedValue, oldSecond = (string)pins[1].SelectedValue;
                pins[0].SelectedValue = oldSecond;
                Assert((string)pins[1].SelectedValue == oldFirst, "Selecting an occupied front position did not swap positions");
                pins[0].SelectedValue = customId;
                string qa = Path.Combine(Environment.CurrentDirectory, "output", "qa");
                var snapshot = new CropTemplatesWindow(new AppSettings { CropTemplates = dialog.Templates, PinnedCropTemplateIds = dialog.PinnedIds });
                UiChecks.Render((FrameworkElement)snapshot.Content, 755, 555, Path.Combine(qa, "templates-manager.png"));
                snapshot.Close();
                Call(dialog, "SaveTemplates");
                if (dialog.DialogResult != true) dialog.Close();
            });
            Call(window, "ManageTemplatesClick", window, new RoutedEventArgs()); manage.Abort();
            Assert(settings.PinnedCropTemplateIds[0] == customId && settings.CropTemplates.Any(t => t.Id == customId && t.Name == "竖图成品"), "Named template or custom front position was not applied");
            ((ToggleButton)window.FindName("Preset1")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(Math.Abs(surface.Ratio - .6) < .00001, "The front pixel template has the wrong ratio");
            lockCheck.IsChecked = true; Call(window, "LockCropSizeClick", lockCheck, new RoutedEventArgs());
            Assert(settings.LockedCrop?.OutputSize == new PixelSize(300, 500), "Fixed pixel size was not captured");
            string screenshots = Path.Combine(Environment.CurrentDirectory, "output", "qa");
            SnapshotCrop(settings, pixels, 1220, 768, "template-crop-desktop.png");
            SnapshotCrop(settings, pixels, 860, 588, "template-crop-compact.png");
            Wait((Task)Call(window, "SaveCrop")!);
            Assert(surface.IsCropping && settings.LockedCrop?.OutputSize == new PixelSize(300, 500), "The next image lost the fixed pixel size");
            Wait((Task)Call(window, "SaveCrop")!);
            foreach (string name in new[] { "image1.jpg", "image2.jpg" })
            {
                using var saved = ImageFiles.Load(Path.Combine(pixels, name));
                Assert(saved.Pixels.Width == 300 && saved.Pixels.Height == 500, "Named pixel template output is not exact");
            }
            var loaded = AppSettings.Load();
            Assert(loaded.LockedCrop?.OutputSize == new PixelSize(300, 500) && loaded.PinnedCropTemplateIds[0] == customId, "Template or fixed-size preferences did not survive reload");
        }
        finally { if (surface.IsCropping) Call(window, "CancelCrop"); window.Close(); }
        Console.WriteLine("Crop templates: six-cell grid, named templates, custom pins, fixed-ratio batch and exact-pixel batch");
    }
    private static void SnapshotCrop(AppSettings settings, string directory, int width, int height, string name)
    {
        var snapshot = new MainWindow();
        Field(snapshot, "settings").SetValue(snapshot, settings);
        Wait((Task)Call(snapshot, "OpenFolder", directory, null, true)!); Call(snapshot, "BeginCrop", false);
        var surface = (ImageSurface)snapshot.FindName("Surface");
        Assert(surface.Bitmap is not null && surface.IsCropping, "Crop snapshot has no image or selection");
        UiChecks.Render((FrameworkElement)snapshot.Content, width, height, Path.Combine(Environment.CurrentDirectory, "output", "qa", name));
        Assert(((Border)snapshot.FindName("CropPanel")).ActualWidth == 258 && surface.ActualWidth > 0, "Crop snapshot layout is not sized correctly");
        Call(snapshot, "CancelCrop"); snapshot.Close();
    }
    private static void CreateBatch(string directory, string sample)
    {
        Directory.CreateDirectory(directory); File.Copy(sample, Path.Combine(directory, "image1.jpg"));
        using var portrait = new MagickImage(MagickColors.SeaGreen, 320, 600); portrait.Write(Path.Combine(directory, "image2.jpg"));
    }
    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
