using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sampling;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Tests.Terrain;

/// <summary>A raster in memory whose pixels follow a function of their center's coordinates.</summary>
internal sealed class FunctionLevel(string key, double x0, double y0, double pixel, int width, int height, int block, Func<double, double, float> value)
    : RasterLevel<float>(x0, y0, pixel, pixel, width, height, block, block)
{
    public int Loads { get; private set; }

    public override string Key => key;

    public override Task<float[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken)
    {
        Loads++;
        var pixels = new float[BlockWidth * BlockHeight];
        for (int y = 0; y < BlockHeight; y++)
        {
            for (int x = 0; x < BlockWidth; x++)
            {
                int c = blockX * BlockWidth + x, r = blockY * BlockHeight + y;
                pixels[y * BlockWidth + x] = value(X0 + c * Dx, Y0 - r * Dy);
            }
        }
        return Task.FromResult<float[]?>(pixels);
    }
}

public class SamplingTests
{
    private static float Plane(double x, double y) => (float)(10 + 0.5 * x - 0.25 * y);

    [Fact]
    public async Task Mosaic_InterpolatesAcrossTheEdgeBetweenTwoRasters()
    {
        // Two 10 × 10 rasters of 1 m pixels side by side: x from 0 to 20, y from 10 down to 0.
        var west = new FunctionLevel("west", 0.5, 9.5, 1, 10, 10, 4, Plane);
        var east = new FunctionLevel("east", 10.5, 9.5, 1, 10, 10, 4, Plane);
        var mosaic = new RasterMosaic<float>([west, east]);
        var view = await mosaic.LoadAsync(8, 2, 12, 8, new BlockCache<float>(), default);
        int hint = -1;
        // Between the west raster's last pixel center (9.5) and the east one's first (10.5): a plane interpolates exactly.
        foreach (double x in new[] { 9.6, 10.0, 10.4 })
        {
            Assert.Equal(Plane(x, 5.3), MosaicSampling.Height(view, x, 5.3, ref hint), 4);
        }
        Assert.True(float.IsNaN(MosaicSampling.Height(view, 25, 5, ref hint)));
    }

    [Fact]
    public async Task BlockCache_LoadsEachBlockOnce()
    {
        var level = new FunctionLevel("cached", 0.5, 9.5, 1, 10, 10, 4, Plane);
        var cache = new BlockCache<float>();
        await new RasterMosaic<float>([level]).LoadAsync(0, 0, 10, 10, cache, default);
        await new RasterMosaic<float>([level]).LoadAsync(0, 0, 10, 10, cache, default);
        Assert.Equal(9, level.Loads);
    }

    [Fact]
    public void SampleRequest_ProjectsLikeTheExactProjection()
    {
        var frame = new LocalFrame(new GeoPoint(46.6863, 7.8632));
        var request = new SampleRequest(frame, -520, -520, 4, 259, 259);
        var (es, ns) = request.Project(SwissGrid.Instance);
        foreach (var (column, row) in new[] { (0, 0), (5, 3), (130, 77), (258, 258), (257, 1) })
        {
            var (e, n) = SwissGrid.ToLv95(request.Point(column, row));
            Assert.Equal(e, es[row * 259 + column], 3);
            Assert.Equal(n, ns[row * 259 + column], 3);
        }
    }

    [Fact]
    public async Task RasterFill_FillsOnlyTheEmptySamples_AndAveragesFinerData()
    {
        var frame = new LocalFrame(new GeoPoint(46.6863, 7.8632));
        var request = new SampleRequest(frame, -40, -40, 8, 11, 11);
        // A local metric raster in the frame's own projection (x east, y north): 1 m pixels, value = x.
        var system = frame.Projection;
        var level = new FunctionLevel("local", -59.5, 59.5, 1, 120, 120, 32, (x, _) => (float)x);
        var heights = new float[request.Count];
        Array.Fill(heights, float.NaN);
        heights[0] = 1234;
        int filled = await RasterFill.FillHeightsAsync(request, heights, system, new RasterMosaic<float>([level]), 1, new BlockCache<float>(), default);

        Assert.Equal(request.Count - 1, filled);
        Assert.Equal(1234, heights[0]);
        // The middle sample at x = 0; the average of a linear function over its cell is its value.
        Assert.Equal(0, heights[5 * 11 + 5], 2);
        Assert.Equal(16, heights[5 * 11 + 7], 2);
    }
}
