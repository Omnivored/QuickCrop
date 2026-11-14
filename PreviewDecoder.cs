using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace QuickCrop;

public sealed record ImagePreview(string Path, SourceStamp Stamp, int Width, int Height, BitmapSource Bitmap)
{
    public long Bytes => (long)Bitmap.PixelWidth * Bitmap.PixelHeight * Math.Max(4, (Bitmap.Format.BitsPerPixel + 7) / 8);
    public bool Satisfies(int edge) => Math.Max(Bitmap.PixelWidth, Bitmap.PixelHeight) >= Math.Min(edge, Math.Max(Width, Height));
}

public static class PreviewDecoder
{
    private static readonly HashSet<string> NativeFormats = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico" };

    public static ImagePreview Load(string path, int maxEdge, CancellationToken cancellationToken)
    {
        if (maxEdge < 1) throw new ArgumentOutOfRangeException(nameof(maxEdge));
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = SourceStamp.Read(path);
        ImagePreview result;
        if (NativeFormats.Contains(System.IO.Path.GetExtension(path)))
        {
            try { result = Native(path, stamp, maxEdge, cancellationToken); }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
            { result = Magick(path, stamp, maxEdge, cancellationToken); }
        }
        else result = Magick(path, stamp, maxEdge, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (stamp != SourceStamp.Read(path)) throw new IOException("文件在读取过程中发生变化，请刷新后重试。");
        return result;
    }

    private static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > 200_000_000) throw new InvalidDataException("图片超过 2 亿像素或尺寸无效，请先缩小后再打开。");
    }

    private static ImagePreview Native(string path, SourceStamp stamp, int edge, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        CheckSize(width, height);
        int orientation = Orientation(frame.Metadata as BitmapMetadata);
        if (System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) orientation = PngOrientation(stream, token) ?? orientation;
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
        if (Math.Max(width, height) > edge)
        {
            if (width >= height) bitmap.DecodePixelWidth = edge;
            else bitmap.DecodePixelHeight = edge;
        }
        bitmap.EndInit(); bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        BitmapSource oriented = Orient(bitmap, orientation);
        return new ImagePreview(path, stamp, orientation >= 5 ? height : width, orientation >= 5 ? width : height, oriented);
    }

    private static int Orientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/eXIf/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.GetQuery(query) is { } value)
                {
                    int orientation = Convert.ToInt32(value);
                    if (orientation is >= 1 and <= 8) return orientation;
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        }
        return 1;
    }

    private static int? PngOrientation(Stream stream, CancellationToken token)
    {
        // Some WIC versions omit PNG eXIf metadata. Skip chunk bodies and use the existing EXIF parser.
        stream.Position = 8;
        int? exifOrientation = null;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position + 12 <= stream.Length)
        {
            token.ThrowIfCancellationRequested(); stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > stream.Length - stream.Position - 4) throw new FileFormatException("PNG 数据块长度无效。");
            if (header[4..].SequenceEqual("eXIf"u8))
            {
                if (length > 16 * 1024 * 1024) throw new InvalidDataException("PNG 的 EXIF 信息过大。");
                var data = new byte[length + 6];
                "Exif\0\0"u8.CopyTo(data); stream.ReadExactly(data.AsSpan(6));
                var exif = new ExifProfile(data);
                exifOrientation = exif.GetValue(ExifTag.Orientation)?.Value is ushort value && value is >= 1 and <= 8 ? value : null;
                stream.Seek(4, SeekOrigin.Current); continue;
            }
            if (header[4..].SequenceEqual("orNT"u8) && length == 1)
            {
                int value = stream.ReadByte();
                if (value is >= 1 and <= 8) return value;
                stream.Seek(4, SeekOrigin.Current); continue;
            }
            if (header[4..].SequenceEqual("IEND"u8)) return exifOrientation;
            stream.Seek(length + 4L, SeekOrigin.Current);
        }
        return exifOrientation;
    }

    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
        };
        if (matrix.IsIdentity) return source;
        var bitmap = new TransformedBitmap(source, new MatrixTransform(matrix)); bitmap.Freeze(); return bitmap;
    }

    private static ImagePreview Magick(string path, SourceStamp stamp, int edge, CancellationToken token)
    {
        ImageFiles.InitializeMagick();
        using var image = new MagickImage();
        image.Progress += (_, args) => args.Cancel = token.IsCancellationRequested;
        var settings = ImageFiles.ReadSettings(path);
        image.Ping(path, settings);
        int width = (int)image.Width, height = (int)image.Height;
        CheckSize(width, height);
        bool swap = image.Orientation is ImageMagick.Orientation.LeftTop or ImageMagick.Orientation.RightTop or ImageMagick.Orientation.RightBottom or ImageMagick.Orientation.LeftBottom;
        token.ThrowIfCancellationRequested();
        if (settings.Format == MagickFormat.Jpeg) settings.SetDefine(MagickFormat.Jpeg, "size", $"{edge}x{edge}");
        image.Read(path, settings); image.AutoOrient(); image.ResetPage();
        token.ThrowIfCancellationRequested();
        return new ImagePreview(path, stamp, swap ? height : width, swap ? width : height, ImageFiles.Preview(image, (uint)edge));
    }
}
