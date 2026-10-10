using System.Runtime.InteropServices;
using ParagliderToolbox.Terrain.Data;
using SkiaSharp;

namespace ParagliderToolbox.Tests.Terrain;

public class GeoTiffTests
{
    // A height field of 20 × 12 pixels: h = 100 + column + 1000 × row.
    private static float Height(int column, int row) => 100 + column + 1000 * row;

    private static byte[] FloatTile(int tx, int ty, int tileWidth, int tileHeight, int width, int height)
    {
        var values = new float[tileWidth * tileHeight];
        for (int y = 0; y < tileHeight; y++)
        {
            for (int x = 0; x < tileWidth; x++)
            {
                int c = tx * tileWidth + x, r = ty * tileHeight + y;
                values[y * tileWidth + x] = c < width && r < height ? Height(c, r) : -9999;
            }
        }
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    [Theory]
    [InlineData(8, 3)]
    [InlineData(5, 1)]
    [InlineData(1, 1)]
    public async Task ReadsFloatTiles_WithEveryCompressionAndPredictor(int compression, int predictor)
    {
        var image = new TestImage(20, 12, 16, 16, 32, 3, 1, compression, predictor, (tx, ty) => FloatTile(tx, ty, 16, 16, 20, 12));
        var file = TestTiff.Create([image], 2632000, 1170000, 0.5, 2056, noData: "-9999");
        var tiff = await GeoTiff.OpenAsync(new MemoryRangeReader(file));

        Assert.Equal(2056, tiff.Epsg);
        Assert.Equal(-9999, tiff.NoData);
        Assert.Equal((2632000.25, 1169999.75), tiff.FirstPixelCenter(0));
        var info = Assert.Single(tiff.Images);
        Assert.Equal((2, 1), (info.BlocksAcross, info.BlocksDown));

        var block = await tiff.ReadElevationBlockAsync(0, 1, 0);
        // Column 16 + 3, row 5 of the image is pixel (3, 5) of the second tile.
        Assert.Equal(Height(19, 5), block[5 * 16 + 3]);
        // Beyond the image: the padding's no-data.
        Assert.True(float.IsNaN(block[5 * 16 + 4]));
    }

    [Fact]
    public async Task ReadsIntegerTiles_WithTheHorizontalPredictor()
    {
        byte[] Tile(int tx, int ty)
        {
            var values = new ushort[16 * 16];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)(30000 + i * 37 % 1000);
            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        }
        var file = TestTiff.Create([new TestImage(16, 16, 16, 16, 16, 1, 1, 5, 2, Tile)], 0, 0, 1, 32632);
        var tiff = await GeoTiff.OpenAsync(new MemoryRangeReader(file));
        var block = await tiff.ReadElevationBlockAsync(0, 0, 0);
        for (int i = 0; i < 256; i++) Assert.Equal(30000 + i * 37 % 1000, block[i]);
        Assert.Equal(32632, tiff.Epsg);
    }

    [Fact]
    public async Task ShiftsPixelIsPointRasters_ByHalfAPixel()
    {
        double step = 1 / 3600.0;
        var image = new TestImage(16, 16, 16, 16, 32, 3, 1, 8, 3, (tx, ty) => FloatTile(tx, ty, 16, 16, 16, 16));
        var file = TestTiff.Create([image], 7, 47, step, 4326, pixelIsPoint: true);
        var tiff = await GeoTiff.OpenAsync(new MemoryRangeReader(file));
        var (x, y) = tiff.FirstPixelCenter(0);
        Assert.Equal(7, x, 12);
        Assert.Equal(47, y, 12);
        Assert.Equal(4326, tiff.Epsg);
    }

    [Fact]
    public async Task ListsTheOverviews_FromFineToCoarse_WithTheirPixelSizes()
    {
        var full = new TestImage(32, 32, 16, 16, 32, 3, 1, 8, 3, (tx, ty) => FloatTile(tx, ty, 16, 16, 32, 32));
        var quarter = full with { Width = 8, Height = 8 };
        var half = full with { Width = 16, Height = 16 };
        var file = TestTiff.Create([full, quarter, half], 0, 100, 2, 2056);
        var tiff = await GeoTiff.OpenAsync(new MemoryRangeReader(file));
        Assert.Equal(new[] { 32, 16, 8 }, tiff.Images.Select(i => i.Width));
        Assert.Equal((4, 4), tiff.PixelSize(1));
        Assert.Equal((2, 98), tiff.FirstPixelCenter(1));
    }

    [Fact]
    public async Task ReadsJpegTiles_AsColors()
    {
        byte[] Jpeg(int tx, int ty)
        {
            using var bitmap = new SKBitmap(16, 16);
            bitmap.Erase(new SKColor(200, 60, 30));
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            return data.ToArray();
        }
        var file = TestTiff.Create([new TestImage(16, 16, 16, 16, 8, 1, 3, 7, 1, Jpeg, Photometric: 2)], 0, 0, 1, 2056);
        var tiff = await GeoTiff.OpenAsync(new MemoryRangeReader(file));
        var block = await tiff.ReadColorBlockAsync(0, 0, 0);
        uint c = block[8 * 16 + 8];
        Assert.InRange((int)(c & 0xFF), 190, 210);
        Assert.InRange((int)((c >> 8) & 0xFF), 50, 70);
        Assert.InRange((int)((c >> 16) & 0xFF), 20, 40);
        Assert.Equal(255u, c >> 24);
    }

    [Fact]
    public void Lzw_DecodesWhatTiffEncodes_AcrossCodeWidths()
    {
        var random = new Random(7);
        var data = new byte[20000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(random.Next(12) + (i / 500 % 3) * 40);
        var decoded = new byte[data.Length];
        int written = Codecs.DecodeLzw(TestTiff.EncodeLzw(data), decoded);
        Assert.Equal(data.Length, written);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void PackBits_DecodesRunsAndLiterals()
    {
        byte[] encoded = [0xFE, 0xAA, 0x02, 0x80, 0x00, 0x2A, 0xFD, 0xAA, 0x03, 0x80, 0x00, 0x2A, 0x22];
        var decoded = new byte[15];
        Codecs.DecodePackBits(encoded, decoded);
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0x22, 0 }, decoded);
    }
}
