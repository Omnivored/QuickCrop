using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace QuickCrop;

public partial class MainWindow : Window
{
    private const string AppTitle = "轻裁 1.3.1";
    private AppSettings settings = AppSettings.Load();
    private ObservableCollection<ImageEntry> entries = [];
    private ImagePreview? document;
    private EditSession? editing;
    private string folder = "";
    private int index = -1, loadVersion, folderVersion, viewQuarterTurns;
    private readonly PreviewLoader previews = new();
    private readonly ViewRotationStore viewRotations = new();
    private CancellationTokenSource? loadCancellation, prefetchCancellation, detailCancellation;
    private Task prefetchTask = Task.CompletedTask;
    private Task detailTask = Task.CompletedTask;
    private bool syncing, updatingInputs, working, closing;
    private readonly HashSet<string> completedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> batchReplacements = new(StringComparer.OrdinalIgnoreCase);
    private double swappedRatio;
    private bool swappedOutput, updatingTemplates;
    private sealed record CropOption(string Title, int Mode, string? TemplateId = null);
    private WindowState previousState;
    private bool fullscreen;
    private bool Cropping => editing is not null;
    private bool Ready => document is not null && LoadingLabel.Visibility != Visibility.Visible && !working;

    public MainWindow()
    {
        InitializeComponent(); Filmstrip.ItemsSource = entries;
        Surface.SelectionChanged += UpdateSelection;
        Surface.ZoomChanged += SurfaceZoomChanged;
        Surface.CropStateChanged += UpdateCropViewControls;
        settings.EnsureCropTemplates(); ReloadTemplateControls(); UpdateControls(); UpdateShortcut();
        Loaded += async (_, _) =>
        {
            string? initial = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            if (initial is not null && (Directory.Exists(initial) || File.Exists(initial))) await OpenPath(initial);
            else if (Directory.Exists(settings.LastFolder)) await OpenFolder(settings.LastFolder);
        };
    }
    private void UpdateShortcut() => CropButton.ToolTip = $"裁剪 ({settings.CropShortcut} / F2)";
    private void UpdateControls()
    {
        CropButton.IsEnabled = Ready && !Cropping;
        DeleteButton.IsEnabled = Ready && !Cropping;
        PrevButton.IsEnabled = entries.Count > 0 && index > 0 && !working;
        NextButton.IsEnabled = entries.Count > 0 && index < entries.Count - 1 && !working;
        Filmstrip.IsEnabled = !working; CropPanel.IsEnabled = !working; RefreshButton.IsEnabled = !working;
        UpdateCropViewControls();
    }
    private void UpdateCropViewControls()
    {
        CropPreviewButton.Visibility = UndoCropButton.Visibility = Cropping ? Visibility.Visible : Visibility.Collapsed;
        CropPreviewButton.IsEnabled = Cropping && !working;
        CropPreviewButton.Content = Surface.IsCropPreview ? "\uE72B" : "\uE890";
        CropPreviewButton.ToolTip = Surface.IsCropPreview ? "返回调整 (Esc)" : "预览裁剪";
        UndoCropButton.IsEnabled = Cropping && Surface.CanUndoCrop && !working;
    }
    private void SetWorking(bool value) { working = value; UpdateControls(); }
    private void Notify(string message) => StatusLabel.Text = message;
    private void Error(string message, Exception error)
    {
        Notify(message);
        MessageBox.Show(this, $"{message}\n\n{error.Message}", "轻裁", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private bool LeaveCrop()
    {
        if (!Cropping) return true;
        if (MessageBox.Show(this, "放弃当前未保存的裁剪？", "未保存的裁剪", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        CancelCrop(); return true;
    }
    private async void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new OpenFolderDialog { Title = "选择图片文件夹", InitialDirectory = folder, Multiselect = false };
        if (dialog.ShowDialog(this) == true && LeaveCrop()) await OpenFolder(dialog.FolderName);
    }
    private async Task OpenPath(string path)
    {
        if (Directory.Exists(path)) await OpenFolder(path);
        else if (File.Exists(path)) await OpenFolder(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, System.IO.Path.GetFullPath(path));
    }
    private async Task OpenFolder(string path, string? select = null, bool newBatch = true)
    {
        int version = ++folderVersion;
        ++loadVersion;
        CancelRequests(); previews.Clear();
        Notify("正在读取文件夹…"); SetWorking(true);
        try
        {
            if (newBatch) { completedPaths.Clear(); batchReplacements.Clear(); }
            var excluded = new List<string> { System.IO.Path.Combine(path, "原图归档") };
            if (!string.IsNullOrWhiteSpace(settings.ArchiveDirectory) && !CropWorkflow.SamePath(path, settings.ArchiveDirectory)) excluded.Add(settings.ArchiveDirectory);
            if (!string.IsNullOrWhiteSpace(settings.OutputDirectory) && !CropWorkflow.SamePath(path, settings.OutputDirectory) && CropWorkflow.IsWithin(settings.OutputDirectory, path)) excluded.Add(settings.OutputDirectory);
            var replacements = batchReplacements.ToArray();
            var completed = new HashSet<string>(completedPaths, StringComparer.OrdinalIgnoreCase);
            var paths = await Task.Run(() =>
            {
                var outputs = replacements.Where(pair => File.Exists(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                var sources = ImageFiles.Enumerate(path, settings.IncludeSubfolders, excluded).Where(p => !completed.Contains(p)).Concat(outputs.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sources.Sort((a, b) => ImageFiles.ComparePaths(a, b, path));
                return sources.Select(p => outputs.GetValueOrDefault(p, p)).ToList();
            });
            if (closing || version != folderVersion) return;
            folder = path; settings.LastFolder = path;
            FolderLabel.Text = System.IO.Path.GetFileName(path.TrimEnd('\\'));
            FolderLabel.ToolTip = path;
            syncing = true;
            entries = new ObservableCollection<ImageEntry>(paths.Select(p => new ImageEntry(p, previews)));
            Filmstrip.ItemsSource = entries; index = -1; syncing = false;
            if (entries.Count == 0)
            {
                document = null; Surface.SetImage(null);
                EmptyPanel.Visibility = Visibility.Visible; CounterLabel.Text = "0 / 0"; ImageInfoLabel.Text = "";
                Notify("此文件夹中没有支持的图片");
            }
            else
            {
                int desired = select is null ? 0 : paths.FindIndex(p => string.Equals(p, select, StringComparison.OrdinalIgnoreCase));
                await SelectImage(Math.Max(0, desired));
            }
        }
        catch (Exception ex) { Error("无法打开文件夹", ex); }
        finally { if (version == folderVersion) SetWorking(false); }
    }
    private async Task SelectImage(int desired)
    {
        if (desired < 0 || desired >= entries.Count || closing) return;
        index = desired;
        syncing = true; Filmstrip.SelectedIndex = index; Filmstrip.ScrollIntoView(entries[index]); syncing = false;
        string path = entries[index].Path;
        int version = ++loadVersion;
        loadCancellation?.Cancel(); loadCancellation?.Dispose(); loadCancellation = new CancellationTokenSource();
        detailCancellation?.Cancel();
        viewQuarterTurns = 0;
        var token = loadCancellation.Token;
        EmptyPanel.Visibility = Visibility.Collapsed;
        // Keep the previous frame visible while loading, but disable all file actions until identity matches.
        document = null; ImageInfoLabel.Text = "";
        CounterLabel.Text = $"{index + 1} / {entries.Count}"; Title = $"{System.IO.Path.GetFileName(path)} · {AppTitle}";
        Notify(System.IO.Path.GetFileName(path)); UpdateControls();
        try
        {
            if (version != loadVersion || closing) return;
            int edge = PreviewEdge();
            // Join a useful prefetch before cancelling the rest of the previous neighbor requests.
            var pending = previews.GetAsync(path, edge, token);
            prefetchCancellation?.Cancel();
            LoadingLabel.Visibility = pending.IsCompletedSuccessfully ? Visibility.Collapsed : Visibility.Visible;
            var result = await pending;
            if (version != loadVersion || closing) return;
            document = result;
            viewQuarterTurns = viewRotations.Get(path, result.Stamp);
            ShowViewerImage();
            entries[index].UsePreview(result.Bitmap);
            PrefetchNeighbors(edge);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == loadVersion && !closing) { Surface.SetImage(null); Notify($"无法读取 {System.IO.Path.GetFileName(path)}：{ex.Message}"); EmptyPanel.Visibility = Visibility.Visible; }
        }
        finally
        {
            if (version == loadVersion && !closing) { LoadingLabel.Visibility = Visibility.Collapsed; UpdateControls(); }
        }
    }
    private BitmapSource RotateView(BitmapSource bitmap)
    {
        if (viewQuarterTurns == 0) return bitmap;
        var rotated = new TransformedBitmap(bitmap, new RotateTransform(viewQuarterTurns * 90));
        rotated.Freeze(); return rotated;
    }
    private void ShowViewerImage()
    {
        if (document is null) return;
        bool swapped = viewQuarterTurns % 2 != 0;
        Surface.SetImage(RotateView(document.Bitmap), swapped ? document.Height : document.Width, swapped ? document.Width : document.Height);
        string frameNote = System.IO.Path.GetExtension(document.Path).ToLowerInvariant() is ".gif" or ".tif" or ".tiff" or ".webp" or ".avif" ? " · 首帧 / 首页" : "";
        ImageInfoLabel.Text = $"{Surface.PixelWidth} × {Surface.PixelHeight} · {document.Stamp.Length / 1024d:N0} KB{frameNote}";
    }
    private int PreviewEdge()
    {
        double size = Math.Max(Surface.ActualWidth, Surface.ActualHeight);
        if (size < 1) size = Math.Max(Width, Height);
        return Math.Clamp((int)Math.Ceiling(size * VisualTreeHelper.GetDpi(this).DpiScaleX / 256) * 256, 1024, 4096);
    }
    private void PrefetchNeighbors(int edge)
    {
        prefetchCancellation?.Cancel(); prefetchCancellation?.Dispose(); prefetchCancellation = new CancellationTokenSource();
        var paths = new[] { index + 1, index + 2, index - 1 }.Where(i => i >= 0 && i < entries.Count).Select(i => entries[i].Path).ToArray();
        prefetchTask = previews.PrefetchAsync(paths, edge, prefetchCancellation.Token);
    }
    private void SurfaceZoomChanged()
    {
        ZoomLabel.Text = Surface.Bitmap is null ? "适应" : $"{Surface.DisplayScale:P0}";
        detailTask = UpdateDetail();
    }
    private async Task UpdateDetail()
    {
        if (document is null || Surface.Bitmap is null || working || closing) return;
        var current = document;
        var session = editing;
        int turns = viewQuarterTurns;
        int edge = Math.Min(Math.Max(current.Width, current.Height), Math.Max(1, (int)Math.Ceiling(Math.Max(current.Width, current.Height) * Math.Min(1, Surface.DisplayScale))));
        if (Math.Max(Surface.Bitmap.PixelWidth, Surface.Bitmap.PixelHeight) >= edge) return;
        detailCancellation?.Cancel(); detailCancellation?.Dispose(); detailCancellation = new CancellationTokenSource();
        var token = detailCancellation.Token;
        try
        {
            var detailed = await previews.GetAsync(current.Path, edge, token);
            if (!token.IsCancellationRequested && !closing && !working && ReferenceEquals(current, document) && ReferenceEquals(session, editing) && detailed.Stamp == current.Stamp && Surface.Bitmap is not null)
            {
                if (session is not null)
                {
                    if (session.UpgradePreview(detailed)) Surface.ReplaceBitmap(session.Bitmap);
                }
                else if (turns == viewQuarterTurns && Math.Max(detailed.Bitmap.PixelWidth, detailed.Bitmap.PixelHeight) > Math.Max(Surface.Bitmap.PixelWidth, Surface.Bitmap.PixelHeight)) Surface.ReplaceBitmap(RotateView(detailed.Bitmap));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && ReferenceEquals(current, document)) Notify($"高清细节读取失败：{ex.Message}"); }
    }
    private void CancelRequests()
    {
        loadCancellation?.Cancel(); prefetchCancellation?.Cancel(); detailCancellation?.Cancel();
    }
    private async Task Navigate(int delta)
    {
        if (working || entries.Count == 0) return;
        int next = Math.Clamp(index + delta, 0, entries.Count - 1);
        if (next != index && LeaveCrop()) await SelectImage(next);
    }
    private async void PrevClick(object sender, RoutedEventArgs e) => await Navigate(-1);
    private async void NextClick(object sender, RoutedEventArgs e) => await Navigate(1);
    private async void FilmstripChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || working || Filmstrip.SelectedIndex < 0 || Filmstrip.SelectedIndex == index) return;
        int desired = Filmstrip.SelectedIndex;
        if (LeaveCrop()) await SelectImage(desired);
        else { syncing = true; Filmstrip.SelectedIndex = index; syncing = false; }
    }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    { if (!working && Directory.Exists(folder) && LeaveCrop()) await OpenFolder(folder, document?.Path, false); }
    private async void DeleteClick(object sender, RoutedEventArgs e) => await DeleteCurrent();
    private async Task DeleteCurrent()
    {
        if (!Ready || Cropping || document is null) return;
        string path = document.Path;
        SetWorking(true);
        try
        {
            // Keep the Shell operation on the STA thread; only failures may display a dialog.
            ImageFiles.Recycle(path);
            viewRotations.Forget(path); PersistViewRotations();
            previews.Invalidate(path); document = null;
            int old = index;
            syncing = true; entries.RemoveAt(index); Filmstrip.SelectedIndex = -1; syncing = false;
            if (entries.Count > 0) await SelectImage(Math.Min(old, entries.Count - 1));
            else { index = -1; Surface.SetImage(null); EmptyPanel.Visibility = Visibility.Visible; CounterLabel.Text = "0 / 0"; ImageInfoLabel.Text = ""; Title = $"{AppTitle} · QuickCrop"; }
            Notify($"已移入回收站：{System.IO.Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) { Notify("已取消删除"); }
        catch (Exception ex) { Error("无法移入回收站，文件未从列表移除", ex); }
        finally { SetWorking(false); Surface.Focus(); }
    }
    private void CropClick(object sender, RoutedEventArgs e) => BeginCrop();
    private void BeginCrop(bool preserveRatio = false)
    {
        if (!Ready || Cropping || document is null) return;
        detailCancellation?.Cancel();
        editing = new EditSession(document); CropPanel.Visibility = Visibility.Visible;
        for (int i = 0; i < viewQuarterTurns; i++) editing.Apply(ImageTransform.RotateRight);
        Surface.SetImage(editing.Bitmap, editing.Width, editing.Height);
        ReloadTemplateControls();
        ConfigureMode(); Surface.BeginCrop(); UpdateControls(); Surface.Focus(); UpdateWorkflowLabels();
        Notify(System.IO.Path.GetFileName(document.Path));
    }
    private void ExcludeWorkflowFolders()
    {
        if (document is null) return;
        // Exclude archive/output subfolders already present in a recursive batch.
        string current = document.Path;
        string archive = CropWorkflow.ArchiveFolder(settings, current), output = CropWorkflow.OutputFolder(settings, current);
        syncing = true;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            string p = entries[i].Path;
            if (batchReplacements.Values.Any(outputPath => CropWorkflow.SamePath(outputPath, p))) continue;
            if (!CropWorkflow.SamePath(p, current) && (CropWorkflow.IsWithin(p, archive) ||
                (!string.IsNullOrWhiteSpace(settings.OutputDirectory) && !CropWorkflow.SamePath(output, folder) && CropWorkflow.IsWithin(p, output)))) entries.RemoveAt(i);
        }
        index = entries.ToList().FindIndex(x => CropWorkflow.SamePath(x.Path, current)); Filmstrip.SelectedIndex = index; syncing = false;
        CounterLabel.Text = $"{index + 1} / {entries.Count}"; UpdateControls();
    }
    private void UpdateWorkflowLabels()
    {
        if (document is null) return;
        OutputPathLabel.Text = CropWorkflow.OutputFolder(settings, document.Path); OutputPathLabel.ToolTip = OutputPathLabel.Text;
        ArchivePathLabel.Text = CropWorkflow.ArchiveFolder(settings, document.Path); ArchivePathLabel.ToolTip = ArchivePathLabel.Text;
    }
    private void CancelCrop()
    {
        if (editing is null) return;
        detailCancellation?.Cancel();
        editing = null; CropPanel.Visibility = Visibility.Collapsed;
        ShowViewerImage();
        Surface.EndCrop(); UpdateControls(); Surface.Focus(); Notify(document is null ? "就绪" : System.IO.Path.GetFileName(document.Path));
    }
    private void CancelCropClick(object sender, RoutedEventArgs e) { if (!working) CancelCrop(); }
    private void ConfigureMode()
    {
        if (ModeBox is null || Surface is null || RatioInputs is null || editing is null) return;
        int mode = settings.SelectedCropMode;
        var locked = settings.LockedCrop;
        RatioInputs.Visibility = mode == 7 && locked is null ? Visibility.Visible : Visibility.Collapsed;
        Surface.Circle = locked?.Circle ?? mode == 9; Surface.FixedPixels = locked is null && mode == 8;
        Surface.Ratio = locked?.Ratio ?? (mode == -1 ? SelectedTemplate?.Ratio ?? 1 : mode switch
        { 1 => (double)editing.Width / editing.Height, 9 => 1, 7 => ReadRatio(), _ => 0 });
        if (locked is null && swappedRatio > 0) Surface.Ratio = swappedRatio;
        SwapRatioButton.IsEnabled = locked is null && Surface.Ratio > 0 && !Surface.Circle;
        TemplateGrid.IsEnabled = locked is null;
        var output = CurrentOutputSize;
        OutputSizeLabel.Text = output is { } size ? $"输出 {size.Width} × {size.Height} px" : "";
        OutputSizeLabel.Visibility = output is null ? Visibility.Collapsed : Visibility.Visible;
        Surface.InvalidateVisual();
    }
    private CropTemplate? SelectedTemplate => settings.CropTemplates.FirstOrDefault(t => t.Id == settings.SelectedCropTemplateId);
    private PixelSize? CurrentOutputSize
    {
        get
        {
            if (settings.LockedCrop is { } locked) return locked.OutputSize;
            var size = settings.SelectedCropMode == -1 ? SelectedTemplate?.OutputSize : null;
            return size is { } pixels && swappedOutput ? new PixelSize(pixels.Height, pixels.Width) : size;
        }
    }
    private void ReloadTemplateControls()
    {
        settings.EnsureCropTemplates(); updatingTemplates = true;
        var buttons = new[] { Preset1, Preset2, Preset3, Preset4, Preset5 };
        for (int i = 0; i < buttons.Length; i++)
        {
            var template = settings.CropTemplates.Single(t => t.Id == settings.PinnedCropTemplateIds[i]);
            var content = new StackPanel { MaxWidth = 56 };
            content.Children.Add(new TextBlock { Text = template.Name, FontSize = template.Name == template.SizeLabel ? 14 : 11, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            if (template.Name != template.SizeLabel) content.Children.Add(new TextBlock { Text = template.Kind == CropTemplateKind.Pixels ? $"{template.Width:0}×{template.Height:0}" : template.SizeLabel, FontSize = 10, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            buttons[i].Content = content; buttons[i].Tag = template.Id; buttons[i].ToolTip = template.DisplayLabel;
            buttons[i].IsChecked = settings.SelectedCropMode == -1 && template.Id == settings.SelectedCropTemplateId;
        }
        ModeBox.Items.Clear();
        var options = new List<CropOption> { new("更多", -2), new("自由裁剪", 0), new("原图比例", 1) };
        options.AddRange(settings.CropTemplates.Where(t => !settings.PinnedCropTemplateIds.Contains(t.Id)).Select(t => new CropOption(t.DisplayLabel, -1, t.Id)));
        options.AddRange(new[] { new CropOption("自定义比例", 7), new CropOption("固定选区像素", 8), new CropOption("圆形 · 透明背景", 9) });
        foreach (var option in options) ModeBox.Items.Add(new ComboBoxItem { Content = option.Title, Tag = option, ToolTip = option.Title });
        int selected = options.FindIndex(o => o.Mode == settings.SelectedCropMode && (o.Mode != -1 || o.TemplateId == settings.SelectedCropTemplateId));
        ModeBox.SelectedIndex = Math.Max(0, selected);
        LockCropSizeCheck.IsChecked = settings.LockedCrop is not null;
        TemplateGrid.IsEnabled = settings.LockedCrop is null;
        updatingTemplates = false;
    }
    private void SelectCropTemplate(string id)
    {
        if (settings.LockedCrop is not null) { ReloadTemplateControls(); return; }
        if (!settings.CropTemplates.Any(t => t.Id == id)) return;
        settings.SelectedCropTemplateId = id; settings.SelectedCropMode = -1;
        ApplyCropChoice();
    }
    private void PresetClick(object sender, RoutedEventArgs e) { if (sender is ToggleButton { Tag: string id }) SelectCropTemplate(id); }
    private void ApplyCropChoice()
    {
        swappedRatio = 0; swappedOutput = false; ReloadTemplateControls();
        if (Cropping) { ConfigureMode(); Surface.ResetSelection(); }
        SaveCropPreferences(); Surface.Focus();
    }
    private void SaveCropPreferences() { try { settings.Save(); } catch (Exception ex) { Notify($"尺寸设置本次有效，但未能保存：{ex.Message}"); } }
    private void ManageTemplatesClick(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new CropTemplatesWindow(settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings.CropTemplates = dialog.Templates; settings.PinnedCropTemplateIds = dialog.PinnedIds;
        ApplyCropChoice();
    }
    private void LockCropSizeClick(object sender, RoutedEventArgs e)
    {
        if (!Cropping) return;
        if (LockCropSizeCheck.IsChecked == true)
        {
            var output = CurrentOutputSize;
            if (Surface.FixedPixels)
            {
                var selection = CropMath.Pixels(Surface.Selection, Surface.PixelWidth, Surface.PixelHeight);
                output = new PixelSize(selection.Width, selection.Height);
            }
            var snapshot = new CropSpecification(Surface.Ratio > 0 ? Surface.Ratio : Surface.Selection.Width / Surface.Selection.Height, output, Surface.Circle);
            try { snapshot.Validate(); settings.LockedCrop = snapshot; }
            catch (Exception ex) { LockCropSizeCheck.IsChecked = false; Notify(ex.Message); return; }
        }
        else settings.LockedCrop = null;
        ReloadTemplateControls(); ConfigureMode();
        if (LockCropSizeCheck.IsChecked != true) Surface.ResetSelection();
        SaveCropPreferences(); Surface.Focus();
    }
    private double ReadRatio()
    {
        return double.TryParse(RatioWidth.Text, out var w) && double.TryParse(RatioHeight.Text, out var h) && w > 0 && h > 0 && double.IsFinite(w / h) ? Math.Clamp(w / h, .001, 1000) : 1;
    }
    private void CropModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updatingTemplates || settings.LockedCrop is not null || ModeBox.SelectedItem is not ComboBoxItem { Tag: CropOption option } || option.Mode == -2) return;
        if (option.TemplateId is not null) SelectCropTemplate(option.TemplateId);
        else { settings.SelectedCropMode = option.Mode; ApplyCropChoice(); }
    }
    private void CustomRatioChanged(object sender, TextChangedEventArgs e)
    { if (!IsInitialized || !Cropping || settings.SelectedCropMode != 7 || settings.LockedCrop is not null) return; swappedRatio = 0; ConfigureMode(); Surface.ResetSelection(); }
    private void SwapRatioClick(object sender, RoutedEventArgs e)
    { if (Surface.Ratio <= 0 || settings.LockedCrop is not null) return; swappedRatio = 1 / Surface.Ratio; swappedOutput = !swappedOutput; ConfigureMode(); Surface.ResetSelection(); }
    private void UpdateSelection()
    {
        if (!Cropping || Surface.Selection.IsEmpty) return;
        updatingInputs = true;
        var r = CropMath.Pixels(Surface.Selection, Surface.PixelWidth, Surface.PixelHeight);
        WidthInput.Text = r.Width.ToString(); HeightInput.Text = r.Height.ToString(); PositionLabel.Text = $"X {r.X} · Y {r.Y}";
        updatingInputs = false;
    }
    private void PixelInputChanged(object sender, RoutedEventArgs e)
    {
        ApplyPixelInputs(sender);
    }
    private bool ApplyPixelInputs(object sender)
    {
        if (updatingInputs || !Cropping || editing is null) return false;
        if (!int.TryParse(WidthInput.Text, out var w) || !int.TryParse(HeightInput.Text, out var h) || w < 1 || h < 1 || w > editing.Width || h > editing.Height)
        { Notify("选区宽高必须为正整数，且不能超出图片"); UpdateSelection(); return false; }
        if (Surface.Ratio > 0)
        {
            if (ReferenceEquals(sender, HeightInput)) w = Math.Max(1, (int)Math.Round(h * Surface.Ratio));
            else h = Math.Max(1, (int)Math.Round(w / Surface.Ratio));
            if (w > editing.Width || h > editing.Height) { Notify("该比例的选区超出图片范围"); UpdateSelection(); return false; }
        }
        Rect old = Surface.Selection;
        Surface.SetSelection(new Rect(Math.Clamp(old.X, 0, editing.Width - w), Math.Clamp(old.Y, 0, editing.Height - h), w, h));
        return true;
    }
    private void ResetCropClick(object sender, RoutedEventArgs e) => Surface.ResetSelection();
    private void GridClick(object sender, RoutedEventArgs e) { Surface.ShowGrid = GridCheck.IsChecked == true; Surface.InvalidateVisual(); }
    private void QualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (QualityLabel is not null) QualityLabel.Text = ((int)e.NewValue).ToString(); }
    private async Task TransformImage(ImageTransform operation)
    {
        if (!Ready || document is null) return;
        if (editing is null)
        {
            if (operation is not (ImageTransform.RotateLeft or ImageTransform.RotateRight)) return;
            detailCancellation?.Cancel();
            viewQuarterTurns = (viewQuarterTurns + (operation == ImageTransform.RotateRight ? 1 : 3)) % 4;
            viewRotations.Remember(document.Path, document.Stamp, viewQuarterTurns);
            ShowViewerImage();
            PersistViewRotations();
            return;
        }
        SetWorking(true);
        detailCancellation?.Cancel();
        try
        {
            await Task.Run(() => editing.Apply(operation));
            Surface.SetImage(editing.Bitmap, editing.Width, editing.Height); ConfigureMode(); Surface.BeginCrop();
        }
        catch (Exception ex) { Error("变换未完成", ex); }
        finally { SetWorking(false); }
    }
    private void PersistViewRotations()
    {
        try { viewRotations.Save(); }
        catch (Exception ex) { Notify($"旋转方向本次有效，但未能保存方向记录：{ex.Message}"); }
    }
    private async void RotateLeftClick(object sender, RoutedEventArgs e) => await TransformImage(ImageTransform.RotateLeft);
    private async void RotateRightClick(object sender, RoutedEventArgs e) => await TransformImage(ImageTransform.RotateRight);
    private async void FlipHorizontalClick(object sender, RoutedEventArgs e) => await TransformImage(ImageTransform.FlipHorizontal);
    private async void FlipVerticalClick(object sender, RoutedEventArgs e) => await TransformImage(ImageTransform.FlipVertical);
    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveCrop();
    private async Task SaveCrop()
    {
        if (editing is null || document is null || working) return;
        if (!ApplyPixelInputs(ReferenceEquals(Keyboard.FocusedElement, HeightInput) ? HeightInput : WidthInput)) return;
        string sourcePath = document.Path;
        string outputDir = CropWorkflow.OutputFolder(settings, sourcePath), archiveDir = CropWorkflow.ArchiveFolder(settings, sourcePath);
        SetWorking(true); Notify("正在保存…");
        try
        {
            var crop = CropMath.Pixels(Surface.Selection, (int)editing.Width, (int)editing.Height);
            uint quality = (uint)QualitySlider.Value; bool circle = Surface.Circle;
            var stamp = document.Stamp;
            string outputExtension = settings.OutputExtension;
            PixelSize? outputSize = CurrentOutputSize;
            var session = editing;
            var result = await Task.Run(() =>
            {
                using var original = session.LoadOriginal();
                return CropWorkflow.SaveAndArchive(original.Pixels, crop, sourcePath, outputDir, archiveDir, circle, quality, stamp, outputExtension, outputSize);
            });
            completedPaths.Add(sourcePath); completedPaths.Add(result.OutputPath);
            string originalSlot = batchReplacements.FirstOrDefault(pair => CropWorkflow.SamePath(pair.Value, sourcePath)).Key ?? sourcePath;
            batchReplacements[originalSlot] = result.OutputPath;
            previews.Invalidate(sourcePath); previews.Invalidate(result.OutputPath);
            viewRotations.Forget(sourcePath); viewRotations.Forget(result.OutputPath); PersistViewRotations();
            editing = null; document = null; Surface.EndCrop();
            int savedIndex = index;
            var savedEntry = new ImageEntry(result.OutputPath, previews);
            syncing = true; entries[savedIndex] = savedEntry; Filmstrip.SelectedIndex = savedIndex; syncing = false;
            _ = savedEntry.EnsureThumbnailAsync();
            // Keep every result in its original slot; only automatic batch advance skips finished images.
            int next = Enumerable.Range(1, entries.Count).Select(offset => (savedIndex + offset) % entries.Count).FirstOrDefault(i => !completedPaths.Contains(entries[i].Path), -1);
            CropPanel.Visibility = Visibility.Collapsed;
            if (next >= 0)
            {
                await SelectImage(next);
                SetWorking(false); BeginCrop(true);
                Notify($"已保存：{System.IO.Path.GetFileName(result.OutputPath)} · 原图已归档" + (document is null ? " · 下一张无法读取，可继续切图" : ""));
            }
            else
            {
                await SelectImage(savedIndex);
                Notify($"本批次处理完成 · 最后保存：{result.OutputPath}");
            }
        }
        catch (Exception ex) { Error("保存流程未完成", ex); }
        finally { SetWorking(false); }
    }
    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new SettingsWindow(settings, document?.Path) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool changed = settings.IncludeSubfolders != dialog.Recursive;
        settings.CropShortcut = dialog.Shortcut; settings.IncludeSubfolders = dialog.Recursive;
        settings.OutputDirectory = dialog.OutputDirectory; settings.ArchiveDirectory = dialog.ArchiveDirectory; settings.OutputExtension = dialog.OutputExtension;
        settings.CropTemplates = dialog.Templates; settings.PinnedCropTemplateIds = dialog.PinnedTemplateIds;
        ReloadTemplateControls();
        if (Cropping) { ConfigureMode(); Surface.ResetSelection(); }
        UpdateShortcut(); UpdateWorkflowLabels(); ExcludeWorkflowFolders();
        try { settings.Save(); } catch (Exception ex) { Error("设置无法保存到磁盘", ex); }
        if (changed && Directory.Exists(folder) && LeaveCrop()) await OpenFolder(folder, document?.Path, false);
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.25);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => Surface.ZoomBy(.8);
    private void FitClick(object sender, RoutedEventArgs e) => Surface.Fit();
    private void ActualClick(object sender, RoutedEventArgs e) => Surface.ActualPixels();
    private void CropPreviewClick(object sender, RoutedEventArgs e)
    {
        if (!Cropping || working) return;
        if (Surface.IsCropPreview) Surface.ReturnToCrop(); else Surface.PreviewCrop();
        Surface.Focus();
    }
    private void UndoCropClick(object sender, RoutedEventArgs e) { if (Cropping && !working) { Surface.UndoCrop(); Surface.Focus(); } }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!fullscreen) { previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousState; }
        fullscreen = !fullscreen;
    }
    private async void OnKeyDown(object sender, KeyEventArgs e) => await HandleKeyDown(e, Keyboard.Modifiers);
    private async Task HandleKeyDown(KeyEventArgs e, ModifierKeys modifiers)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        if (key == Key.Escape)
        {
            if (!working && Cropping) { if (Surface.IsCropPreview) Surface.ReturnToCrop(); else CancelCrop(); }
            else if (fullscreen) ToggleFullscreen();
            e.Handled = true; return;
        }
        var cropGesture = settings.Gesture();
        if ((key == cropGesture.Key && modifiers == cropGesture.Modifiers) || (key == Key.F2 && modifiers == ModifierKeys.None))
        {
            e.Handled = true;
            if (!e.IsRepeat) BeginCrop();
            return;
        }
        if (modifiers == ModifierKeys.Control && key == Key.O) { OpenFolderClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.S) { e.Handled = true; if (!e.IsRepeat) await SaveCrop(); return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or Slider) return;
        if (modifiers == ModifierKeys.Control && key is Key.Left or Key.Right)
        {
            e.Handled = true;
            if (!e.IsRepeat) await TransformImage(key == Key.Left ? ImageTransform.RotateLeft : ImageTransform.RotateRight);
            return;
        }
        if (working) return;
        if (key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (Cropping && modifiers == ModifierKeys.Control && key == Key.Z) { Surface.UndoCrop(); e.Handled = true; return; }
        switch (key)
        {
            case Key.F: Surface.Fit(); e.Handled = true; return;
            case Key.D1: case Key.NumPad1: Surface.ActualPixels(); e.Handled = true; return;
            case Key.Add: case Key.OemPlus: Surface.ZoomBy(1.25); e.Handled = true; return;
            case Key.Subtract: case Key.OemMinus: Surface.ZoomBy(.8); e.Handled = true; return;
        }
        if (Cropping)
        {
            if (key == Key.Enter) { await SaveCrop(); e.Handled = true; return; }
            double step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            Vector delta = key switch { Key.Left => new(-step, 0), Key.Right => new(step, 0), Key.Up => new(0, -step), Key.Down => new(0, step), _ => new() };
            if (delta.Length > 0) { Surface.SetSelection(CropMath.Move(Surface.Selection, delta, Surface.PixelWidth, Surface.PixelHeight)); e.Handled = true; }
            return;
        }
        switch (key)
        {
            case Key.Left: e.Handled = true; await Navigate(-1); break;
            case Key.Right: e.Handled = true; await Navigate(1); break;
            case Key.Delete: e.Handled = true; if (!e.IsRepeat) await DeleteCurrent(); break;
            case Key.Home: if (entries.Count > 0) await SelectImage(0); e.Handled = true; break;
            case Key.End: if (entries.Count > 0) await SelectImage(entries.Count - 1); e.Handled = true; break;
            case Key.F5: RefreshClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (working || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files?.Length > 0 && LeaveCrop()) await OpenPath(files[0]);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (working) { e.Cancel = true; Notify("请等待当前操作完成"); return; }
        if (!LeaveCrop()) { e.Cancel = true; return; }
        closing = true; ++loadVersion; ++folderVersion; CancelRequests(); previews.Dispose();
        PersistViewRotations();
        try { settings.Save(); } catch { /* Closing still succeeds on a read-only profile. */ }
    }
}
