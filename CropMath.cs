using System.Windows;

namespace QuickCrop;

public static class CropMath
{
    public static Rect Fit(double imageWidth, double imageHeight, double ratio, double fraction = 1)
    {
        double w = imageWidth * fraction, h = imageHeight * fraction;
        if (ratio > 0) { if (w / h > ratio) w = h * ratio; else h = w / ratio; }
        return new Rect((imageWidth - w) / 2, (imageHeight - h) / 2, w, h);
    }
    public static Rect FromPoints(Point anchor, Point target, double ratio, double width, double height)
    {
        anchor = new Point(Math.Clamp(anchor.X, 0, width), Math.Clamp(anchor.Y, 0, height));
        target = new Point(Math.Clamp(target.X, 0, width), Math.Clamp(target.Y, 0, height));
        double dx = target.X - anchor.X, dy = target.Y - anchor.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (ratio > 0)
        {
            w = Math.Max(w, h * ratio); h = w / ratio;
            double availableW = dx < 0 ? anchor.X : width - anchor.X;
            double availableH = dy < 0 ? anchor.Y : height - anchor.Y;
            double scale = Math.Min(1, Math.Min(availableW / Math.Max(w, .001), availableH / Math.Max(h, .001)));
            w *= scale; h *= scale;
        }
        return new Rect(dx < 0 ? anchor.X - w : anchor.X, dy < 0 ? anchor.Y - h : anchor.Y, w, h);
    }
    public static Rect Move(Rect rect, Vector delta, double width, double height) =>
        new(Math.Clamp(rect.X + delta.X, 0, Math.Max(0, width - rect.Width)), Math.Clamp(rect.Y + delta.Y, 0, Math.Max(0, height - rect.Height)), rect.Width, rect.Height);
    public static Int32Rect Pixels(Rect rect, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(rect.X), 0, width - 1), y = Math.Clamp((int)Math.Round(rect.Y), 0, height - 1);
        return new Int32Rect(x, y, Math.Clamp((int)Math.Round(rect.Width), 1, width - x), Math.Clamp((int)Math.Round(rect.Height), 1, height - y));
    }
}
