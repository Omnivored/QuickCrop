using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickCrop;

public sealed class SettingsWindow : Window
{
    public string Shortcut { get; private set; }
    public bool Recursive => recursive.IsChecked == true;
    public string OutputDirectory => workflow.OutputDirectory;
    public string ArchiveDirectory => workflow.ArchiveDirectory;
    public string OutputExtension => workflow.OutputExtension;
    public List<CropTemplate> Templates => templateSettings.CropTemplates.Select(t => t.Clone()).ToList();
    public List<string> PinnedTemplateIds => templateSettings.PinnedCropTemplateIds.ToList();
    private readonly TextBox shortcut;
    private readonly CheckBox recursive;
    private readonly TextBlock error;
    private readonly WorkflowOptions workflow;
    private readonly AppSettings templateSettings;
    public SettingsWindow(AppSettings settings, string? sourcePath = null)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "设置 · 轻裁"; Width = 620; Height = Math.Min(760, SystemParameters.WorkArea.Height - 60); MinWidth = 480; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Shortcut = settings.CropShortcut;
        templateSettings = new AppSettings { CropTemplates = settings.CropTemplates.Select(t => t.Clone()).ToList(), PinnedCropTemplateIds = settings.PinnedCropTemplateIds.ToList() };
        var panel = new StackPanel { Margin = new Thickness(26) };
        var root = new DockPanel();
        panel.Children.Add(new TextBlock { Text = "裁剪快捷键", Margin = new Thickness(0, 0, 0, 10), FontSize = 16 });
        shortcut = new TextBox { Text = Shortcut, IsReadOnly = true, MinHeight = 38, ToolTip = "点击后按下新的组合键" };
        shortcut.PreviewKeyDown += CaptureShortcut; panel.Children.Add(shortcut);
        error = new TextBlock { Text = "", Foreground = System.Windows.Media.Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        recursive = new CheckBox { Content = "包含子文件夹", IsChecked = settings.IncludeSubfolders };
        panel.Children.Add(recursive);
        var templatesButton = new Button { Content = "尺寸模板", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        templatesButton.Click += (_, _) =>
        {
            var dialog = new CropTemplatesWindow(templateSettings) { Owner = this };
            if (dialog.ShowDialog() == true) { templateSettings.CropTemplates = dialog.Templates; templateSettings.PinnedCropTemplateIds = dialog.PinnedIds; }
        };
        panel.Children.Add(templatesButton);
        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = "保存与原图归档", FontSize = 16, Margin = new Thickness(0, 0, 0, 18) });
        string sourceDirectory = Directory.Exists(settings.LastFolder) ? settings.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(sourceDirectory)) sourceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        workflow = new WorkflowOptions(settings, sourcePath ?? Path.Combine(sourceDirectory, "image.jpg"));
        panel.Children.Add(workflow);
        var footer = new StackPanel { Margin = new Thickness(26, 0, 26, 20) };
        footer.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 10, 0) };
        reset.Click += (_, _) => { Shortcut = "Ctrl+Space"; shortcut.Text = Shortcut; error.Text = ""; recursive.IsChecked = false; workflow.Reset(); };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => { try { workflow.Validate(); DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } };
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(save); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
    }
    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        var modifiers = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        bool reserved = key is Key.Delete or Key.Escape or Key.Enter or Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.F2 or Key.F5 or Key.F11 ||
            (modifiers == ModifierKeys.Control && key is Key.O or Key.S) || (modifiers == ModifierKeys.Alt && key == Key.F4) || modifiers.HasFlag(ModifierKeys.Windows);
        if (reserved || (modifiers is ModifierKeys.None or ModifierKeys.Shift && !(key >= Key.F1 && key <= Key.F12)))
        { error.Text = "该按键已保留，请使用 Ctrl / Alt 组合键或其他功能键。"; return; }
        Shortcut = new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, modifiers))!;
        shortcut.Text = Shortcut; error.Text = "";
    }
}
