namespace ParagliderToolbox.Terrain.Features;

/// <summary>
/// Binary masks to center lines: the Euclidean distance transform (how far each pixel is from the mask's edge),
/// morphology, connected parts, thinning to a one-pixel skeleton (Zhang–Suen; <see cref="SkeletonGraph"/> traces it) and
/// polyline simplification.
/// </summary>
public static class Skeletons
{
    /// <summary>
    /// The distance (pixels) from every pixel of the mask to the nearest pixel outside it (0 outside), exact (Felzenszwalb
    /// and Huttenlocher's lower envelope of parabolas, by rows then columns).
    /// </summary>
    public static float[] DistanceTransform(bool[] mask, int width, int height)
    {
        const float Infinity = 1e20f;
        var squared = new float[mask.Length];
        // Columns first: the squared distance to the nearest outside pixel in the column.
        Parallel.For(0, width, x =>
        {
            var f = new float[height];
            for (int y = 0; y < height; y++) f[y] = mask[y * width + x] ? Infinity : 0;
            var d = Envelope(f);
            for (int y = 0; y < height; y++) squared[y * width + x] = d[y];
        });
        var distance = new float[mask.Length];
        Parallel.For(0, height, y =>
        {
            var f = new float[width];
            Array.Copy(squared, y * width, f, 0, width);
            var d = Envelope(f);
            for (int x = 0; x < width; x++) distance[y * width + x] = MathF.Sqrt(d[x]);
        });
        return distance;
    }

    // The lower envelope of the parabolas (q − p)² + f(p): the 1D squared distance transform.
    private static float[] Envelope(float[] f)
    {
        int n = f.Length;
        var d = new float[n];
        var v = new int[n];
        var z = new float[n + 1];
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = (f[q] + q * q - (f[p] + p * p)) / (2f * q - 2f * p);
                if (s > z[k] || k == 0) break;
                k--;
            }
            if (s <= z[k] && k == 0)
            {
                v[0] = q;
                z[0] = float.NegativeInfinity;
                z[1] = float.PositiveInfinity;
                continue;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            int p = v[k];
            d[q] = (q - p) * (q - p) + f[p];
        }
        return d;
    }

    /// <summary>The pixels within <paramref name="radius"/> pixels of the mask (a disc).</summary>
    public static bool[] Dilate(bool[] mask, int width, int height, float radius)
    {
        var outside = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) outside[i] = !mask[i];
        var distance = DistanceTransform(outside, width, height);
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) result[i] = distance[i] <= radius;
        return result;
    }

    /// <summary>The pixels of the mask more than <paramref name="radius"/> pixels from its edge.</summary>
    public static bool[] Erode(bool[] mask, int width, int height, float radius)
    {
        var distance = DistanceTransform(mask, width, height);
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) result[i] = distance[i] > radius;
        return result;
    }

    /// <summary>Labels the 8-connected parts of a mask (−1 outside, 0, 1 … inside); returns the labels and each part's pixel count.</summary>
    public static (int[] Labels, List<int> Sizes) Components(bool[] mask, int width, int height)
    {
        var labels = new int[mask.Length];
        Array.Fill(labels, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] >= 0) continue;
            int label = sizes.Count, size = 0;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                size++;
                int x = i % width, y = i / width;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                    int j = yy * width + xx;
                    if (!mask[j] || labels[j] >= 0) continue;
                    labels[j] = label;
                    stack.Push(j);
                }
            }
            sizes.Add(size);
        }
        return (labels, sizes);
    }

    /// <summary>Fills the holes of a mask (parts outside it that don't reach the edge) smaller than <paramref name="maxPixels"/>, in place; returns it.</summary>
    public static bool[] FillHoles(bool[] mask, int width, int height, int maxPixels)
    {
        var outside = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) outside[i] = !mask[i];
        var (labels, sizes) = Components(outside, width, height);
        var touchesEdge = new bool[sizes.Count];
        for (int x = 0; x < width; x++)
        {
            if (labels[x] >= 0) touchesEdge[labels[x]] = true;
            if (labels[(height - 1) * width + x] >= 0) touchesEdge[labels[(height - 1) * width + x]] = true;
        }
        for (int y = 0; y < height; y++)
        {
            if (labels[y * width] >= 0) touchesEdge[labels[y * width]] = true;
            if (labels[y * width + width - 1] >= 0) touchesEdge[labels[y * width + width - 1]] = true;
        }
        for (int i = 0; i < mask.Length; i++)
        {
            int label = labels[i];
            if (label >= 0 && !touchesEdge[label] && sizes[label] < maxPixels) mask[i] = true;
        }
        return mask;
    }

    /// <summary>Thins a mask to a skeleton one pixel wide (Zhang–Suen), in place; returns it.</summary>
    public static bool[] Thin(bool[] mask, int width, int height)
    {
        var remove = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int pass = 0; pass < 2; pass++)
            {
                remove.Clear();
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        int i = y * width + x;
                        if (!mask[i]) continue;
                        // Neighbors clockwise from north: p2 … p9.
                        bool p2 = mask[i - width], p3 = mask[i - width + 1], p4 = mask[i + 1], p5 = mask[i + width + 1];
                        bool p6 = mask[i + width], p7 = mask[i + width - 1], p8 = mask[i - 1], p9 = mask[i - width - 1];
                        int count = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (count < 2 || count > 6) continue;
                        int transitions = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0) +
                                          (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                        if (transitions != 1) continue;
                        if (pass == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                        remove.Add(i);
                    }
                }
                foreach (int i in remove) mask[i] = false;
                changed |= remove.Count > 0;
            }
        }
        // Make it 8-connected thin: drop pixels whose removal keeps their neighbors connected (stair steps).
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (!mask[i]) continue;
                bool n = mask[i - width], e = mask[i + 1], s = mask[i + width], w = mask[i - 1];
                if ((n && e && !mask[i + width - 1] && !s && !w) || (e && s && !mask[i - width - 1] && !n && !w) ||
                    (s && w && !mask[i - width + 1] && !n && !e) || (w && n && !mask[i + width + 1] && !s && !e))
                {
                    mask[i] = false;
                }
            }
        }
        return mask;
    }

    /// <summary>Simplifies a polyline (Douglas–Peucker): drops points within <paramref name="tolerance"/> of the line through their neighbors kept.</summary>
    public static List<(double X, double Y)> Simplify(IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        if (points.Count < 3) return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = DistanceToSegment(points[i], points[a], points[b]);
                if (d > best) { best = d; index = i; }
            }
            if (index < 0 || best <= tolerance) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
        var result = new List<(double, double)>();
        for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static double DistanceToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length2 = dx * dx + dy * dy;
        double t = length2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0, 1) : 0;
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
