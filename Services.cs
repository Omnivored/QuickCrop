using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

namespace QuickCrop;

public sealed class ImageDocument : IDisposable
{
    public MagickImage Pixels { get; }
    public string Path { get; }
    public SourceStamp Stamp { get; }
    public ImageDocument(string path, MagickImage pixels, SourceStamp stamp) { Path = path; Pixels = pixels; Stamp = stamp; }
    public void Dispose() => Pixels.Dispose();
}

public static class ImageFiles
{
    private static readonly Lazy<bool> MagickInitialized = new(() =>
    {
        ResourceLimits.Memory = 512UL * 1024 * 1024;
        ResourceLimits.Disk = 2UL * 1024 * 1024 * 1024;
        ResourceLimits.Thread = 2;
        return true;
    });
    internal static void InitializeMagick() => _ = MagickInitialized.Value;
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".tga", ".psd", ".ppm", ".pgm", ".pbm", ".pcx" };
    internal static MagickReadSettings ReadSettings(string path) => new()
    {
        FrameIndex = 0, FrameCount = 1,
        // Select only raster decoders, even if a script/document has a misleading image extension.
        Format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => MagickFormat.Jpeg, ".png" => MagickFormat.Png,
            ".bmp" => MagickFormat.Bmp, ".dib" => MagickFormat.Dib, ".gif" => MagickFormat.Gif,
            ".tif" or ".tiff" => MagickFormat.Tiff, ".webp" => MagickFormat.WebP, ".heic" or ".heif" => MagickFormat.Heic,
            ".avif" => MagickFormat.Avif, ".ico" => MagickFormat.Ico, ".tga" => MagickFormat.Tga, ".psd" => MagickFormat.Psd,
            ".ppm" => MagickFormat.Ppm, ".pgm" => MagickFormat.Pgm, ".pbm" => MagickFormat.Pbm, ".pcx" => MagickFormat.Pcx,
            _ => throw new InvalidDataException("不支持此图片扩展名。")
        }
    };

    public static List<string> Enumerate(string folder, bool recursive, IEnumerable<string>? excludedDirectories = null)
    {
        var excluded = (excludedDirectories ?? []).Where(Directory.Exists).ToArray();
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var paths = Directory.EnumerateFiles(folder, "*", options).Where(p => Extensions.Contains(System.IO.Path.GetExtension(p)) && !excluded.Any(d => CropWorkflow.IsWithin(p, d))).ToList();
        paths.Sort((a, b) => ComparePaths(a, b, folder));
        return paths;
    }

    internal static int ComparePaths(string a, string b, string folder)
    {
        int c = StrCmpLogicalW(System.IO.Path.GetRelativePath(folder, a), System.IO.Path.GetRelativePath(folder, b));
        return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static ImageDocument Load(string path)
    {
        InitializeMagick();
        var image = new MagickImage();
        try
        {
            var stamp = SourceStamp.Read(path);
            // Only decode the first page/frame; never execute vector/document delegates.
            var settings = ReadSettings(path);
            image.Ping(path, settings);
            if ((ulong)image.Width * image.Height > 200_000_000) throw new InvalidDataException("图片超过 2 亿像素，请先缩小后再打开。");
            image.Read(path, settings);
            image.AutoOrient();
            image.ResetPage();
            if (stamp != SourceStamp.Read(path)) throw new IOException("文件在读取过程中发生变化，请刷新后重试。");
            return new ImageDocument(path, image, stamp);
        }
        catch { image.Dispose(); throw; }
    }

    public static BitmapSource Preview(IMagickImage<byte> image, uint maxEdge = 3200)
    {
        InitializeMagick();
        using var preview = image.Clone();
        if (preview.Width > maxEdge || preview.Height > maxEdge) preview.Resize(new MagickGeometry(maxEdge, maxEdge));
        preview.ColorSpace = ColorSpace.sRGB;
        using var pixelCollection = preview.GetPixels();
        var pixels = pixelCollection.ToByteArray(PixelMapping.BGRA)!;
        var bitmap = BitmapSource.Create((int)preview.Width, (int)preview.Height, 96, 96, PixelFormats.Bgra32, null, pixels, checked((int)preview.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    public static void Save(IMagickImage<byte> source, Int32Rect crop, string destination, bool circle, uint quality, PixelSize? outputSize = null)
    {
        InitializeMagick();
        outputSize?.Validate();
        if (crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0 || (long)crop.X + crop.Width > source.Width || (long)crop.Y + crop.Height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(crop));
        var format = System.IO.Path.GetExtension(destination).ToLowerInvariant() switch
        { ".png" => MagickFormat.Png, ".jpg" or ".jpeg" => MagickFormat.Jpeg, ".webp" => MagickFormat.WebP, ".tif" or ".tiff" => MagickFormat.Tiff, ".bmp" => MagickFormat.Bmp, _ => throw new InvalidDataException("请选择 PNG、JPEG、WebP、TIFF 或 BMP 格式。") };
        if (circle && format is not (MagickFormat.Png or MagickFormat.WebP or MagickFormat.Tiff)) throw new InvalidDataException("圆形裁剪需要支持透明背景的 PNG、WebP 或 TIFF 格式。");
        using var output = source.Clone();
        output.Crop(new MagickGeometry(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height)); output.ResetPage();
        if (outputSize is { } size) output.Resize(new MagickGeometry((uint)size.Width, (uint)size.Height) { IgnoreAspectRatio = true });
        if (circle)
        {
            using var mask = new MagickImage(MagickColors.Transparent, output.Width, output.Height);
            double cx = (output.Width - 1) / 2.0, cy = (output.Height - 1) / 2.0;
            new Drawables().FillColor(MagickColors.White).Ellipse(cx, cy, output.Width / 2.0, output.Height / 2.0, 0, 360).Draw(mask);
            output.Alpha(AlphaAction.Set); output.Composite(mask, CompositeOperator.CopyAlpha);
        }
        if (format is MagickFormat.Jpeg or MagickFormat.Bmp) { output.BackgroundColor = MagickColors.White; output.Alpha(AlphaAction.Remove); }
        // Cropping invalidates EXIF dimensions and embedded thumbnails; retain ICC color data only.
        var profile = output.GetColorProfile(); output.Strip(); if (profile is not null) output.SetProfile(profile);
        output.Quality = quality;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!;
        var temp = System.IO.Path.Combine(directory, $".quickcrop-{Guid.NewGuid():N}.tmp");
        try
        {
            output.Write(temp, format);
            if (File.Exists(destination)) File.Replace(temp, destination, null);
            else File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Recycle(string path)
    {
        RecycleBin.Move(path);
    }
}

public sealed class ImageEntry : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim ThumbnailGate = new(1);
    private BitmapSource? thumbnail;
    private Task? thumbnailTask;
    private readonly PreviewLoader? previews;
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public ImageEntry(string path, PreviewLoader? previews = null) { Path = path; this.previews = previews; }
    public BitmapSource? Thumbnail
    {
        get { _ = EnsureThumbnailAsync(); return thumbnail; }
    }
    internal Task EnsureThumbnailAsync() => thumbnailTask ??= LoadThumbnail();
    private async Task LoadThumbnail()
    {
        await Task.Delay(200);
        await ThumbnailGate.WaitAsync();
        try
        {
            if (thumbnail is not null) return;
            var bitmap = await Task.Run(() => (previews?.Cached(Path) ?? PreviewDecoder.Load(Path, 120, CancellationToken.None)).Bitmap);
            UsePreview(bitmap);
        }
        catch { /* A corrupt thumbnail must not stop folder navigation. */ }
        finally { ThumbnailGate.Release(); }
    }
    internal void UsePreview(BitmapSource source)
    {
        if (thumbnail is not null) return;
        double scale = Math.Min(1, 120d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        // Copy the tiny thumbnail so it cannot retain a large evicted preview through a transform chain.
        thumbnail = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        thumbnail.Freeze(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AppSettings
{
    public string CropShortcut { get; set; } = "Ctrl+Space";
    public bool IncludeSubfolders { get; set; }
    public string LastFolder { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string ArchiveDirectory { get; set; } = "";
    public string OutputExtension { get; set; } = "";
    public List<CropTemplate> CropTemplates { get; set; } = CropTemplate.Defaults();
    public List<string> PinnedCropTemplateIds { get; set; } = CropTemplate.Defaults().Take(5).Select(t => t.Id).ToList();
    public string SelectedCropTemplateId { get; set; } = "ratio-9-16";
    public int SelectedCropMode { get; set; } = -1;
    public CropSpecification? LockedCrop { get; set; }
    internal static string StorageDirectory { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickCrop");
    private static string FilePath => System.IO.Path.Combine(StorageDirectory, "settings.json");
    public static AppSettings Load()
    {
        try { var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); settings.EnsureCropTemplates(); return settings; } catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    public KeyGesture Gesture()
    {
        try { return (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(CropShortcut)!; }
        catch { return new KeyGesture(Key.Space, ModifierKeys.Control); }
    }
    public void EnsureCropTemplates()
    {
        var valid = new List<CropTemplate>();
        foreach (var template in CropTemplates ?? [])
        {
            try { template.Validate(); if (!valid.Any(t => t.Id == template.Id || string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase))) valid.Add(template); } catch { }
        }
        foreach (var template in CropTemplate.Defaults())
            if (valid.Count < 5 && !valid.Any(t => t.Id == template.Id || string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase))) valid.Add(template);
        CropTemplates = valid;
        PinnedCropTemplateIds = (PinnedCropTemplateIds ?? []).Where(id => valid.Any(t => t.Id == id)).Concat(valid.Select(t => t.Id)).Distinct().Take(5).ToList();
        if (!valid.Any(t => t.Id == SelectedCropTemplateId)) SelectedCropTemplateId = PinnedCropTemplateIds[0];
        if (SelectedCropMode is not (-1 or 0 or 1 or 7 or 8 or 9)) SelectedCropMode = -1;
        try { LockedCrop?.Validate(); } catch { LockedCrop = null; }
    }
}
