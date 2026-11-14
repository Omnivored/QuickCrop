using System.IO;
using QuickCrop;

internal static class ViewRotationChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        string statePath = Path.Combine(root, "directions", "state.json");
        string first = Path.Combine(root, "direction1.jpg"), second = Path.Combine(root, "direction2.jpg");
        File.WriteAllText(first, "original one"); File.WriteAllText(second, "original two");
        var firstStamp = SourceStamp.Read(first); var secondStamp = SourceStamp.Read(second);
        check("Viewer directions persist independently without modifying originals", () =>
        {
            var store = new ViewRotationStore(statePath);
            store.Remember(first, firstStamp, 1); store.Remember(second, secondStamp, -1); store.Save();
            var loaded = new ViewRotationStore(statePath);
            Assert(loaded.Get(first.ToUpperInvariant(), firstStamp) == 1 && loaded.Get(second, secondStamp) == 3);
            Assert(File.ReadAllText(first) == "original one" && File.ReadAllText(second) == "original two");
        });
        check("A full turn clears the stored direction without affecting other images", () =>
        {
            var store = new ViewRotationStore(statePath); store.Remember(first, firstStamp, 4); store.Save();
            var loaded = new ViewRotationStore(statePath);
            Assert(loaded.Get(first, firstStamp) == 0 && loaded.Get(second, secondStamp) == 3);
        });
        check("Directions are invalidated when source contents change", () =>
        {
            var store = new ViewRotationStore(statePath); store.Remember(first, firstStamp, 2); store.Save();
            File.AppendAllText(first, " replacement");
            var loaded = new ViewRotationStore(statePath); Assert(loaded.Get(first, SourceStamp.Read(first)) == 0); loaded.Save();
            Assert(new ViewRotationStore(statePath).Get(first, firstStamp) == 0);
        });
        check("Saving viewer directions merges unrelated window edits", () =>
        {
            var a = new ViewRotationStore(statePath); var b = new ViewRotationStore(statePath);
            var currentStamp = SourceStamp.Read(first);
            a.Remember(first, currentStamp, 1); a.Save();
            b.Remember(second, secondStamp, 2); b.Save();
            var loaded = new ViewRotationStore(statePath);
            Assert(loaded.Get(first, currentStamp) == 1 && loaded.Get(second, secondStamp) == 2);
            a.Forget(first); a.Save();
            Assert(new ViewRotationStore(statePath).Get(second, secondStamp) == 2);
        });
        check("A damaged direction record cannot prevent image browsing", () =>
        {
            File.WriteAllText(statePath, "not json");
            var store = new ViewRotationStore(statePath); Assert(store.Get(second, secondStamp) == 0);
            store.Remember(second, secondStamp, 1); store.Save();
            Assert(new ViewRotationStore(statePath).Get(second, secondStamp) == 1);
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Unexpected saved viewer direction"); }
}
