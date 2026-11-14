using System.IO;
using System.Text.Json;

namespace QuickCrop;

internal sealed class ViewRotationStore
{
    private sealed record Rotation(SourceStamp Stamp, int QuarterTurns);
    private readonly string storagePath;
    private Dictionary<string, Rotation> rotations;
    private readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);

    public ViewRotationStore(string? storagePath = null)
    {
        this.storagePath = storagePath ?? Path.Combine(AppSettings.StorageDirectory, "view-rotations.json");
        rotations = Read();
    }
    public int Get(string path, SourceStamp stamp)
    {
        path = Path.GetFullPath(path);
        if (!rotations.TryGetValue(path, out var rotation)) return 0;
        if (rotation.Stamp == stamp) return rotation.QuarterTurns;
        Forget(path); return 0;
    }
    public void Remember(string path, SourceStamp stamp, int quarterTurns)
    {
        path = Path.GetFullPath(path);
        quarterTurns = (quarterTurns % 4 + 4) % 4;
        if (quarterTurns == 0) Forget(path);
        else { rotations[path] = new Rotation(stamp, quarterTurns); changed.Add(path); }
    }
    public void Forget(string path)
    {
        path = Path.GetFullPath(path);
        rotations.Remove(path); changed.Add(path);
    }
    public void Save()
    {
        if (changed.Count == 0) return;
        // Merge only this window's edits so unrelated image directions are retained.
        var saved = Read();
        foreach (string path in changed)
        {
            if (rotations.TryGetValue(path, out var rotation)) saved[path] = rotation;
            else saved.Remove(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        string temp = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, storagePath, true);
            rotations = saved; changed.Clear();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private Dictionary<string, Rotation> Read()
    {
        var result = new Dictionary<string, Rotation>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Rotation?>>(File.ReadAllText(storagePath));
            if (saved is not null)
                foreach (var pair in saved)
                    if (Path.IsPathFullyQualified(pair.Key) && pair.Value is { QuarterTurns: >= 1 and <= 3 }) result[Path.GetFullPath(pair.Key)] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return result;
    }
}
