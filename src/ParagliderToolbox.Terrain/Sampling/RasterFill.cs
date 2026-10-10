using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Terrain.Sampling;

/// <summary>
/// Fills the empty samples of a request from a mosaic: projects the samples into the mosaic's coordinate system, loads
/// the blocks around them and interpolates. Data much finer than the samples is averaged over each sample's cell (up to
/// 4 × 4 points), so coarse levels of detail don't alias.
/// </summary>
public static class RasterFill
{
    /// <summary>Fills the <see cref="float.NaN"/> heights where the mosaic has data.</summary>
    /// <param name="request">The samples.</param>
    /// <param name="heights">The heights, one per sample; only NaN entries are written.</param>
    /// <param name="system">The mosaic's coordinate system.</param>
    /// <param name="mosaic">The rasters.</param>
    /// <param name="pixelMeters">The size of the rasters' pixels on the ground (m).</param>
    /// <param name="cache">The block cache.</param>
    /// <param name="cancellationToken">Cancels the fill.</param>
    /// <returns>The number of samples filled.</returns>
    public static async Task<int> FillHeightsAsync(SampleRequest request, float[] heights, ICoordinateSystem system, RasterMosaic<float> mosaic,
        double pixelMeters, BlockCache<float> cache, CancellationToken cancellationToken)
    {
        var (xs, ys) = request.Project(system);
        var plan = Plan(request, xs, ys, i => float.IsNaN(heights[i]), pixelMeters);
        if (plan is null) return 0;
        var view = await mosaic.LoadAsync(plan.MinX, plan.MinY, plan.MaxX, plan.MaxY, cache, cancellationToken);
        int filled = 0, hint = -1;
        int m = plan.Supersampling;
        for (int i = 0; i < heights.Length; i++)
        {
            if (!float.IsNaN(heights[i])) continue;
            float value;
            if (m == 1)
            {
                value = MosaicSampling.Height(view, xs[i], ys[i], ref hint);
            }
            else
            {
                double sum = 0;
                int count = 0;
                for (int b = 0; b < m; b++)
                {
                    for (int a = 0; a < m; a++)
                    {
                        double u = (a + 0.5) / m - 0.5, v = (b + 0.5) / m - 0.5;
                        float h = MosaicSampling.Height(view, xs[i] + u * plan.EastX + v * plan.SouthX, ys[i] + u * plan.EastY + v * plan.SouthY, ref hint);
                        if (float.IsNaN(h)) continue;
                        sum += h;
                        count++;
                    }
                }
                value = count * 2 >= m * m ? (float)(sum / count) : float.NaN;
            }
            if (float.IsNaN(value)) continue;
            heights[i] = value;
            filled++;
        }
        return filled;
    }

    /// <summary>Fills the empty colors (0) where the mosaic has data; see <see cref="FillHeightsAsync"/>.</summary>
    public static async Task<int> FillColorsAsync(SampleRequest request, uint[] colors, ICoordinateSystem system, RasterMosaic<uint> mosaic,
        double pixelMeters, BlockCache<uint> cache, CancellationToken cancellationToken)
    {
        var (xs, ys) = request.Project(system);
        var plan = Plan(request, xs, ys, i => colors[i] >> 24 == 0, pixelMeters);
        if (plan is null) return 0;
        var view = await mosaic.LoadAsync(plan.MinX, plan.MinY, plan.MaxX, plan.MaxY, cache, cancellationToken);
        int filled = 0, hint = -1;
        int m = plan.Supersampling;
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i] >> 24 != 0) continue;
            uint value;
            if (m == 1)
            {
                value = MosaicSampling.Color(view, xs[i], ys[i], ref hint);
            }
            else
            {
                double r = 0, g = 0, bl = 0;
                int count = 0;
                for (int b = 0; b < m; b++)
                {
                    for (int a = 0; a < m; a++)
                    {
                        double u = (a + 0.5) / m - 0.5, v = (b + 0.5) / m - 0.5;
                        uint c = MosaicSampling.Color(view, xs[i] + u * plan.EastX + v * plan.SouthX, ys[i] + u * plan.EastY + v * plan.SouthY, ref hint);
                        if (c >> 24 == 0) continue;
                        r += c & 0xFF;
                        g += (c >> 8) & 0xFF;
                        bl += (c >> 16) & 0xFF;
                        count++;
                    }
                }
                value = count * 2 >= m * m ? MosaicSampling.Pack(r / count, g / count, bl / count) : 0;
            }
            if (value >> 24 == 0) continue;
            colors[i] = value;
            filled++;
        }
        return filled;
    }

    private sealed record FillPlan(double MinX, double MinY, double MaxX, double MaxY, int Supersampling, double EastX, double EastY, double SouthX, double SouthY);

    // The box around the samples to fill (and their cells), and how many points per side average each sample.
    private static FillPlan? Plan(SampleRequest request, double[] xs, double[] ys, Func<int, bool> isEmpty, double pixelMeters)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        for (int i = 0; i < xs.Length; i++)
        {
            if (!isEmpty(i)) continue;
            any = true;
            minX = Math.Min(minX, xs[i]);
            maxX = Math.Max(maxX, xs[i]);
            minY = Math.Min(minY, ys[i]);
            maxY = Math.Max(maxY, ys[i]);
        }
        if (!any) return null;

        // One sample's steps east and south in the system's units (the grid is nearly uniform there).
        int last = request.Columns - 1, bottom = (request.Rows - 1) * request.Columns;
        double eastX = request.Columns > 1 ? (xs[last] - xs[0]) / last : 0, eastY = request.Columns > 1 ? (ys[last] - ys[0]) / last : 0;
        double southX = request.Rows > 1 ? (xs[bottom] - xs[0]) / (request.Rows - 1) : 0, southY = request.Rows > 1 ? (ys[bottom] - ys[0]) / (request.Rows - 1) : 0;
        // A single row or column has no cells to average over.
        int m = pixelMeters > 0 && request.Columns > 1 && request.Rows > 1 ? Math.Clamp((int)Math.Round(request.Spacing / pixelMeters), 1, 4) : 1;
        double marginX = (Math.Abs(eastX) + Math.Abs(southX)) / 2, marginY = (Math.Abs(eastY) + Math.Abs(southY)) / 2;
        return new FillPlan(minX - marginX, minY - marginY, maxX + marginX, maxY + marginY, m, eastX, eastY, southX, southY);
    }
}
