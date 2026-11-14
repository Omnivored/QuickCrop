using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickCrop;

internal static class CropEntryChecks
{
    public static void Run(string testRoot, string sample)
    {
        string directory = Path.Combine(testRoot, "crop-entry"); Directory.CreateDirectory(directory);
        File.Copy(sample, Path.Combine(directory, "photo.jpg"));
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var settings = new AppSettings();
        Field(window, "settings").SetValue(window, settings);
        window.Show();
        Wait(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        Wait((Task)Call(window, "OpenFolder", directory, null, true)!);
        var surface = (ImageSurface)window.FindName("Surface");
        try
        {
            Assert(((Button)window.FindName("CropButton")).IsEnabled, "Crop button should be enabled after loading");
            EnterWithoutDialog(() => ((Button)window.FindName("CropButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window);
            Assert(surface.IsCropping, "Crop button should enter cropping immediately");
            Assert(((TextBlock)window.FindName("OutputPathLabel")).Text == directory, "Default output folder should be the source folder");
            Assert(((TextBlock)window.FindName("ArchivePathLabel")).Text == Path.Combine(directory, "原图归档"), "Default archive folder should follow the source folder");
            Call(window, "CancelCrop");
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            EnterWithoutDialog(() => Wait((Task)Call(window, "HandleKeyDown", e, ModifierKeys.Control)!), window);
            Assert(e.Handled && surface.IsCropping, "Configured crop shortcut failed to enter cropping");
            Call(window, "CancelCrop");
            e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, null);
            Wait((Task)Call(window, "HandleKeyDown", e, ModifierKeys.Control)!);
            Assert(e.Handled && surface.IsCropping, "IME-processed crop shortcut failed to enter cropping");
            Call(window, "CancelCrop");
            settings.CropShortcut = "Ctrl+Shift+X";
            e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.X) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Wait((Task)Call(window, "HandleKeyDown", e, ModifierKeys.Control | ModifierKeys.Shift)!);
            Assert(e.Handled && surface.IsCropping, "Custom crop shortcut failed to enter cropping");
            Call(window, "CancelCrop");
            e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F2) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            EnterWithoutDialog(() => Wait((Task)Call(window, "HandleKeyDown", e, ModifierKeys.None)!), window);
            Assert(e.Handled && surface.IsCropping, "F2 fallback failed after changing the configurable shortcut");
            Call(window, "CancelCrop");
            settings.OutputDirectory = "invalid-relative-path";
            settings.ArchiveDirectory = directory;
            EnterWithoutDialog(() => ((Button)window.FindName("CropButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window);
            Assert(surface.IsCropping, "Even stale directory settings must not intercept entry into cropping");
            Call(window, "CancelCrop");
            string output = Path.Combine(directory, "export"), archive = Path.Combine(directory, "archive");
            settings.OutputDirectory = ""; settings.ArchiveDirectory = "";
            EditDirectories(window, output, archive, true);
            Assert(settings.OutputDirectory == output && settings.ArchiveDirectory == archive, "Settings did not update workflow directories");
            var saved = AppSettings.Load();
            Assert(saved.OutputDirectory == output && saved.ArchiveDirectory == archive, "Workflow directory settings were not persisted");
            EnterWithoutDialog(() => ((Button)window.FindName("CropButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window);
            Assert(surface.IsCropping && ((TextBlock)window.FindName("OutputPathLabel")).Text == output && ((TextBlock)window.FindName("ArchivePathLabel")).Text == archive, "Immediate crop did not use saved directory settings");
            Assert(!Directory.Exists(output) && !Directory.Exists(archive), "Entering crop should not create workflow folders or move files");
            Call(window, "CancelCrop");
            EditDirectories(window, Path.Combine(directory, "cancelled-output"), Path.Combine(directory, "cancelled-archive"), false);
            Assert(settings.OutputDirectory == output && settings.ArchiveDirectory == archive, "Cancelling settings changed saved workflow directories");
        }
        finally
        {
            if (surface.IsCropping) Call(window, "CancelCrop");
            window.Close();
        }
        Console.WriteLine("Crop entry: immediate entry without dialogs; workflow directories edited, persisted and cancelled only in Settings");
    }

    private static void EditDirectories(MainWindow window, string output, string archive, bool accept)
    {
        bool visited = false;
        var edit = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var dialog = Application.Current.Windows.OfType<SettingsWindow>().Single();
            var options = (WorkflowOptions)Field(dialog, "workflow").GetValue(dialog)!;
            ((CheckBox)Field(options, "useSource").GetValue(options)!).IsChecked = false;
            ((CheckBox)Field(options, "useDefaultArchive").GetValue(options)!).IsChecked = false;
            ((TextBox)Field(options, "output").GetValue(options)!).Text = output;
            ((TextBox)Field(options, "archive").GetValue(options)!).Text = archive;
            if (accept)
            {
                var footer = (StackPanel)((DockPanel)dialog.Content).Children[0];
                var buttons = (StackPanel)footer.Children[1];
                buttons.Children.OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (dialog.DialogResult != true) dialog.Close();
            }
            else dialog.DialogResult = false;
            visited = true;
        });
        Call(window, "SettingsClick", window, new RoutedEventArgs());
        edit.Abort(); Assert(visited, "Directory editing was not available from Settings");
    }

    private static void EnterWithoutDialog(Action enter, MainWindow window)
    {
        var existing = Application.Current.Windows.Cast<Window>().ToHashSet();
        bool intercepted = false;
        var guard = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(w => !existing.Contains(w)).ToArray())
            { intercepted = true; dialog.Close(); }
        });
        enter();
        guard.Abort();
        Assert(!intercepted, "Crop entry was intercepted by a dialog");
        Assert(Application.Current.Windows.Cast<Window>().All(existing.Contains), "Crop entry created an intermediate window");
    }

    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
