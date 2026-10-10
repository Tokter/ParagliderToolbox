using System.Collections.Concurrent;
using ParagliderToolbox.Terrain.Data;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>Opens Cloud Optimized GeoTIFFs through a <see cref="DataCache"/>, each once (concurrent requests share the opening).</summary>
public sealed class CogFiles(DataCache cache)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<GeoTiff?>>> _files = new();

    /// <summary>Gets the cache the files are read through.</summary>
    public DataCache Cache => cache;

    /// <summary>Opens the file at <paramref name="url"/>; <c>null</c> when the server doesn't have it.</summary>
    /// <param name="url">The file's URL.</param>
    /// <param name="version">The file's version (e.g. its last-modified time) for the cache.</param>
    /// <param name="cancellationToken">Cancels waiting (the opening goes on for the next request).</param>
    public async Task<GeoTiff?> OpenAsync(string url, string? version, CancellationToken cancellationToken)
    {
        var lazy = _files.GetOrAdd(url, u => new Lazy<Task<GeoTiff?>>(() => Task.Run(() => TryOpenAsync(u, version))));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            // Try again next time (e.g. the network was down).
            _files.TryRemove(new KeyValuePair<string, Lazy<Task<GeoTiff?>>>(url, lazy));
            throw;
        }
    }

    /// <summary>Opens every file at once; the files the server doesn't have are left out.</summary>
    public async Task<List<GeoTiff>> OpenAllAsync(IEnumerable<(string Url, string? Version)> files, CancellationToken cancellationToken)
    {
        var opened = await Task.WhenAll(files.Select(f => OpenAsync(f.Url, f.Version, cancellationToken)));
        return opened.OfType<GeoTiff>().ToList();
    }

    private async Task<GeoTiff?> TryOpenAsync(string url, string? version)
    {
        try
        {
            return await GeoTiff.OpenAsync(cache.OpenRanges(url, version));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the coarsest image of <paramref name="tiff"/> whose pixels are at most <paramref name="spacing"/> on the
    /// ground (the full resolution when even that is coarser).
    /// </summary>
    /// <param name="tiff">The file.</param>
    /// <param name="spacing">The sample spacing (m).</param>
    /// <param name="metersPerUnit">The ground meters per unit of the file's coordinates (1 for projected systems).</param>
    public static int ChooseImage(GeoTiff tiff, double spacing, double metersPerUnit = 1)
    {
        int chosen = 0;
        for (int i = 1; i < tiff.Images.Count; i++)
        {
            var (w, h) = tiff.PixelSize(i);
            if (Math.Max(w, h) * metersPerUnit <= spacing * 1.01) chosen = i;
        }
        return chosen;
    }
}
