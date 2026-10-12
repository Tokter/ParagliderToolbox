using System.Text;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Tests.Terrain;

public class UnitedStatesSourcesTests : IDisposable
{
    // A point in Sonoma County, in UTM zone 10, in the 0.1° cell from 122.6° W, 38.4° N.
    private static readonly GeoPoint s_point = new(38.43, -122.55);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ParagliderToolbox.Tests", "us-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeServer _server = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private DataCache Cache() => new(_folder, new HttpClient(_server));

    private void Serve(string url, byte[] data) => _server.Files[new Uri(url).ToString()] = data;

    private static void Serve(FakeServer server, string url, string json) => server.Files[new Uri(url).ToString()] = Encoding.UTF8.GetBytes(json);

    // An 80 m square of samples 8 m apart around the point.
    private static SampleRequest Request() => new(new LocalFrame(s_point), -40, -40, 8, 11, 11);

    // A 256 m square of 1 m float pixels around the point in UTM zone 10 (NAD83), uncompressed, 64 px tiles.
    private static byte[] Heights(Func<int, float> byColumn)
    {
        var (e, n) = TransverseMercator.Utm(10).Project(s_point);
        var image = new TestImage(256, 256, 64, 64, 32, 3, 1, 1, 1, (tx, ty) =>
        {
            var bytes = new byte[64 * 64 * 4];
            for (int r = 0; r < 64; r++)
            {
                for (int c = 0; c < 64; c++) BitConverter.TryWriteBytes(bytes.AsSpan((r * 64 + c) * 4), byColumn(tx * 64 + c));
            }
            return bytes;
        });
        return TestTiff.Create([image], Math.Round(e) - 128, Math.Round(n) + 128, 1, 26910, noData: "-999999");
    }

    [Fact]
    public async Task TheOneMeterLidar_ReadsTheNewestProjectFirst_AndOlderOnesFillItsGaps()
    {
        // The new project has the east half (the west is no data), the old one everything.
        const string newer = "https://prd-tnm.test/1m/Projects/CA_New_2024/TIFF/USGS_1M_10_x54y425_CA_New_2024.tif";
        const string older = "https://prd-tnm.test/1m/Projects/CA_Old_2010/TIFF/USGS_1M_10_x54y425_CA_Old_2010.tif";
        Serve(newer, Heights(c => c >= 128 ? 100 : -999999));
        Serve(older, Heights(_ => 50));
        string Item(string url, string date) => $$"""
            { "downloadURL": "{{url}}", "publicationDate": "{{date}}", "lastUpdated": "{{date}}T10:00:00",
              "boundingBox": { "minX": -122.56, "maxX": -122.54, "minY": 38.42, "maxY": 38.44 } }
            """;
        Serve(_server, NationalMapCatalog.CellUrl(-1226, 384, 0), $$"""{ "total": 2, "items": [ {{Item(older, "2011-05-01")}}, {{Item(newer, "2024-03-01")}} ] }""");

        var cache = Cache();
        var source = new Usgs3Dep1MeterSource(new CogFiles(cache), new NationalMapCatalog(cache));
        Assert.True(source.Covers(Request().Bounds));
        Assert.False(source.Covers(new GeoBounds(46.6, 7.8, 46.7, 7.9)));
        var request = Request();
        var heights = new float[request.Count];
        Array.Fill(heights, float.NaN);

        int filled = await source.FillAsync(request, heights, default);

        Assert.Equal(request.Count, filled);
        // West of the point the old project, east the new one.
        Assert.Equal(50, heights[5 * 11 + 0], 3);
        Assert.Equal(100, heights[5 * 11 + 10], 3);
    }

