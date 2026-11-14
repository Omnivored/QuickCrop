using System.IO;
using ImageMagick;
using QuickCrop;
using System.Windows;

internal static class CropTemplateChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("Default crop templates have five requested front positions", () =>
        {
            var settings = new AppSettings(); settings.EnsureCropTemplates();
            Assert(string.Join(",", settings.PinnedCropTemplateIds.Select(id => settings.CropTemplates.Single(t => t.Id == id).Name)) == "9:16,2:3,1:1,16:9,3:2");
        });
        check("Named ratio and exact pixel templates validate independently", () =>
        {
            new CropTemplate { Name = "电影", Width = 2.39, Height = 1 }.Validate();
            new CropTemplate { Name = "竖图", Width = 1080, Height = 1920, Kind = CropTemplateKind.Pixels }.Validate();
            Reject(() => new CropTemplate { Name = "", Width = 1, Height = 1 }.Validate());
            Reject(() => new CropTemplate { Name = "bad", Width = double.NaN }.Validate());
            Reject(() => new CropTemplate { Name = "fractional pixel", Width = 20.5, Kind = CropTemplateKind.Pixels }.Validate());
            Reject(() => new CropTemplate { Name = "too large", Width = 50000, Height = 50000, Kind = CropTemplateKind.Pixels }.Validate());
        });
        check("Damaged template settings restore five unique valid positions", () =>
        {
            var settings = new AppSettings { CropTemplates = [new() { Name = "invalid", Width = -1 }], PinnedCropTemplateIds = ["missing", "missing"], LockedCrop = new CropSpecification(0) };
            settings.EnsureCropTemplates();
            Assert(settings.CropTemplates.Count >= 5 && settings.PinnedCropTemplateIds.Distinct().Count() == 5 && settings.LockedCrop is null);
        });
        check("Crop templates, front positions and fixed size survive restart", () =>
        {
            string previous = AppSettings.StorageDirectory;
            try
            {
                AppSettings.StorageDirectory = Path.Combine(root, "template-settings");
                var template = new CropTemplate { Name = "封面", Width = 300, Height = 500, Kind = CropTemplateKind.Pixels };
                var settings = new AppSettings { LockedCrop = new CropSpecification(.6, new PixelSize(300, 500)) };
                settings.CropTemplates.Add(template); settings.PinnedCropTemplateIds[0] = template.Id; settings.SelectedCropTemplateId = template.Id; settings.Save();
                var loaded = AppSettings.Load();
                Assert(loaded.CropTemplates.Single(t => t.Id == template.Id).Name == "封面" && loaded.PinnedCropTemplateIds[0] == template.Id && loaded.LockedCrop == settings.LockedCrop);
            }
            finally { AppSettings.StorageDirectory = previous; }
        });
        check("Pixel template exports exact dimensions without changing the source", () =>
        {
            using var source = new MagickImage(MagickColors.SeaGreen, 600, 400);
            string path = Path.Combine(root, "template-exact.png");
            ImageFiles.Save(source, new Int32Rect(50, 0, 240, 400), path, false, 95, new PixelSize(150, 250));
            using var output = ImageFiles.Load(path);
            Assert(output.Pixels.Width == 150 && output.Pixels.Height == 250 && source.Width == 600 && source.Height == 400);
        });
        check("Invalid pixel output cannot overwrite an existing image", () =>
        {
            using var source = new MagickImage(MagickColors.Blue, 40, 40);
            string path = Path.Combine(root, "template-protected.png"); File.WriteAllText(path, "original");
            Reject(() => ImageFiles.Save(source, new Int32Rect(0, 0, 40, 40), path, false, 95, new PixelSize(0, 20)));
            Assert(File.ReadAllText(path) == "original");
        });
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid crop template was accepted"); }
    private static void Assert(bool value) { if (!value) throw new Exception("Unexpected crop template state"); }
}
