namespace ParagliderToolbox.Terrain.Sampling;

/// <summary>
/// The decoded blocks of a source's rasters, shared by the requests of a build (neighboring tiles read the same
/// blocks), least recently used first out once more than the limit of pixels is kept. Concurrent requests for a block
/// share one load.
/// </summary>
public sealed class BlockCache<T>
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Level, int X, int Y), LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = [];
    private readonly long _maxPixels;
    private long _pixels;

    private sealed record Entry((string Level, int X, int Y) Key, Task<T[]?> Block, long Pixels);

    /// <summary>Initializes a cache that keeps about <paramref name="maxPixels"/> pixels.</summary>
    public BlockCache(long maxPixels = 64L * 1024 * 1024)
    {
        _maxPixels = maxPixels;
    }

    /// <summary>Gets a block of <paramref name="level"/>, loading it when it isn't cached.</summary>
    public async Task<T[]?> GetAsync(RasterLevel<T> level, int blockX, int blockY, CancellationToken cancellationToken)
    {
        var key = (level.Key, blockX, blockY);
        Task<T[]?> task;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                task = node.Value.Block;
            }
            else
            {
                // Not tied to one request's cancellation: other requests may wait for the same block.
                task = Task.Run(() => level.LoadBlockAsync(blockX, blockY, CancellationToken.None), CancellationToken.None);
                long pixels = (long)level.BlockWidth * level.BlockHeight;
                _entries[key] = _order.AddFirst(new Entry(key, task, pixels));
                _pixels += pixels;
                while (_pixels > _maxPixels && _order.Last is { } oldest && oldest != _order.First)
                {
                    _order.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                    _pixels -= oldest.Value.Pixels;
                }
            }
        }
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (Exception) when (task.IsFaulted)
        {
            // A failed load is tried again by the next request.
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var node) && node.Value.Block == task)
                {
                    _order.Remove(node);
                    _entries.Remove(key);
                    _pixels -= node.Value.Pixels;
                }
            }
            throw;
        }
    }
}
