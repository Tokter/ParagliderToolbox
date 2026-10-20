namespace ParagliderToolbox.Terrain.Features;

/// <summary>A tree crown found in a raster: its top (pixel), height, crown radius and shape.</summary>
/// <param name="X">The column of the top.</param>
/// <param name="Y">The row of the top.</param>
/// <param name="Height">The top's height above the ground (m).</param>
/// <param name="CrownRadius">The radius of a circle as big as the crown (m).</param>
/// <param name="Peakedness">How steeply the crown falls off from its top: its top less its mean height, over its radius (conifers about 1 and more, broadleaf crowns about 0.3).</param>
public readonly record struct CrownFound(int X, int Y, float Height, float CrownRadius, float Peakedness);

/// <summary>
/// Finds individual trees in a canopy height model (the height of the vegetation above the ground): treetops are the
/// local maxima of the smoothed model within a window that grows with the height, crowns grow from them down the canopy
/// (a watershed) while it stays above half the top's height. Without a canopy model, <see cref="FindInImage"/> finds
/// crowns in the aerial image alone.
/// </summary>
public static class TreeFinder
{
    /// <summary>How much larger crowns are than the dark, shaded part <see cref="FindInImage"/> finds (measured against lidar crowns).</summary>
    public const float ImageCrownScale = 1.8f;

