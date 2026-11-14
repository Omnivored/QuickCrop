using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ImageMagick;
using QuickCrop;

internal static class PerformanceChecks
{
    public static void Run(string root)
    {
        string photo = System.IO.Path.Combine(Environment.CurrentDirectory, "samples", "01-lake.jpg");
        var results = new List<object>();
        foreach (var specification in new[] { (Width: 6000, Height: 4000, Extension: "jpg"), (Width: 6000, Height: 4000, Extension: "png"), (Width: 8000, Height: 6000, Extension: "jpg") })
        {
            string path = System.IO.Path.Combine(root, $"performance-{specification.Width}x{specification.Height}.{specification.Extension}");
            using (var image = new MagickImage(photo))
            {
                image.Resize(new MagickGeometry((uint)specification.Width, (uint)specification.Height) { IgnoreAspectRatio = true });
                image.Depth = 8; image.Quality = 95; image.Write(path);
            }
            // Both paths use warmed filesystem caches. Fixture generation and JIT warmup are excluded.
            using (var original = ImageFiles.Load(path)) _ = LegacyPreview(original.Pixels);
            _ = PreviewDecoder.Load(path, 1280, CancellationToken.None);
            var legacy = new List<double>(); var optimized = new List<double>();
            for (int sample = 0; sample < 3; sample++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var watch = Stopwatch.StartNew();
                using (var original = ImageFiles.Load(path)) _ = LegacyPreview(original.Pixels);
                legacy.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart(); _ = PreviewDecoder.Load(path, 1280, CancellationToken.None); optimized.Add(watch.Elapsed.TotalMilliseconds);
            }
            using var loader = new PreviewLoader(); _ = loader.GetAsync(path, 1280).GetAwaiter().GetResult();
            var cachedWatch = Stopwatch.StartNew();
            var cached = loader.GetAsync(path, 1280);
            _ = cached.GetAwaiter().GetResult(); double hit = cachedWatch.Elapsed.TotalMilliseconds;
            if (!cached.IsCompletedSuccessfully) throw new Exception("Cache hit was not immediate");
            double baseline = legacy.Order().ElementAt(1), candidate = optimized.Order().ElementAt(1);
            var row = new { specification.Width, specification.Height, Format = specification.Extension, FileBytes = new FileInfo(path).Length, LegacyMilliseconds = baseline, PreviewMilliseconds = candidate, CacheHitMilliseconds = hit, LegacySamples = legacy, PreviewSamples = optimized };
            results.Add(row); Console.WriteLine("TIMING " + JsonSerializer.Serialize(row));
        }
        string output = System.IO.Path.Combine(Environment.CurrentDirectory, "output", "performance-1.2.0.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Fixture = "Bundled landscape photo resized to test dimensions", DisplayEdge = 1280, SamplesPerPath = 3, FilesystemCache = "warm", Scope = "decode and preview preparation; not end-to-end UI or WPS comparison", Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static BitmapSource LegacyPreview(IMagickImage<byte> image)
    {
        using var preview = image.Clone();
        if (preview.Width > 3200 || preview.Height > 3200) preview.Resize(new MagickGeometry(3200, 3200));
        using var stream = new MemoryStream(preview.ToByteArray(MagickFormat.Png));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
