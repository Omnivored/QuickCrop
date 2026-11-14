using System.IO;
using System.Windows;
using ImageMagick;

namespace QuickCrop;

public readonly record struct SourceStamp(long Length, DateTime LastWriteTimeUtc)
{
    public static SourceStamp Read(string path) { var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc); }
}

public sealed record SaveResult(string OutputPath, string ArchivePath);

public static class CropWorkflow
{
    public static bool SamePath(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    public static bool IsWithin(string path, string directory) => SamePath(path, directory) || System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(directory).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void ValidateDirectories(string sourceDirectory, string outputDirectory, string archiveDirectory)
    {
        if (!System.IO.Path.IsPathFullyQualified(outputDirectory) || !System.IO.Path.IsPathFullyQualified(archiveDirectory)) throw new InvalidDataException("请选择完整的绝对目录路径。");
        if (SamePath(sourceDirectory, archiveDirectory)) throw new InvalidDataException("原图归档目录不能就是原图当前所在目录。");
        if (IsWithin(outputDirectory, archiveDirectory)) throw new InvalidDataException("成品目录不能位于原图归档目录中，两者需要分开。");
        if (File.Exists(outputDirectory) || File.Exists(archiveDirectory)) throw new InvalidDataException("所选路径是文件，请选择文件夹。");
    }
    public static string OutputFolder(AppSettings settings, string sourcePath) => string.IsNullOrWhiteSpace(settings.OutputDirectory) ? System.IO.Path.GetDirectoryName(sourcePath)! : settings.OutputDirectory;
    public static string ArchiveFolder(AppSettings settings, string sourcePath) => string.IsNullOrWhiteSpace(settings.ArchiveDirectory) ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, "原图归档") : settings.ArchiveDirectory;
    private static string UniquePath(string directory, string name, string? allowedExisting = null)
    {
        string path = System.IO.Path.Combine(directory, name), stem = System.IO.Path.GetFileNameWithoutExtension(name), extension = System.IO.Path.GetExtension(name);
        int n = 2;
        while ((File.Exists(path) || Directory.Exists(path)) && (allowedExisting is null || !SamePath(path, allowedExisting))) path = System.IO.Path.Combine(directory, $"{stem}_{n++}{extension}");
        return path;
    }
    public static SaveResult SaveAndArchive(IMagickImage<byte> image, Int32Rect crop, string sourcePath, string outputDirectory, string archiveDirectory, bool circle, uint quality, SourceStamp stamp, string outputExtension = "", PixelSize? outputSize = null)
    {
        sourcePath = System.IO.Path.GetFullPath(sourcePath);
        ValidateDirectories(System.IO.Path.GetDirectoryName(sourcePath)!, outputDirectory, archiveDirectory);
        outputDirectory = System.IO.Path.GetFullPath(outputDirectory); archiveDirectory = System.IO.Path.GetFullPath(archiveDirectory);
        if (stamp != SourceStamp.Read(sourcePath)) throw new IOException("原图已被其他程序更改，请刷新图片后再保存。");
        Directory.CreateDirectory(outputDirectory); Directory.CreateDirectory(archiveDirectory);
        string ext = string.IsNullOrWhiteSpace(outputExtension) ? System.IO.Path.GetExtension(sourcePath).ToLowerInvariant() : outputExtension.ToLowerInvariant();
        if (circle || ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".tif" or ".tiff" or ".bmp")) ext = ".png";
        string output = UniquePath(outputDirectory, System.IO.Path.GetFileNameWithoutExtension(sourcePath) + ext, sourcePath);
        string archive = UniquePath(archiveDirectory, System.IO.Path.GetFileName(sourcePath));
        string stage = System.IO.Path.Combine(outputDirectory, $".quickcrop-stage-{Guid.NewGuid():N}{ext}");
        bool archived = false;
        try
        {
            // Encode fully before moving the original. Publication never overwrites another file.
            ImageFiles.Save(image, crop, stage, circle, quality, outputSize);
            if (stamp != SourceStamp.Read(sourcePath)) throw new IOException("保存期间原图发生变化，请刷新后重试。");
            File.Move(sourcePath, archive); archived = true;
            try { File.Move(stage, output); }
            catch (Exception publishError)
            {
                try { File.Move(archive, sourcePath); archived = false; }
                catch (Exception rollbackError)
                { throw new IOException($"成品保存失败，原图已保留在：\n{archive}\n\n自动还原失败：{rollbackError.Message}", publishError); }
                throw new IOException("成品保存失败，原图已自动还原到原位置。", publishError);
            }
            return new SaveResult(output, archive);
        }
        finally
        {
            if (File.Exists(stage))
            {
                try { File.Delete(stage); }
                catch when (archived) { /* A successful result must not be turned into a false failure by temporary-file cleanup. */ }
            }
        }
    }
}