    /// <summary>
    /// Finds tree crowns in an aerial image: crowns are green and darker than open grass at the scale of a meter (their
    /// leaves shade each other) but not as dark as the cast shadows beside them; the parts of such pixels are split into
    /// crowns where they narrow (the distance to their edge, taken as a canopy, with <see cref="Find"/>). The radii are
    /// scaled by <see cref="ImageCrownScale"/>, the heights estimated from them (<see cref="EstimatedHeight"/>), the peakedness unknown (NaN).
    /// </summary>
    /// <param name="colors">The image (RGBA, red in the lowest byte).</param>
    /// <param name="covers">The ground cover of every pixel (buildings and water are no crowns; the open grass sets how bright the image is).</param>
    /// <param name="texture">How much the brightness varies within about a meter (smooth grass sets how bright the image is).</param>
    /// <param name="width">The pixels across.</param>
    /// <param name="height">The pixels down.</param>
    /// <param name="spacing">The pixel size (m).</param>
    public static List<CrownFound> FindInImage(uint[] colors, GroundCover[] covers, float[] texture, int width, int height, double spacing)
    {
        var luminance = Rasters.Luminance(colors);
        // How bright the image is: the median of open, smooth grass (aerial images differ by a factor of two).
        var grass = new List<float>();
        for (int i = 0; i < colors.Length; i += 7) if (covers[i] == GroundCover.Grass && texture[i] < 0.03f) grass.Add(luminance[i]);
        grass.Sort();
        float open = grass.Count > 200 ? grass[grass.Count / 2] : 0.4f;

        // Crown pixels: green, darker than open grass, but not the deep, gray cast shadow.
        var crownish = new float[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            uint c = colors[i];
            float r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF, sum = r + g + b + 1;
            bool green = (2 * g - r - b) / sum > 0.03f;
            crownish[i] = green && luminance[i] < 0.85f * open && luminance[i] > 0.35f * open ? 1 : 0;
        }
        var density = Rasters.BoxMean(crownish, width, height, Math.Max(1, (int)Math.Round(1 / spacing)));
        var mask = new bool[colors.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = density[i] > 0.5f && covers[i] is not (GroundCover.Building or GroundCover.Water or GroundCover.None);
        mask = Skeletons.Erode(Skeletons.Dilate(mask, width, height, 1), width, height, 1);

        // The distance to the crowns' edge (m) × 10 as a canopy: a crown of radius r is a top of 10·r, so Find's window
        // (0.6 + 0.07·h) is about the crown.
        var distance = Skeletons.DistanceTransform(mask, width, height);
        var canopy = new float[mask.Length];
        for (int i = 0; i < canopy.Length; i++) canopy[i] = (float)(10 * distance[i] * spacing);
        var crowns = Find(canopy, width, height, spacing, minHeight: 8);
        for (int i = 0; i < crowns.Count; i++)
        {
            float radius = crowns[i].CrownRadius * ImageCrownScale;
            crowns[i] = crowns[i] with { CrownRadius = radius, Height = EstimatedHeight(radius), Peakedness = float.NaN };
        }
        return crowns;
    }

    /// <summary>Estimates a tree's height from its crown's radius (m), where no surface model measures it: about 5.5 radii.</summary>
    public static float EstimatedHeight(float crownRadius) => Math.Clamp(1.5f + 5.5f * crownRadius, 2, 35);

    /// <summary>Finds the trees taller than <paramref name="minHeight"/> in <paramref name="canopy"/> (m above the ground, 0 where there is none).</summary>
    public static List<CrownFound> Find(float[] canopy, int width, int height, double spacing, float minHeight = 3)
    {
        var smooth = Rasters.Blur(canopy, width, height, (float)(0.5 / spacing));

        // Treetops: higher than everything within a window that grows with the tree.
        var tops = new List<(int X, int Y, float Height)>();
        var locks = new object();
        Parallel.For(0, height, y =>
        {
            var found = new List<(int, int, float)>();
            for (int x = 0; x < width; x++)
            {
                float h = smooth[y * width + x];
                if (h < minHeight) continue;
                int radius = Math.Max(1, (int)Math.Round((0.6 + 0.07 * h) / spacing));
                bool top = true;
                for (int dy = -radius; dy <= radius && top; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= height) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > radius * radius || (dx == 0 && dy == 0)) continue;
                        int xx = x + dx;
                        if (xx < 0 || xx >= width) continue;
                        float other = smooth[yy * width + xx];
                        // Ties go to the first pixel in reading order, so a flat top counts once.
                        if (other > h || (other == h && (dy < 0 || (dy == 0 && dx < 0)))) { top = false; break; }
                    }
                }
                if (top) found.Add((x, y, h));
            }
            lock (locks) tops.AddRange(found);
        });
        tops.Sort((a, b) => b.Height.CompareTo(a.Height));

        // Crowns: a watershed from the tops, down the smoothed canopy, while above half the top's height.
        var label = new int[width * height];
        Array.Fill(label, -1);
        var queue = new PriorityQueue<int, float>();
        for (int t = 0; t < tops.Count; t++)
        {
            int i = tops[t].Y * width + tops[t].X;
            label[i] = t;
            queue.Enqueue(i, -smooth[i]);
        }
        var area = new int[tops.Count];
        var sum = new double[tops.Count];
        while (queue.TryDequeue(out int i, out _))
        {
            int t = label[i];
            area[t]++;
            sum[t] += smooth[i];
            int x = i % width, y = i / width;
            float floor = MathF.Max(minHeight * 0.5f, tops[t].Height * 0.5f);
            for (int k = 0; k < 4; k++)
            {
                int xx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), yy = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                int j = yy * width + xx;
                if (label[j] >= 0 || smooth[j] < floor || smooth[j] > smooth[i] + 0.3f) continue;
                label[j] = t;
                queue.Enqueue(j, -smooth[j]);
            }
        }

        var crowns = new List<CrownFound>(tops.Count);
        for (int t = 0; t < tops.Count; t++)
        {
            float radius = (float)(Math.Sqrt(area[t] / Math.PI) * spacing);
            float mean = (float)(sum[t] / Math.Max(1, area[t]));
            float peak = canopy[tops[t].Y * width + tops[t].X];
            crowns.Add(new CrownFound(tops[t].X, tops[t].Y, MathF.Max(peak, tops[t].Height), radius, radius > 0 ? (tops[t].Height - mean) / radius : 0));
        }
        return crowns;
    }
}
