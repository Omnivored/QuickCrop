using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QuickCrop;

public sealed class WorkflowOptions : StackPanel
{
    private readonly string sourcePath;
    private readonly CheckBox useSource, useDefaultArchive;
    private readonly TextBox output, archive;
    private readonly Button outputBrowse, archiveBrowse;
    private readonly ComboBox format;
    private static readonly string[] FormatExtensions = ["", ".png", ".jpg", ".webp", ".tif", ".bmp"];
    public string OutputDirectory => useSource.IsChecked == true ? "" : output.Text.Trim();
    public string ArchiveDirectory => useDefaultArchive.IsChecked == true ? "" : archive.Text.Trim();
    public string OutputExtension => FormatExtensions[Math.Max(0, format.SelectedIndex)];
    public WorkflowOptions(AppSettings settings, string sourcePath)
    {
        this.sourcePath = sourcePath;
        var panel = this;
        panel.Children.Add(new TextBlock { Text = "成品保存目录", Margin = new Thickness(0, 0, 0, 8) });
        useSource = new CheckBox { Content = "原图所在目录", IsChecked = string.IsNullOrWhiteSpace(settings.OutputDirectory), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(useSource);
        output = new TextBox { Text = CropWorkflow.OutputFolder(settings, sourcePath), ToolTip = "裁剪后图片的保存目录" };
        outputBrowse = new Button { Content = "浏览…", Margin = new Thickness(10, 0, 0, 0) };
        panel.Children.Add(PathRow(output, outputBrowse));
        outputBrowse.Click += (_, _) => Browse(output);
        useSource.Checked += (_, _) => UpdateOutput(); useSource.Unchecked += (_, _) => UpdateOutput();
        panel.Children.Add(new TextBlock { Text = "裁剪前原图归档目录", Margin = new Thickness(0, 22, 0, 8) });
        useDefaultArchive = new CheckBox { Content = "原图所在目录中的“原图归档”子目录", IsChecked = string.IsNullOrWhiteSpace(settings.ArchiveDirectory), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(useDefaultArchive);
        archive = new TextBox { Text = CropWorkflow.ArchiveFolder(settings, sourcePath), ToolTip = "保存成功后，将未经修改的原图移动至此" };
        archiveBrowse = new Button { Content = "浏览…", Margin = new Thickness(10, 0, 0, 0) };
        archiveBrowse.Click += (_, _) => Browse(archive); panel.Children.Add(PathRow(archive, archiveBrowse));
        useDefaultArchive.Checked += (_, _) => UpdateArchive(); useDefaultArchive.Unchecked += (_, _) => UpdateArchive();
        panel.Children.Add(new TextBlock { Text = "成品格式", Margin = new Thickness(0, 20, 0, 8) });
        format = new ComboBox { ItemsSource = new[] { "沿用原格式（不支持写入的格式转 PNG）", "PNG", "JPEG", "WebP", "TIFF", "BMP" }, SelectedIndex = Math.Max(0, Array.IndexOf(FormatExtensions, settings.OutputExtension)) };
        panel.Children.Add(format);
        UpdateOutput(); UpdateArchive();
    }
    public void Validate()
    {
        var settings = new AppSettings { OutputDirectory = OutputDirectory, ArchiveDirectory = ArchiveDirectory };
        CropWorkflow.ValidateDirectories(System.IO.Path.GetDirectoryName(sourcePath)!, CropWorkflow.OutputFolder(settings, sourcePath), CropWorkflow.ArchiveFolder(settings, sourcePath));
    }
    public void Reset() { useSource.IsChecked = true; useDefaultArchive.IsChecked = true; format.SelectedIndex = 0; }
    private void UpdateOutput() { output.IsEnabled = outputBrowse.IsEnabled = useSource.IsChecked != true; }
    private void UpdateArchive() { archive.IsEnabled = archiveBrowse.IsEnabled = useDefaultArchive.IsChecked != true; }
    private static Grid PathRow(TextBox box, Button browse)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1); row.Children.Add(box); row.Children.Add(browse); return row;
    }
    private void Browse(TextBox box)
    {
        var dialog = new OpenFolderDialog { Title = "选择目录", InitialDirectory = Directory.Exists(box.Text) ? box.Text : System.IO.Path.GetDirectoryName(sourcePath)!, Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) box.Text = dialog.FolderName;
    }
}
