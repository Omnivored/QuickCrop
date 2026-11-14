using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace QuickCrop;

public enum CropTemplateKind { Ratio, Pixels }

public readonly record struct PixelSize(int Width, int Height)
{
    public void Validate()
    {
        if (Width < 1 || Height < 1 || Width > 50000 || Height > 50000 || (long)Width * Height > 200_000_000)
            throw new InvalidDataException("像素尺寸必须为正整数，单边不超过 50000，总像素不超过 2 亿。");
    }
}

public sealed class CropTemplate : INotifyPropertyChanged
{
    private string name = "";
    private double width = 1, height = 1;
    private CropTemplateKind kind;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => name; set { name = value; Changed(); } }
    public double Width { get => width; set { width = value; Changed(); } }
    public double Height { get => height; set { height = value; Changed(); } }
    public CropTemplateKind Kind { get => kind; set { kind = value; Changed(); } }
    [JsonIgnore] public double Ratio => Width / Height;
    [JsonIgnore] public string SizeLabel => Kind == CropTemplateKind.Pixels ? $"{Width:0} × {Height:0} px" : $"{Width:0.###}:{Height:0.###}";
    [JsonIgnore] public string DisplayLabel => Name == SizeLabel ? Name : $"{Name} · {SizeLabel}";
    [JsonIgnore] public PixelSize? OutputSize => Kind == CropTemplateKind.Pixels ? new PixelSize((int)Width, (int)Height) : null;
    public CropTemplate Clone() => new() { Id = Id, Name = Name, Width = Width, Height = Height, Kind = Kind };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 40) throw new InvalidDataException("模板名称不能为空，最多 40 个字符。");
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0 || Ratio < .001 || Ratio > 1000) throw new InvalidDataException("模板宽高必须为正数，比例范围为 1:1000 至 1000:1。");
        if (!Enum.IsDefined(Kind)) throw new InvalidDataException("请选择比例或像素尺寸。");
        if (Kind == CropTemplateKind.Pixels)
        {
            if (Width != Math.Truncate(Width) || Height != Math.Truncate(Height) || Width > 50000 || Height > 50000) throw new InvalidDataException("像素宽高必须为不超过 50000 的正整数。");
            OutputSize!.Value.Validate();
        }
    }
    public static List<CropTemplate> Defaults() => new[] { (9, 16), (2, 3), (1, 1), (16, 9), (3, 2), (4, 3) }
        .Select(p => new CropTemplate { Id = $"ratio-{p.Item1}-{p.Item2}", Name = $"{p.Item1}:{p.Item2}", Width = p.Item1, Height = p.Item2 }).ToList();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    }
}

public sealed record CropSpecification(double Ratio, PixelSize? OutputSize = null, bool Circle = false)
{
    public void Validate()
    {
        if (!double.IsFinite(Ratio) || Ratio < .001 || Ratio > 1000) throw new InvalidDataException("固定裁剪比例无效。");
        OutputSize?.Validate();
        if (OutputSize is { } size && Math.Abs((double)size.Width / size.Height - Ratio) > .001) throw new InvalidDataException("输出尺寸与裁剪比例不一致。");
    }
}