    [Fact]
    public async Task TheThirdArcSecondModel_ReadsTheTileNamedByItsNorthWestCorner()
    {
        Assert.EndsWith("/n39w123/USGS_13_n39w123.tif", Usgs3Dep13Source.TileUrl(39, 123));
        // 1/3″ pixels (NAD83) around the point.
        var image = new TestImage(256, 256, 64, 64, 32, 3, 1, 1, 1, (_, _) =>
        {
            var bytes = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), 321f);
            return bytes;
        });
        Serve(Usgs3Dep13Source.TileUrl(39, 123), TestTiff.Create([image], -122.56, 38.44, 1 / 10800.0, 4269));

        var source = new Usgs3Dep13Source(new CogFiles(Cache()));
        var request = Request();
        var heights = new float[request.Count];
        Array.Fill(heights, float.NaN);
        Assert.Equal(request.Count, await source.FillAsync(request, heights, default));
        Assert.All(heights, h => Assert.Equal(321, h, 3));
    }

    [Fact]
    public async Task Naip_TakesTheNewestImageOfEachQuarterQuad_AndItsBlackCornersAreNoImage()
    {
        // The 2022 image of the quarter quad has the west half (black east), its neighbor the east half (black west);
        // the 2020 image of the first, all gray, is older.
        var (e, n) = TransverseMercator.Utm(10).Project(s_point);
        byte[] Image(Func<int, (byte R, byte G, byte B)> byColumn)
        {
            var image = new TestImage(256, 256, 64, 64, 8, 1, 4, 8, 2, (tx, _) =>
            {
                var bytes = new byte[64 * 64 * 4];
                for (int r = 0; r < 64; r++)
                {
                    for (int c = 0; c < 64; c++)
                    {
                        var (red, green, blue) = byColumn(tx * 64 + c);
                        int at = (r * 64 + c) * 4;
                        (bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]) = (red, green, blue, 77);
                    }
                }
                return bytes;
            }, Photometric: 2);
            // The fourth sample is near-infrared, not alpha.
            return TestTiff.Create([image], Math.Round(e) - 128 * 0.6, Math.Round(n) + 128 * 0.6, 0.6, 26910, extraSamples: 0);
        }
        const string west = "https://naip.test/ca/2022/m_3812237_sw_10_060_20220524.tif";
        const string old = "https://naip.test/ca/2020/m_3812237_sw_10_060_20200528.tif";
        const string east = "https://naip.test/ca/2022/m_3812237_nw_10_060_20220524.tif";
        Serve(west, Image(c => c < 128 ? ((byte)10, (byte)200, (byte)30) : ((byte)0, (byte)0, (byte)0)));
        Serve(old, Image(_ => ((byte)100, (byte)100, (byte)100)));
        Serve(east, Image(c => c >= 128 ? ((byte)200, (byte)10, (byte)30) : ((byte)0, (byte)0, (byte)0)));
        string Feature(string url, string year, string box) => $$"""
            { "id": "{{Path.GetFileNameWithoutExtension(url)}}", "bbox": [{{box}}], "properties": { "naip:year": "{{year}}", "gsd": 0.6 },
              "assets": { "image": { "href": "{{url}}" } } }
            """;
        // Their boxes: the neighbor's center is a quarter quad north (the other checkerboard square).
        Serve(_server, NaipCatalog.CellUrl(-1226, 384), $$"""
            { "type": "FeatureCollection", "features": [ {{Feature(west, "2022", "-122.56, 38.40, -122.54, 38.44")}},
              {{Feature(old, "2020", "-122.56, 38.40, -122.54, 38.44")}}, {{Feature(east, "2022", "-122.56, 38.42, -122.54, 38.48")}} ], "links": [] }
            """);

        var cache = Cache();
        var source = new NaipSource(new CogFiles(cache), new NaipCatalog(cache));
        var request = Request();
        var colors = new uint[request.Count];

        int filled = await source.FillAsync(request, colors, default);

        Assert.Equal(request.Count, filled);
        Assert.Equal(0xFF1EC80Au, colors[5 * 11 + 0]);
        Assert.Equal(0xFF1E0AC8u, colors[5 * 11 + 10]);
        Assert.DoesNotContain(_server.Requests, r => r.Url == old);
    }

    [Fact]
    public void EpsgCodes_GiveTheirCoordinateSystems()
    {
        Assert.Equal(-123, Assert.IsType<TransverseMercator>(EpsgSystems.Find(26910)).CentralMeridian);
        Assert.Equal(-117, Assert.IsType<TransverseMercator>(EpsgSystems.Find(32611)).CentralMeridian);
        Assert.Same(GeographicCoordinates.Instance, EpsgSystems.Find(4269));
        Assert.Same(SwissGrid.Instance, EpsgSystems.Find(2056));
        Assert.Null(EpsgSystems.Find(9999));
        Assert.Null(EpsgSystems.Find(null));
        // A UTM point and back.
        var (x, y) = EpsgSystems.Find(26910)!.Project(s_point);
        var back = EpsgSystems.Find(26910)!.Unproject(x, y);
        Assert.Equal(s_point.Latitude, back.Latitude, 6);
        Assert.Equal(s_point.Longitude, back.Longitude, 6);
        Assert.InRange(x, 500_000, 600_000);
    }
}
