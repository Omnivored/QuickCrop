using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using QuickCrop;

internal static class PreviewChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        foreach (string extension in new[] { "png", "jpg", "webp", "bmp", "gif", "tif", "ico", "tga", "psd", "avif" })
        {
            check("Fast preview " + extension.ToUpperInvariant(), () =>
            {
                var preview = PreviewDecoder.Load(System.IO.Path.Combine(root, "sample." + extension), 60, CancellationToken.None);
                Equal(120, preview.Width); Equal(80, preview.Height); Equal(60, preview.Bitmap.PixelWidth); Equal(40, preview.Bitmap.PixelHeight); Assert(preview.Bitmap.IsFrozen);
            });
        }
        check("Fast preview HEIC fallback", () =>
        {
            var preview = PreviewDecoder.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "example.heic"), 120, CancellationToken.None);
            Assert(Math.Max(preview.Bitmap.PixelWidth, preview.Bitmap.PixelHeight) == 120 && preview.Width > 120);
            using var full = ImageFiles.Load(preview.Path); Equal((int)full.Pixels.Width, preview.Width); Equal((int)full.Pixels.Height, preview.Height);
        });
        foreach (string extension in new[] { "jpg", "tif", "png" })
        foreach (int orientation in Enumerable.Range(1, 8))
        {
            check($"Fast preview {extension} EXIF orientation {orientation}", () =>
            {
                string path = System.IO.Path.Combine(root, $"exif-{orientation}.{extension}");
                using (var sample = Quadrants())
                {
                    var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)orientation); sample.SetProfile(exif); sample.Orientation = (ImageMagick.Orientation)orientation; sample.Write(path);
                }
                var preview = PreviewDecoder.Load(path, 200, CancellationToken.None);
                using var full = ImageFiles.Load(path);
                Equal((int)full.Pixels.Width, preview.Width); Equal((int)full.Pixels.Height, preview.Height);
                CompareCorners(ImageFiles.Preview(full.Pixels, 200), preview.Bitmap);
            });
        }
        check("Direct pixel transfer handles alpha and low-bit-depth images", () =>
        {
            using var image = new MagickImage(MagickColors.Transparent, 80, 60);
            new Drawables().FillColor(MagickColors.Red).Rectangle(20, 20, 50, 50).Draw(image);
            var preview = ImageFiles.Preview(image, 80); Equal((byte)0, Bytes(preview)[3]); Equal((byte)255, Bytes(preview)[(30 * 80 + 30) * 4 + 3]);
        });
        check("PNG orientation chunk works without an EXIF profile", () =>
        {
            string path = System.IO.Path.Combine(root, "png-orientation-only.png");
            using (var sample = Quadrants()) { sample.Orientation = ImageMagick.Orientation.RightTop; sample.Write(path); }
            var preview = PreviewDecoder.Load(path, 200, CancellationToken.None); using var full = ImageFiles.Load(path);
            Equal((int)full.Pixels.Width, preview.Width); Equal((int)full.Pixels.Height, preview.Height); CompareCorners(ImageFiles.Preview(full.Pixels, 200), preview.Bitmap);
        });
        check("Preview decoder releases the source file", () =>
        {
            string path = System.IO.Path.Combine(root, "sample.jpg"); _ = PreviewDecoder.Load(path, 60, CancellationToken.None);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        check("Cached previews are immediate and larger previews serve smaller requests", () =>
        {
            using var loader = new PreviewLoader(); string path = System.IO.Path.Combine(root, "sample.jpg");
            var first = loader.GetAsync(path, 100).GetAwaiter().GetResult(); var second = loader.GetAsync(path, 60);
            Assert(second.IsCompletedSuccessfully); Assert(ReferenceEquals(first, second.Result));
        });
        check("LRU count limit and least-recently-used eviction", () =>
        {
            int calls = 0;
            using var loader = new PreviewLoader(1024 * 1024, 2, (p, _, _) => { Interlocked.Increment(ref calls); return Fake(p); });
            string a = System.IO.Path.Combine(root, "sample.jpg"), b = System.IO.Path.Combine(root, "sample.png"), c = System.IO.Path.Combine(root, "sample.bmp");
            loader.GetAsync(a, 16).GetAwaiter().GetResult(); loader.GetAsync(b, 16).GetAwaiter().GetResult(); loader.GetAsync(a, 16).GetAwaiter().GetResult(); loader.GetAsync(c, 16).GetAwaiter().GetResult();
            Equal(2, loader.CachedCount); loader.GetAsync(b, 16).GetAwaiter().GetResult(); Equal(4, calls);
        });
        check("LRU respects byte budget", () =>
        {
            using var loader = new PreviewLoader(2048, 10, (p, _, _) => Fake(p));
            foreach (string extension in new[] { "jpg", "png", "bmp" }) loader.GetAsync(System.IO.Path.Combine(root, "sample." + extension), 16).GetAwaiter().GetResult();
            Equal(2, loader.CachedCount); Assert(loader.CachedBytes <= 2048);
        });
        check("Foreground joins an in-flight prefetch without duplicate decoding", () =>
        {
            int calls = 0; using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var loader = new PreviewLoader(decode: (p, _, t) => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(t); return Fake(p); });
            string path = System.IO.Path.Combine(root, "sample.jpg");
            var background = loader.GetAsync(path, 16, background: true);
            try { Assert(entered.Wait(5000)); var foreground = loader.GetAsync(path, 16); release.Set(); Task.WhenAll(background, foreground).GetAwaiter().GetResult(); Equal(1, calls); }
            finally { release.Set(); }
        });
        check("Cancelling a prefetch does not cancel a foreground subscriber", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
            using var loader = new PreviewLoader(decode: (p, _, t) => { entered.Set(); release.Wait(t); return Fake(p); });
            string path = System.IO.Path.Combine(root, "sample.jpg"); var background = loader.GetAsync(path, 16, cancel.Token, true);
            try
            {
                Assert(entered.Wait(5000)); var foreground = loader.GetAsync(path, 16); cancel.Cancel(); Cancelled(background); release.Set();
                Assert(foreground.GetAwaiter().GetResult().Bitmap.IsFrozen);
            }
            finally { release.Set(); }
        });
        check("Background decoding does not occupy foreground worker slots", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            string slow = System.IO.Path.Combine(root, "sample.jpg"), fast = System.IO.Path.Combine(root, "sample.png");
            using var loader = new PreviewLoader(decode: (p, _, t) => { if (p == slow) { entered.Set(); release.Wait(t); } return Fake(p); });
            var background = loader.GetAsync(slow, 16, background: true);
            try { Assert(entered.Wait(5000)); Assert(loader.GetAsync(fast, 16).Wait(5000)); }
            finally { release.Set(); background.GetAwaiter().GetResult(); }
        });
        check("Cancelled navigation work does not populate the cache", () =>
        {
            using var entered = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource(); using var done = new ManualResetEventSlim();
            using var loader = new PreviewLoader(decode: (p, _, t) => { entered.Set(); try { t.WaitHandle.WaitOne(); t.ThrowIfCancellationRequested(); return Fake(p); } finally { done.Set(); } });
            var task = loader.GetAsync(System.IO.Path.Combine(root, "sample.jpg"), 16, cancel.Token);
            Assert(entered.Wait(5000)); cancel.Cancel(); Cancelled(task); Assert(done.Wait(5000)); Equal(0, loader.CachedCount);
        });
        check("Changed files invalidate cached previews", () =>
        {
            string path = System.IO.Path.Combine(root, "cache-change.jpg"); File.Copy(System.IO.Path.Combine(root, "sample.jpg"), path);
            int calls = 0; using var loader = new PreviewLoader(decode: (p, _, _) => { Interlocked.Increment(ref calls); return Fake(p); });
            loader.GetAsync(path, 16).GetAwaiter().GetResult(); File.AppendAllText(path, "changed"); loader.GetAsync(path, 16).GetAwaiter().GetResult(); Equal(2, calls);
        });
        check("Cleared caches cannot be repopulated by obsolete in-flight work", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var loader = new PreviewLoader(decode: (p, _, _) => { entered.Set(); release.Wait(); return Fake(p); });
            var task = loader.GetAsync(System.IO.Path.Combine(root, "sample.jpg"), 16);
            try { Assert(entered.Wait(5000)); loader.Clear(); release.Set(); Cancelled(task); Equal(0, loader.CachedCount); }
            finally { release.Set(); }
        });
        check("Thumbnails do not retain full-size preview bitmaps", () =>
        {
            var source = PreviewDecoder.Load(System.IO.Path.Combine(root, "sample.jpg"), 120, CancellationToken.None);
            var entry = new ImageEntry(source.Path); entry.UsePreview(source.Bitmap);
            Assert(entry.Thumbnail is not null && !ReferenceEquals(source.Bitmap, entry.Thumbnail)); Equal(120, entry.Thumbnail!.PixelWidth); Assert(entry.Thumbnail.IsFrozen);
        });
        check("Crop setup and preview transforms do not read the original", () =>
        {
            string path = System.IO.Path.Combine(root, "sample.jpg"); var preview = PreviewDecoder.Load(path, 60, CancellationToken.None);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var session = new EditSession(preview); session.Apply(ImageTransform.RotateRight); session.Apply(ImageTransform.FlipHorizontal);
            Equal(80, session.Width); Equal(120, session.Height); Equal(40, session.Bitmap.PixelWidth);
        });
        check("Crop export uses full source pixels, not downsampled preview", () =>
        {
            string path = System.IO.Path.Combine(root, "sample.jpg"), output = System.IO.Path.Combine(root, "full-pixel-crop.png");
            var session = new EditSession(PreviewDecoder.Load(path, 30, CancellationToken.None));
            using var original = session.LoadOriginal(); ImageFiles.Save(original.Pixels, new Int32Rect(0, 0, 100, 70), output, false, 95);
            using var saved = ImageFiles.Load(output); Equal(100U, saved.Pixels.Width); Equal(70U, saved.Pixels.Height);
        });
        check("Preview transform sequence matches full-resolution export", () =>
        {
            string path = System.IO.Path.Combine(root, "exif-1.jpg"); var session = new EditSession(PreviewDecoder.Load(path, 200, CancellationToken.None));
            session.Apply(ImageTransform.RotateRight); session.Apply(ImageTransform.FlipHorizontal); session.Apply(ImageTransform.RotateLeft); session.Apply(ImageTransform.FlipVertical);
            using var full = session.LoadOriginal(); Equal(session.Width, (int)full.Pixels.Width); Equal(session.Height, (int)full.Pixels.Height); CompareCorners(ImageFiles.Preview(full.Pixels, 200), session.Bitmap);
        });
        check("High-detail crop previews preserve the complete rotation and flip sequence", () =>
        {
            string path = System.IO.Path.Combine(root, "exif-1.jpg");
            var low = PreviewDecoder.Load(path, 30, CancellationToken.None);
            var full = PreviewDecoder.Load(path, 200, CancellationToken.None);
            var session = new EditSession(low);
            session.Apply(ImageTransform.RotateRight); session.Apply(ImageTransform.FlipHorizontal); session.Apply(ImageTransform.RotateLeft); session.Apply(ImageTransform.FlipVertical);
            Assert(session.UpgradePreview(full));
            using var original = session.LoadOriginal(); CompareCorners(ImageFiles.Preview(original.Pixels, 200), session.Bitmap);
            Assert(!session.UpgradePreview(low));
        });
        check("Crop detail upgrades reject different sources and stale source stamps", () =>
        {
            string path = System.IO.Path.Combine(root, "sample.jpg");
            var low = PreviewDecoder.Load(path, 30, CancellationToken.None);
            var full = PreviewDecoder.Load(path, 200, CancellationToken.None);
            var session = new EditSession(low);
            Assert(!session.UpgradePreview(full with { Stamp = full.Stamp with { Length = full.Stamp.Length + 1 } }));
            Assert(!session.UpgradePreview(full with { Path = System.IO.Path.Combine(root, "other.jpg") }));
            Assert(ReferenceEquals(session.Bitmap, low.Bitmap));
        });
    }
    private static ImagePreview Fake(string path)
    {
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 64); bitmap.Freeze();
        return new ImagePreview(path, SourceStamp.Read(path), 16, 16, bitmap);
    }
    private static MagickImage Quadrants()
    {
        var image = new MagickImage(MagickColors.Yellow, 120, 80);
        new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 59, 39).FillColor(MagickColors.Lime).Rectangle(60, 0, 119, 39).FillColor(MagickColors.Blue).Rectangle(0, 40, 59, 79).Draw(image); return image;
    }
    private static byte[] Bytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static void CompareCorners(BitmapSource expected, BitmapSource actual)
    {
        Equal(expected.PixelWidth, actual.PixelWidth); Equal(expected.PixelHeight, actual.PixelHeight);
        byte[] a = Bytes(expected), b = Bytes(actual);
        foreach (var point in new[] { (5, 5), (expected.PixelWidth - 6, 5), (5, expected.PixelHeight - 6), (expected.PixelWidth - 6, expected.PixelHeight - 6) })
        for (int c = 0; c < 4; c++)
        {
            int index = (point.Item2 * expected.PixelWidth + point.Item1) * 4 + c;
            if (Math.Abs(a[index] - b[index]) > 5) throw new Exception($"Pixel mismatch at {point}: channel {c}, expected {a[index]}, actual {b[index]}");
        }
    }
    private static void Cancelled(Task task) { try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
}
