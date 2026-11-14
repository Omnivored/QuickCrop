namespace QuickCrop;

public sealed class PreviewLoader : IDisposable
{
    private sealed record Key(string Path, SourceStamp Stamp, int Edge);
    private sealed class Pending
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<ImagePreview> Task = null!;
        public int Users;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, LinkedListNode<ImagePreview>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<ImagePreview> lru = new();
    private readonly Dictionary<Key, Pending> pending = [];
    private readonly SemaphoreSlim foregroundGate = new(2), backgroundGate = new(1);
    private readonly Func<string, int, CancellationToken, ImagePreview> decode;
    private readonly long byteBudget;
    private readonly int countBudget;
    private long cacheBytes;
    private bool disposed;
    public long CachedBytes { get { lock (sync) return cacheBytes; } }
    public int CachedCount { get { lock (sync) return cache.Count; } }

    public PreviewLoader(long byteBudget = 128L * 1024 * 1024, int countBudget = 8, Func<string, int, CancellationToken, ImagePreview>? decode = null)
    { this.byteBudget = byteBudget; this.countBudget = countBudget; this.decode = decode ?? PreviewDecoder.Load; }

    public async Task<ImagePreview> GetAsync(string path, int edge, CancellationToken token = default, bool background = false)
    {
        token.ThrowIfCancellationRequested();
        path = System.IO.Path.GetFullPath(path);
        var stamp = SourceStamp.Read(path);
        var key = new Key(path.ToUpperInvariant(), stamp, edge);
        Pending job;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cache.TryGetValue(path, out var cached))
            {
                if (cached.Value.Stamp == stamp && cached.Value.Satisfies(edge)) { lru.Remove(cached); lru.AddFirst(cached); return cached.Value; }
                if (cached.Value.Stamp != stamp) Remove(cached);
            }
            if (!pending.TryGetValue(key, out job!) || job.Cancellation.IsCancellationRequested)
            {
                job = new Pending(); pending[key] = job;
                var captured = job;
                job.Task = Task.Run(() => DecodeAsync(key, captured, path, edge, background));
                _ = job.Task.ContinueWith(t => { _ = t.Exception; captured.Cancellation.Dispose(); }, TaskScheduler.Default);
            }
            job.Users++;
        }
        try { return await job.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (sync)
            {
                job.Users--;
                if (job.Users == 0 && !job.Task.IsCompleted) job.Cancellation.Cancel();
            }
        }
    }

    private async Task<ImagePreview> DecodeAsync(Key key, Pending job, string path, int edge, bool background)
    {
        var gate = background ? backgroundGate : foregroundGate;
        var token = job.Cancellation.Token;
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            var result = decode(path, edge, token);
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (!disposed && result.Bytes <= byteBudget / 2)
                {
                    if (cache.TryGetValue(path, out var old))
                    {
                        if (old.Value.Stamp == result.Stamp && old.Value.Satisfies(edge)) return old.Value;
                        Remove(old);
                    }
                    var node = lru.AddFirst(result); cache[path] = node; cacheBytes += result.Bytes;
                    while (cacheBytes > byteBudget || cache.Count > countBudget) Remove(lru.Last!);
                }
            }
            return result;
        }
        finally
        {
            if (entered) gate.Release();
            lock (sync) { if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, job)) pending.Remove(key); }
        }
    }
    private void Remove(LinkedListNode<ImagePreview> node) { cache.Remove(node.Value.Path); lru.Remove(node); cacheBytes -= node.Value.Bytes; }

    public async Task PrefetchAsync(IEnumerable<string> paths, int edge, CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try { await GetAsync(path, edge, token, background: true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* A bad neighbor must not interrupt foreground navigation. */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    public void Invalidate(string path) { lock (sync) { if (cache.TryGetValue(path, out var node)) Remove(node); } }
    internal ImagePreview? Cached(string path)
    {
        var stamp = SourceStamp.Read(path);
        lock (sync) return cache.TryGetValue(path, out var node) && node.Value.Stamp == stamp ? node.Value : null;
    }
    public void Clear()
    {
        lock (sync)
        {
            foreach (var job in pending.Values) if (!job.Task.IsCompleted) job.Cancellation.Cancel();
            cache.Clear(); lru.Clear(); cacheBytes = 0;
        }
    }
    public void Dispose() { lock (sync) { disposed = true; Clear(); } }
}
