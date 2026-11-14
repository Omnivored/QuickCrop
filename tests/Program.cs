using System.IO;
using System.Windows;
using System.Windows.Input;
using ImageMagick;
using ImageMagick.Drawing;
using QuickCrop;

internal static class Program
{
    private static int passed, failed;
    private static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickCrop-tests-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        Console.WriteLine("Test artifacts: " + Root);
        if (args.Contains("--demo")) return DocumentationDemo.Run(Root);
        Check("Free crop clamps all four boundaries", () =>
        {
            var r = CropMath.FromPoints(new Point(20, 30), new Point(-100, 800), 0, 400, 300);
            Equal(new Rect(0, 30, 20, 270), r);
        });
        Check("All preset ratios remain in bounds", () =>
        {
            foreach (double ratio in new[] { 1d, 4d/3, 3d/2, 16d/9, 9d/16, .001, 1000 })
            {
                var r = CropMath.Fit(1200, 800, ratio);
                Near(r.Width / r.Height, ratio);
                Assert(new Rect(0, 0, 1200, 800).Contains(r));
            }
        });
        Check("Preset crop rectangles fill the largest possible image area", () =>
        {
            foreach (var size in new[] { (1600d, 1067d), (1067d, 1600d) })
            foreach (double ratio in new[] { 9d / 16, 2d / 3, 1, 16d / 9, 3d / 2, 4d / 3 })
            {
                var r = CropMath.Fit(size.Item1, size.Item2, ratio);
                Near(r.Width / r.Height, ratio);
                Assert(Math.Abs(r.Width - size.Item1) < .00001 || Math.Abs(r.Height - size.Item2) < .00001);
                Near(r.X * 2 + r.Width, size.Item1); Near(r.Y * 2 + r.Height, size.Item2);
            }
            Equal(new Rect(0, 0, 1600, 1067), CropMath.Fit(1600, 1067, 0));
        });
        Check("Random constrained drags preserve ratio and bounds", () =>
        {
            var random = new Random(42);
            for (int i = 0; i < 5000; i++)
            {
                double ratio = random.NextDouble() * 5 + .1;
                var r = CropMath.FromPoints(new Point(random.Next(1, 399), random.Next(1, 299)), new Point(random.Next(-200, 600), random.Next(-200, 500)), ratio, 400, 300);
                Assert(r.Left >= -.0001 && r.Top >= -.0001 && r.Right <= 400.0001 && r.Bottom <= 300.0001);
                if (r.Height > .0001) Near(r.Width / r.Height, ratio);
                var p = CropMath.Pixels(r, 400, 300);
                Assert(p.X >= 0 && p.Y >= 0 && p.Width >= 1 && p.Height >= 1 && p.X + p.Width <= 400 && p.Y + p.Height <= 300);
            }
        });
        Check("Moving a crop cannot leave the image", () =>
        {
            Equal(new Rect(0, 250, 100, 50), CropMath.Move(new Rect(20, 30, 100, 50), new Vector(-1000, 1000), 400, 300));
        });
        Check("Natural sort, mixed extensions and recursion", () =>
        {
            var folder = System.IO.Path.Combine(Root, "sort"); Directory.CreateDirectory(folder); Directory.CreateDirectory(System.IO.Path.Combine(folder, "sub"));
            foreach (string name in new[] { "image10.PNG", "image2.jpg", "image1.webp", "not-image.txt" }) File.WriteAllText(System.IO.Path.Combine(folder, name), "test");
            File.WriteAllText(System.IO.Path.Combine(folder, "sub", "nested.png"), "test");
            var paths = ImageFiles.Enumerate(folder, false); Equal(3, paths.Count); Equal("image1.webp", System.IO.Path.GetFileName(paths[0])); Equal("image2.jpg", System.IO.Path.GetFileName(paths[1])); Equal(4, ImageFiles.Enumerate(folder, true).Count);
        });
        foreach (var pair in new (string Extension, MagickFormat Format)[] {
            ("png", MagickFormat.Png), ("jpg", MagickFormat.Jpeg), ("webp", MagickFormat.WebP), ("bmp", MagickFormat.Bmp),
            ("gif", MagickFormat.Gif), ("tif", MagickFormat.Tiff), ("ico", MagickFormat.Ico), ("tga", MagickFormat.Tga),
            ("psd", MagickFormat.Psd), ("avif", MagickFormat.Avif) })
        {
            Check("Read and preview " + pair.Extension.ToUpperInvariant(), () =>
            {
                string path = System.IO.Path.Combine(Root, "sample." + pair.Extension);
                using (var image = Sample()) image.Write(path, pair.Format);
                using var doc = ImageFiles.Load(path); Equal(120U, doc.Pixels.Width); Equal(80U, doc.Pixels.Height);
                var preview = ImageFiles.Preview(doc.Pixels, 60); Equal(60, preview.PixelWidth); Equal(40, preview.PixelHeight); Assert(preview.IsFrozen);
            });
        }
        Check("Read HEIC photo using bundled decoder", () =>
        {
            using var doc = ImageFiles.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "example.heic"));
            Assert(doc.Pixels.Width > 0 && doc.Pixels.Height > 0); Assert(ImageFiles.Preview(doc.Pixels, 120).IsFrozen);
        });
        Check("EXIF orientation is applied before cropping", () =>
        {
            var path = System.IO.Path.Combine(Root, "orientation.jpg");
            using (var image = Sample()) { var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); image.SetProfile(exif); image.Orientation = Orientation.RightTop; image.Write(path, MagickFormat.Jpeg); }
            using var doc = ImageFiles.Load(path); Equal(80U, doc.Pixels.Width); Equal(120U, doc.Pixels.Height);
        });
        Check("First frame of animated GIF only", () =>
        {
            var path = System.IO.Path.Combine(Root, "animated.gif");
            using (var frames = new MagickImageCollection()) { frames.Add(new MagickImage(MagickColors.Red, 40, 30)); frames.Add(new MagickImage(MagickColors.Blue, 40, 30)); frames.Write(path, MagickFormat.Gif); }
            using var doc = ImageFiles.Load(path); Equal(40U, doc.Pixels.Width); Equal(MagickColors.Red, doc.Pixels.GetPixels().GetPixel(0, 0).ToColor());
        });
        foreach (string extension in new[] { "png", "jpg", "webp", "tif", "bmp" })
        {
            Check("Crop and export " + extension, () =>
            {
                using var image = Sample(); string path = System.IO.Path.Combine(Root, "crop." + extension);
                ImageFiles.Save(image, new Int32Rect(10, 20, 50, 30), path, false, 95);
                using var doc = ImageFiles.Load(path); Equal(50U, doc.Pixels.Width); Equal(30U, doc.Pixels.Height); Equal(120U, image.Width);
            });
        }
        Check("Circle export has transparent corners and opaque center", () =>
        {
            using var image = Sample(); string path = System.IO.Path.Combine(Root, "circle.png");
            ImageFiles.Save(image, new Int32Rect(10, 10, 60, 60), path, true, 95);
            using var output = new MagickImage(path); var pixels = output.GetPixels();
            Equal((byte)0, pixels.GetPixel(0, 0).ToColor()!.A); Equal((byte)255, pixels.GetPixel(30, 30).ToColor()!.A);
        });
        Check("Circle JPEG export fails without touching existing file", () =>
        {
            using var image = Sample(); string path = System.IO.Path.Combine(Root, "protected.jpg"); File.WriteAllText(path, "original");
            Throws<InvalidDataException>(() => ImageFiles.Save(image, new Int32Rect(0, 0, 60, 60), path, true, 95)); Equal("original", File.ReadAllText(path));
        });
        Check("Atomic overwrite and stale metadata removal", () =>
        {
            using var image = Sample(); string path = System.IO.Path.Combine(Root, "overwrite.png");
            var exif = new ExifProfile(); exif.SetValue(ExifTag.ImageDescription, "old crop metadata"); image.SetProfile(exif);
            image.Write(path); ImageFiles.Save(image, new Int32Rect(0, 0, 20, 30), path, false, 95);
            using var result = new MagickImage(path); Equal(20U, result.Width); Assert(result.GetExifProfile() is null);
            Assert(!Directory.EnumerateFiles(Root, ".quickcrop-*.tmp").Any());
        });
        Check("Invalid crop cannot modify destination", () =>
        {
            using var image = Sample(); string path = System.IO.Path.Combine(Root, "unchanged.png"); File.WriteAllText(path, "original");
            Throws<ArgumentOutOfRangeException>(() => ImageFiles.Save(image, new Int32Rect(119, 0, 50, 40), path, false, 95)); Equal("original", File.ReadAllText(path));
        });
        Check("Corrupt image reports an error without keeping a file lock", () =>
        {
            string path = System.IO.Path.Combine(Root, "corrupt.png"); File.WriteAllText(path, "not a picture");
            Throws<MagickException>(() => ImageFiles.Load(path)); using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Check("Default and custom shortcut parsing", () =>
        {
            Equal(Key.Space, new AppSettings().Gesture().Key);
            var settings = new AppSettings { CropShortcut = "Ctrl+Shift+X" }; Equal(Key.X, settings.Gesture().Key);
            Equal(ModifierKeys.Control | ModifierKeys.Shift, settings.Gesture().Modifiers);
            settings.CropShortcut = "invalid shortcut"; Equal(Key.Space, settings.Gesture().Key);
        });
        Check("Image can be exclusively opened after decode", () =>
        {
            string path = System.IO.Path.Combine(Root, "sample.png"); using var doc = ImageFiles.Load(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Check("Workflow saves in source folder and archives byte-identical original", () =>
        {
            string directory = NewDirectory("workflow-same"), source = System.IO.Path.Combine(directory, "photo.png"), archive = System.IO.Path.Combine(directory, "originals");
            using (var image = Sample()) image.Write(source);
            var original = File.ReadAllBytes(source);
            using var doc = ImageFiles.Load(source);
            var result = CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(10, 20, 50, 30), source, directory, archive, false, 95, doc.Stamp);
            Equal(source, result.OutputPath); Assert(File.ReadAllBytes(result.ArchivePath).SequenceEqual(original));
            using var saved = ImageFiles.Load(source); Equal(50U, saved.Pixels.Width); Equal(30U, saved.Pixels.Height);
        });
        Check("Workflow external directories and automatic collision numbering", () =>
        {
            string directory = NewDirectory("workflow-separate"), source = System.IO.Path.Combine(directory, "photo.png"), output = NewDirectory("workflow-output"), archive = NewDirectory("workflow-archive");
            using (var image = Sample()) image.Write(source);
            File.WriteAllText(System.IO.Path.Combine(output, "photo.png"), "existing crop"); File.WriteAllText(System.IO.Path.Combine(archive, "photo.png"), "existing original");
            using var doc = ImageFiles.Load(source);
            var result = CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(0, 0, 50, 30), source, output, archive, false, 95, doc.Stamp);
            Equal("photo_2.png", System.IO.Path.GetFileName(result.OutputPath)); Equal("photo_2.png", System.IO.Path.GetFileName(result.ArchivePath));
            Assert(!File.Exists(source)); Equal("existing crop", File.ReadAllText(System.IO.Path.Combine(output, "photo.png")));
            Equal("existing original", File.ReadAllText(System.IO.Path.Combine(archive, "photo.png")));
        });
        Check("Workflow fails before moving original on invalid crop", () =>
        {
            string directory = NewDirectory("workflow-fail"), source = System.IO.Path.Combine(directory, "photo.png");
            using (var image = Sample()) image.Write(source); var original = File.ReadAllBytes(source); using var doc = ImageFiles.Load(source);
            Throws<ArgumentOutOfRangeException>(() => CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(119, 0, 90, 30), source, directory, System.IO.Path.Combine(directory, "archive"), false, 95, doc.Stamp));
            Assert(File.ReadAllBytes(source).SequenceEqual(original)); Assert(!Directory.EnumerateFiles(directory, ".quickcrop-*").Any());
        });
        Check("Workflow detects external edits without archiving them", () =>
        {
            string directory = NewDirectory("workflow-external"), source = System.IO.Path.Combine(directory, "photo.png");
            using (var image = Sample()) image.Write(source); using var doc = ImageFiles.Load(source);
            File.AppendAllText(source, "external edit");
            Throws<IOException>(() => CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(0, 0, 50, 30), source, directory, System.IO.Path.Combine(directory, "archive"), false, 95, doc.Stamp));
            Assert(File.Exists(source)); Assert(!Directory.Exists(System.IO.Path.Combine(directory, "archive")));
        });
        Check("Locked original leaves source untouched and removes staged crop", () =>
        {
            string directory = NewDirectory("workflow-locked"), source = System.IO.Path.Combine(directory, "photo.png");
            using (var image = Sample()) image.Write(source); using var doc = ImageFiles.Load(source);
            using var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws<IOException>(() => CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(0, 0, 50, 30), source, directory, System.IO.Path.Combine(directory, "archive"), false, 95, doc.Stamp));
            Assert(File.Exists(source)); Assert(!Directory.EnumerateFiles(directory, ".quickcrop-*").Any());
        });
        Check("Circle workflow selects PNG automatically and keeps original filename in archive", () =>
        {
            string directory = NewDirectory("workflow-circle"), source = System.IO.Path.Combine(directory, "photo.jpg");
            using (var image = Sample()) image.Write(source); using var doc = ImageFiles.Load(source);
            var result = CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(0, 0, 60, 60), source, directory, System.IO.Path.Combine(directory, "archive"), true, 95, doc.Stamp, ".jpg");
            Equal(".png", System.IO.Path.GetExtension(result.OutputPath)); Equal("photo.jpg", System.IO.Path.GetFileName(result.ArchivePath)); Assert(!File.Exists(source));
        });
        Check("Workflow configured output format is honored", () =>
        {
            string directory = NewDirectory("workflow-format"), source = System.IO.Path.Combine(directory, "photo.png");
            using (var image = Sample()) image.Write(source); using var doc = ImageFiles.Load(source);
            var result = CropWorkflow.SaveAndArchive(doc.Pixels, new Int32Rect(0, 0, 60, 60), source, directory, System.IO.Path.Combine(directory, "archive"), false, 95, doc.Stamp, ".webp");
            using var saved = ImageFiles.Load(result.OutputPath); Equal(MagickFormat.WebP, saved.Pixels.Format);
        });
        Check("Workflow rejects dangerous folder configurations", () =>
        {
            string directory = NewDirectory("workflow-validation");
            Throws<InvalidDataException>(() => CropWorkflow.ValidateDirectories(directory, directory, directory));
            Throws<InvalidDataException>(() => CropWorkflow.ValidateDirectories(directory, System.IO.Path.Combine(directory, "archive", "output"), System.IO.Path.Combine(directory, "archive")));
            Throws<InvalidDataException>(() => CropWorkflow.ValidateDirectories(directory, "relative", System.IO.Path.Combine(directory, "archive")));
        });
        Check("Recursive scanning excludes archives and generated output directories", () =>
        {
            string directory = NewDirectory("exclude"), archive = System.IO.Path.Combine(directory, "archive"); Directory.CreateDirectory(archive);
            File.WriteAllText(System.IO.Path.Combine(directory, "original.png"), "image"); File.WriteAllText(System.IO.Path.Combine(archive, "archived.png"), "image");
            Equal(1, ImageFiles.Enumerate(directory, true, [archive]).Count);
        });
        PreviewChecks.Run(Root, Check);
        ViewRotationChecks.Run(Root, Check);
        CropTemplateChecks.Run(Root, Check);
        CropViewChecks.Run(Check);
        if (args.Contains("--perf")) Check("Large image load timings", () => PerformanceChecks.Run(Root));
        if (args.Contains("--ui")) Check("UI keyboard shortcuts, workflow and desktop layouts", () => UiChecks.Run(Root, args.Contains("--recycle")));
        Check("Recycle safety rejects permanent-delete transfer flags", () =>
        {
            Assert(!RecycleBin.IsRecycling(0)); Assert(!RecycleBin.IsRecycling(0x10));
            Assert(RecycleBin.IsRecycling(0x80)); Assert(RecycleBin.IsRecycling(0x180));
        });
        if (args.Contains("--recycle")) Check("Locked test file cannot be deleted or recycled", () =>
        {
            string path = System.IO.Path.Combine(Root, "locked-recycle.png");
            using (var image = Sample()) image.Write(path);
            var original = File.ReadAllBytes(path);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws<IOException>(() => ImageFiles.Recycle(path));
            Assert(File.Exists(path)); Assert(File.ReadAllBytes(path).SequenceEqual(original));
        });
        if (args.Contains("--recycle")) Check("Disposable test file moves to Windows Recycle Bin", () =>
        {
            string path = System.IO.Path.Combine(Root, "QuickCrop-recycle-test-" + Guid.NewGuid().ToString("N") + ".png");
            using (var image = Sample()) image.Write(path); ImageFiles.Recycle(path); Assert(!File.Exists(path));
        });
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    private static MagickImage Sample()
    {
        var image = new MagickImage(MagickColors.SeaGreen, 120, 80);
        new Drawables().FillColor(MagickColors.Coral).Rectangle(10, 10, 50, 50).Draw(image); return image;
    }
    private static string NewDirectory(string name) { var path = System.IO.Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    private static void Check(string name, Action test)
    { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Near(double actual, double expected) => Assert(Math.Abs(actual - expected) < .00001);
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
