namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>Helpers for maps of the grid's columns (<c>k * nx + i</c>).</summary>
internal static class Maps
{
    /// <summary>Smooths a map with three box blurs of <paramref name="radius"/> columns (close to a Gaussian), shrinking the boxes at the edges.</summary>
    public static float[] Smooth(float[] map, int nx, int nz, int radius)
    {
        if (radius <= 0) return (float[])map.Clone();
        var a = (float[])map.Clone();
        var b = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Parallel.For(0, nz, k =>
            {
                for (int i = 0; i < nx; i++)
                {
                    double sum = 0;
                    int lo = Math.Max(0, i - radius), hi = Math.Min(nx - 1, i + radius);
                    for (int x = lo; x <= hi; x++) sum += a[k * nx + x];
                    b[k * nx + i] = (float)(sum / (hi - lo + 1));
                }
            });
            Parallel.For(0, nx, i =>
            {
                for (int k = 0; k < nz; k++)
                {
                    double sum = 0;
                    int lo = Math.Max(0, k - radius), hi = Math.Min(nz - 1, k + radius);
                    for (int z = lo; z <= hi; z++) sum += b[z * nx + i];
                    a[k * nx + i] = (float)(sum / (hi - lo + 1));
                }
            });
        }
        return a;
    }

    /// <summary>Returns whether column (i, k) is the largest within <paramref name="radius"/> (ties go to the first).</summary>
    public static bool IsPeak(float[] map, int nx, int nz, int i, int k, int radius)
    {
        float value = map[k * nx + i];
        for (int b = Math.Max(0, k - radius); b <= Math.Min(nz - 1, k + radius); b++)
        {
            for (int a = Math.Max(0, i - radius); a <= Math.Min(nx - 1, i + radius); a++)
            {
                if (a == i && b == k) continue;
                float other = map[b * nx + a];
                if (other > value || (other == value && (b < k || (b == k && a < i)))) return false;
            }
        }
        return true;
    }
}
