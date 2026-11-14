using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace QuickCrop;

public enum ImageTransform { RotateLeft, RotateRight, FlipHorizontal, FlipVertical }

public sealed class EditSession
{
    private readonly List<ImageTransform> transforms = [];
    public ImagePreview Source { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public BitmapSource Bitmap { get; private set; }
    public EditSession(ImagePreview source) { Source = source; Width = source.Width; Height = source.Height; Bitmap = source.Bitmap; }
    public void Apply(ImageTransform operation)
    {
        Bitmap = TransformBitmap(Bitmap, operation);
        if (operation is ImageTransform.RotateLeft or ImageTransform.RotateRight) (Width, Height) = (Height, Width);
        transforms.Add(operation);
    }
    public bool UpgradePreview(ImagePreview preview)
    {
        if (!CropWorkflow.SamePath(preview.Path, Source.Path) || preview.Stamp != Source.Stamp || preview.Width != Source.Width || preview.Height != Source.Height ||
            Math.Max(preview.Bitmap.PixelWidth, preview.Bitmap.PixelHeight) <= Math.Max(Bitmap.PixelWidth, Bitmap.PixelHeight)) return false;
        BitmapSource bitmap = preview.Bitmap;
        foreach (var operation in transforms) bitmap = TransformBitmap(bitmap, operation);
        Bitmap = bitmap; return true;
    }
    private static BitmapSource TransformBitmap(BitmapSource source, ImageTransform operation)
    {
        Transform transform = operation switch
        {
            ImageTransform.RotateLeft => new RotateTransform(-90), ImageTransform.RotateRight => new RotateTransform(90),
            ImageTransform.FlipHorizontal => new ScaleTransform(-1, 1), _ => new ScaleTransform(1, -1)
        };
        var bitmap = new TransformedBitmap(source, transform); bitmap.Freeze(); return bitmap;
    }
    public ImageDocument LoadOriginal()
    {
        if (SourceStamp.Read(Source.Path) != Source.Stamp) throw new IOException("原图已被其他程序更改，请刷新后再保存。");
        var document = ImageFiles.Load(Source.Path);
        try
        {
            if (document.Stamp != Source.Stamp) throw new IOException("读取期间原图发生变化，请刷新后重试。");
            foreach (var operation in transforms)
            {
                switch (operation)
                {
                    case ImageTransform.RotateLeft: document.Pixels.Rotate(-90); break;
                    case ImageTransform.RotateRight: document.Pixels.Rotate(90); break;
                    case ImageTransform.FlipHorizontal: document.Pixels.Flop(); break;
                    case ImageTransform.FlipVertical: document.Pixels.Flip(); break;
                }
            }
            document.Pixels.ResetPage(); return document;
        }
        catch { document.Dispose(); throw; }
    }
}
