using System.Net;
using System.Net.Http.Headers;
using ParagliderToolbox.Terrain.Data;

namespace ParagliderToolbox.Tests.Terrain;

/// <summary>A server in memory: files by URL, with byte ranges, counting the requests.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<(string Url, RangeItemHeaderValue? Range)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        lock (Requests) Requests.Add((url, range));
        if (!Files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (range is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        long from = range.From ?? 0, to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
        if (from >= data.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from..(int)(to + 1)]) });
    }
}

public class DataCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ParagliderToolbox.Tests", "cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeServer _server = new();
    private readonly byte[] _file = Enumerable.Range(0, DataCache.ChunkSize * 3 + 1234).Select(i => (byte)(i * 31 % 251)).ToArray();

    public DataCacheTests() => _server.Files["https://data.test/big.tif"] = _file;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private DataCache Cache() => new(_folder, new HttpClient(_server));

    [Fact]
    public async Task Ranges_AreDownloadedInChunks_OnceAndThenReadFromDisk()
    {
        var cache = Cache();
        var reader = cache.OpenRanges("https://data.test/big.tif");
        var middle = await reader.ReadAsync(DataCache.ChunkSize - 10, 30);
        Assert.Equal(_file[(DataCache.ChunkSize - 10)..(DataCache.ChunkSize + 20)], middle);
        // Two missing chunks, one request.
        Assert.Single(_server.Requests);

        // A new cache on the same folder (the next session) needs no request for them.
        var again = await Cache().OpenRanges("https://data.test/big.tif").ReadAsync(DataCache.ChunkSize, 100);
        Assert.Equal(_file[DataCache.ChunkSize..(DataCache.ChunkSize + 100)], again);
        Assert.Single(_server.Requests);
        Assert.True(cache.BytesDownloaded >= 2 * DataCache.ChunkSize);
    }

    [Fact]
    public async Task Ranges_EndWithTheFile()
    {
        var reader = Cache().OpenRanges("https://data.test/big.tif");
        var tail = await reader.ReadAsync(_file.Length - 100, 1000);
        Assert.Equal(_file[^100..], tail);
        Assert.Empty(await reader.ReadAsync(_file.Length + 10, 10));
    }

    [Fact]
    public async Task MissingFiles_AreRemembered()
    {
        var reader = Cache().OpenRanges("https://data.test/sea.tif");
        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.ReadAsync(0, 100));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Cache().OpenRanges("https://data.test/sea.tif").ReadAsync(0, 100));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task WholeResources_AreCached_AndServedWhenOffline()
    {
        _server.Files["https://data.test/catalog.json"] = "{\"a\":1}"u8.ToArray();
        var cache = Cache();
        Assert.Equal("{\"a\":1}"u8.ToArray(), await cache.GetAsync("https://data.test/catalog.json", TimeSpan.FromDays(1)));
        Assert.Null(await cache.GetAsync("https://data.test/none.json", TimeSpan.FromDays(1)));
        int requests = _server.Requests.Count;

        var offline = Cache();
        offline.IsOffline = true;
        Assert.Equal("{\"a\":1}"u8.ToArray(), await offline.GetAsync("https://data.test/catalog.json", TimeSpan.Zero));
        Assert.Null(await offline.GetAsync("https://data.test/none.json", TimeSpan.Zero));
        await Assert.ThrowsAsync<HttpRequestException>(() => offline.OpenRanges("https://data.test/big.tif").ReadAsync(0, 10));
        Assert.Equal(requests, _server.Requests.Count);
        Assert.True(offline.GetSize() > 0);
        offline.Clear();
        Assert.Equal(0, offline.GetSize());
    }

    [Fact]
    public async Task GeoTiff_OpensThroughTheCache_ReadingOnlyWhatItNeeds()
    {
        var image = new TestImage(64, 64, 16, 16, 32, 3, 1, 8, 3, (tx, ty) =>
        {
            var values = new float[256];
            Array.Fill(values, tx * 10 + ty);
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        });
        _server.Files["https://data.test/dem.tif"] = TestTiff.Create([image], 0, 64, 1, 2056);
        var tiff = await GeoTiff.OpenAsync(Cache().OpenRanges("https://data.test/dem.tif"));
        var block = await tiff.ReadElevationBlockAsync(0, 3, 2);
        Assert.Equal(32, block[17]);
    }
}
