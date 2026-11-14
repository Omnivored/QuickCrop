using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickCrop;

internal static class BatchListChecks
{
    public static void Run(string root, string sample)
    {
        string directory = Path.Combine(root, "replacement-sources"), output = Path.Combine(root, "replacement-outputs"), archive = Path.Combine(root, "replacement-originals");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "image1.jpg", "image2.jpg", "image3.jpg" }) File.Copy(sample, Path.Combine(directory, name));
        var settings = new AppSettings { OutputDirectory = output, ArchiveDirectory = archive, OutputExtension = ".png" };
        var window = new MainWindow();
        Field(window, "settings").SetValue(window, settings);
        var surface = (ImageSurface)window.FindName("Surface");
        var list = (ListBox)window.FindName("Filmstrip");
        try
        {
            Wait((Task)Call(window, "OpenFolder", directory, null, true)!);
            Wait((Task)Call(window, "SelectImage", 1)!); Call(window, "BeginCrop", false);
            surface.SetSelection(new Rect(120, 160, 450, 800)); surface.PreviewCrop();
            Wait((Task)Call(window, "SaveCrop")!);
            Assert(list.Items.Count == 3 && Entry(1).Path == Path.Combine(output, "image2.png"), "The middle output did not replace its source at the same slot");
            Assert((int)Field(window, "index").GetValue(window)! == 2 && surface.IsCropping, "The batch did not continue to the next unfinished source");
            Wait(Entry(1).EnsureThumbnailAsync());
            Assert(Entry(1).Thumbnail is { } thumb && thumb.PixelHeight > thumb.PixelWidth, "The saved portrait thumbnail still shows the landscape original");
            Wait(Task.WhenAll(list.Items.Cast<ImageEntry>().Select(entry => entry.EnsureThumbnailAsync())));
            UiChecks.Render((FrameworkElement)window.Content, 1220, 768, Path.Combine(Environment.CurrentDirectory, "output", "qa", "batch-output-replacement.png"));
            Assert(!File.Exists(Path.Combine(directory, "image2.jpg")) && File.Exists(Path.Combine(archive, "image2.jpg")), "The original was not archived");
            Wait((Task)Call(window, "SaveCrop")!);
            Assert((int)Field(window, "index").GetValue(window)! == 0, "The batch did not wrap to the remaining unfinished source");
            Assert(Entry(1).Path.EndsWith("image2.png") && Entry(2).Path.EndsWith("image3.png"), "Completed rows disappeared while advancing");
            Wait((Task)Call(window, "SaveCrop")!);
            Assert(!surface.IsCropping && list.Items.Count == 3 && (int)Field(window, "index").GetValue(window)! == 0, "Finishing the batch did not keep the last output visible");
            string[] expected = Enumerable.Range(1, 3).Select(n => Path.Combine(output, $"image{n}.png")).ToArray();
            Assert(list.Items.Cast<ImageEntry>().Select(e => e.Path).SequenceEqual(expected), "The output order differs from the original order");
            Wait((Task)Call(window, "OpenFolder", directory, expected[0], false)!);
            Assert(list.Items.Cast<ImageEntry>().Select(e => e.Path).SequenceEqual(expected), "Refreshing lost outputs saved outside the source folder");
            Wait((Task)Call(window, "SelectImage", 1)!);
            Assert(((ImagePreview)Field(window, "document").GetValue(window)!).Path == expected[1] && surface.PixelWidth == 450 && surface.PixelHeight == 800, "Revisiting the saved row did not show the crop output");
            Call(window, "BeginCrop", false); Wait((Task)Call(window, "SaveCrop")!);
            Wait((Task)Call(window, "OpenFolder", directory, null, false)!);
            Assert(list.Items.Count == 3, "Recropping a completed row created a duplicate after refresh");
            Console.WriteLine("Batch list: original slots retained, output thumbnails refreshed, unfinished sources advanced, external outputs retained after refresh");
        }
        finally { if (surface.IsCropping) Call(window, "CancelCrop"); window.Close(); }
        ImageEntry Entry(int i) => (ImageEntry)list.Items[i];
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
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
