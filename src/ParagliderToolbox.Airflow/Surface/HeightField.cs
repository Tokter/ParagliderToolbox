using System.Numerics;

namespace ParagliderToolbox.Airflow.Surface;

/// <summary>
/// Ground heights on a regular grid in the terrain's frame (+X east, +Y up, +Z south): the terrain the airflow runs
/// over. A game passes its own heightmap; the toolbox samples its terrain model.
/// </summary>
/// <remarks>Samples go row by row from north (−Z) to south, each from west to east: <c>Heights[row * Columns + column]</c>.</remarks>
public sealed class HeightField
{
    /// <summary>Initializes a heightfield.</summary>
    /// <param name="x0">The first sample's X (m).</param>
    /// <param name="z0">The first sample's Z (m).</param>
    /// <param name="spacing">The distance between samples (m).</param>
    /// <param name="columns">The samples per row (at least 2).</param>
    /// <param name="rows">The rows (at least 2).</param>
    /// <param name="heights">The heights (m above sea level), <paramref name="columns"/> × <paramref name="rows"/>.</param>
    public HeightField(double x0, double z0, double spacing, int columns, int rows, float[] heights)
    {
        if (columns < 2 || rows < 2) throw new ArgumentException("A heightfield needs at least 2 × 2 samples.");
        if (heights.Length != columns * rows) throw new ArgumentException("The heights don't match the size.", nameof(heights));
        if (spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        X0 = x0;
        Z0 = z0;
        Spacing = spacing;
        Columns = columns;
        Rows = rows;
        Heights = heights;
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in heights)
        {
            min = Math.Min(min, h);
            max = Math.Max(max, h);
        }
        MinHeight = min;
        MaxHeight = max;
    }

    /// <summary>Gets the first sample's X (m).</summary>
    public double X0 { get; }

    /// <summary>Gets the first sample's Z (m).</summary>
    public double Z0 { get; }

    /// <summary>Gets the distance between samples (m).</summary>
    public double Spacing { get; }

    /// <summary>Gets the samples per row.</summary>
    public int Columns { get; }

    /// <summary>Gets the rows.</summary>
    public int Rows { get; }

    /// <summary>Gets the heights (m above sea level).</summary>
    public float[] Heights { get; }

    /// <summary>Gets the lowest height.</summary>
    public float MinHeight { get; }

    /// <summary>Gets the highest height.</summary>
    public float MaxHeight { get; }

    /// <summary>Gets the extent from west to east (m).</summary>
    public double Width => (Columns - 1) * Spacing;

    /// <summary>Gets the extent from north to south (m).</summary>
    public double Depth => (Rows - 1) * Spacing;

    /// <summary>Samples <paramref name="height"/> on a grid.</summary>
    public static HeightField Sample(double x0, double z0, double spacing, int columns, int rows, Func<double, double, float> height)
    {
        var heights = new float[columns * rows];
        Parallel.For(0, rows, row =>
        {
            for (int column = 0; column < columns; column++) heights[row * columns + column] = height(x0 + column * spacing, z0 + row * spacing);
        });
        return new HeightField(x0, z0, spacing, columns, rows, heights);
    }

    /// <summary>Gets the height at a point (bilinear; clamped to the edges).</summary>
    public float HeightAt(double x, double z)
    {
        double fx = Math.Clamp((x - X0) / Spacing, 0, Columns - 1), fz = Math.Clamp((z - Z0) / Spacing, 0, Rows - 1);
        int c = Math.Min((int)fx, Columns - 2), r = Math.Min((int)fz, Rows - 2);
        float tx = (float)(fx - c), tz = (float)(fz - r);
        int i = r * Columns + c;
        float top = Heights[i] + tx * (Heights[i + 1] - Heights[i]);
        float bottom = Heights[i + Columns] + tx * (Heights[i + Columns + 1] - Heights[i + Columns]);
        return top + tz * (bottom - top);
    }

    /// <summary>Gets the upward unit normal at sample (<paramref name="column"/>, <paramref name="row"/>), from central differences.</summary>
    public Vector3 NormalAt(int column, int row)
    {
        int c0 = Math.Max(column - 1, 0), c1 = Math.Min(column + 1, Columns - 1);
        int r0 = Math.Max(row - 1, 0), r1 = Math.Min(row + 1, Rows - 1);
        float dx = (Heights[row * Columns + c1] - Heights[row * Columns + c0]) / (float)((c1 - c0) * Spacing);
        float dz = (Heights[r1 * Columns + column] - Heights[r0 * Columns + column]) / (float)((r1 - r0) * Spacing);
        return Vector3.Normalize(new Vector3(-dx, 1, -dz));
    }

    /// <summary>Gets a coarser copy with samples <paramref name="spacing"/> apart over the same area (averaging the samples between).</summary>
    public HeightField Resample(double spacing)
    {
        int columns = Math.Max(2, (int)Math.Round(Width / spacing) + 1), rows = Math.Max(2, (int)Math.Round(Depth / spacing) + 1);
        double sx = Width / (columns - 1), sz = Depth / (rows - 1);
        int reach = Math.Max(0, (int)(spacing / Spacing / 2));
        var heights = new float[columns * rows];
        Parallel.For(0, rows, row =>
        {
            for (int column = 0; column < columns; column++)
            {
                double x = X0 + column * sx, z = Z0 + row * sz;
                if (reach == 0)
                {
                    heights[row * columns + column] = HeightAt(x, z);
                    continue;
                }
                int cc = (int)Math.Round((x - X0) / Spacing), rr = (int)Math.Round((z - Z0) / Spacing);
                double sum = 0;
                int n = 0;
                for (int b = Math.Max(rr - reach, 0); b <= Math.Min(rr + reach, Rows - 1); b++)
                {
                    for (int a = Math.Max(cc - reach, 0); a <= Math.Min(cc + reach, Columns - 1); a++)
                    {
                        sum += Heights[b * Columns + a];
                        n++;
                    }
                }
                heights[row * columns + column] = (float)(sum / n);
            }
        });
        return new HeightField(X0, Z0, Math.Min(sx, sz), columns, rows, heights);
    }
}
