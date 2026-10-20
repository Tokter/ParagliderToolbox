using System.Numerics;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>A polygon traced from a mask: its outline and holes, in pixel corner coordinates (x right, y down).</summary>
/// <param name="Outline">The outline, counterclockwise on screen (x right, y down), not closed.</param>
/// <param name="Holes">The holes, clockwise on screen.</param>
public sealed record TracedPolygon(List<Vector2> Outline, List<List<Vector2>> Holes);

/// <summary>A rectangle turned in the plane.</summary>
/// <param name="Center">Its center.</param>
/// <param name="Length">Its long side.</param>
/// <param name="Width">Its short side.</param>
/// <param name="Direction">The unit direction of its long side.</param>
public readonly record struct OrientedRectangle(Vector2 Center, float Length, float Width, Vector2 Direction);

/// <summary>
/// Polygons of masks: outlines traced along the pixels' edges (8-connected parts, with their holes), their areas,
/// simplification, convex hulls and the smallest rectangle around them.
/// </summary>
/// <remarks>
/// Coordinates are those of an image (x right, y down), which is the map seen from above with north up when x is east
/// and y south: "counterclockwise on screen" is counterclockwise seen from above, and such a ring's
/// <see cref="SignedArea"/> is negative.
/// </remarks>
public static class Polygons
{
    // Directions on screen: right, down, left, up.
    private static readonly (int X, int Y)[] s_steps = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Traces the outlines of the 8-connected parts of a mask along the pixels' edges, with their holes; the corners of
    /// pixel (x, y) are (x, y) … (x + 1, y + 1). Points where the outline runs straight on are left out.
    /// </summary>
    public static List<TracedPolygon> Trace(bool[] mask, int width, int height)
    {
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x];

        // The edges between inside and outside pixels, from each corner (two at most: at a corner where two pixels touch
        // diagonally), directed so the inside is on the left (counterclockwise around a part on screen).
        int stride = width + 1;
        var first = new sbyte[stride * (height + 1)];
        var second = new sbyte[first.Length];
        Array.Fill(first, (sbyte)-1);
        Array.Fill(second, (sbyte)-1);
        void Add(int x, int y, int direction)
        {
            int v = y * stride + x;
            if (first[v] < 0) first[v] = (sbyte)direction;
            else second[v] = (sbyte)direction;
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[y * width + x]) continue;
                if (!Inside(x, y - 1)) Add(x + 1, y, 2);
                if (!Inside(x - 1, y)) Add(x, y, 1);
                if (!Inside(x, y + 1)) Add(x, y + 1, 0);
                if (!Inside(x + 1, y)) Add(x + 1, y + 1, 3);
            }
        }

        var outlines = new List<List<Vector2>>();
        var holes = new List<List<Vector2>>();
        // Rings start where a single edge leaves a corner (and end when none is left there); then, should any edges be
        // left (rings whose every corner touches diagonal pixels), anywhere, ending back at the start.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int start = 0; start < first.Length; start++)
            {
                while (pass == 0 ? first[start] >= 0 && second[start] < 0 : first[start] >= 0)
                {
                    var ring = TraceFrom(start, pass == 1);
                    if (ring.Count < 3) continue;
                    if (SignedArea(ring) < 0) outlines.Add(ring);
                    else holes.Add(ring);
                }
            }
        }

        var polygons = outlines.Select(o => new TracedPolygon(o, [])).ToList();
        var areas = outlines.Select(o => -SignedArea(o)).ToArray();
        var boxes = outlines.Select(Box).ToArray();
        foreach (var ring in holes)
        {
            // The pixel right of the hole's first edge is in the hole (the mask is on the left): the smallest outline
            // around it has the hole.
            var a = ring[0];
            var (sx, sy) = s_steps[DirectionBetween(a, ring[1])];
            var probe = new Vector2(a.X + 0.5f * sx - 0.5f * sy, a.Y + 0.5f * sy + 0.5f * sx);
            int best = -1;
            for (int i = 0; i < polygons.Count; i++)
            {
                var box = boxes[i];
                if (probe.X < box.Min.X || probe.X > box.Max.X || probe.Y < box.Min.Y || probe.Y > box.Max.Y) continue;
                if ((best < 0 || areas[i] < areas[best]) && Contains(polygons[i].Outline, probe)) best = i;
            }
            if (best >= 0) polygons[best].Holes.Add(ring);
        }
        return polygons;

        List<Vector2> TraceFrom(int start, bool endAtStart)
        {
            var ring = new List<Vector2>();
            int x = start % stride, y = start / stride;
            int direction = Take(start, -1), previous = -1;
            while (direction >= 0)
            {
                if (direction != previous) ring.Add(new Vector2(x, y));
                previous = direction;
                x += s_steps[direction].X;
                y += s_steps[direction].Y;
                int v = y * stride + x;
                if (endAtStart && v == start) break;
                direction = Take(v, direction);
            }
            // The start is a corner only if the ring turns there.
            if (ring.Count > 1 && previous == DirectionBetween(ring[0], ring[1])) ring.RemoveAt(0);
            return ring;
        }

        // Takes the edge leaving corner v after arriving in direction `arrived` (−1 at the start): where two leave, the
        // right turn, which keeps diagonal neighbors in one part.
        int Take(int v, int arrived)
        {
            int a = first[v], b = second[v];
            if (a < 0) return -1;
            if (b >= 0 && arrived >= 0 && b == (arrived + 1) % 4)
            {
                second[v] = -1;
                return b;
            }
            first[v] = second[v];
            second[v] = -1;
            return a;
        }
    }

    private static int DirectionBetween(Vector2 a, Vector2 b)
    {
        var d = b - a;
        return Math.Abs(d.X) >= Math.Abs(d.Y) ? (d.X > 0 ? 0 : 2) : (d.Y > 0 ? 1 : 3);
    }

    private static (Vector2 Min, Vector2 Max) Box(IReadOnlyList<Vector2> ring)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in ring)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>Gets the shoelace area of a ring (not closed): negative when it runs counterclockwise on screen (x right, y down).</summary>
    public static double SignedArea(IReadOnlyList<Vector2> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>Gets whether <paramref name="point"/> is inside a ring (even–odd rule).</summary>
    public static bool Contains(IReadOnlyList<Vector2> ring, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Simplifies a ring (Douglas–Peucker, split at the point farthest from its first): drops points within <paramref name="tolerance"/>.</summary>
    public static List<Vector2> SimplifyRing(IReadOnlyList<Vector2> ring, double tolerance)
    {
        if (ring.Count <= 4) return [.. ring];
        int far = 0;
        float best = -1;
        for (int i = 1; i < ring.Count; i++)
        {
            float d = Vector2.DistanceSquared(ring[0], ring[i]);
            if (d > best)
            {
                best = d;
                far = i;
            }
        }
        var firstHalf = ring.Take(far + 1).Select(p => ((double)p.X, (double)p.Y)).ToList();
        var secondHalf = ring.Skip(far).Append(ring[0]).Select(p => ((double)p.X, (double)p.Y)).ToList();
        var a = Skeletons.Simplify(firstHalf, tolerance);
        var b = Skeletons.Simplify(secondHalf, tolerance);
        var result = a.Concat(b.Skip(1).SkipLast(1)).Select(p => new Vector2((float)p.X, (float)p.Y)).ToList();
        return result.Count >= 3 ? result : [.. ring];
    }

    /// <summary>
    /// Calls <paramref name="cell"/> for every cell of a <paramref name="width"/> × <paramref name="height"/> grid whose
    /// center lies inside the rings (even–odd: holes are rings too); coordinates in cells, cell (x, y)'s center at
    /// (x + 0.5, y + 0.5).
    /// </summary>
    public static void Scan(IReadOnlyList<IReadOnlyList<Vector2>> rings, int width, int height, Action<int, int> cell)
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var ring in rings)
        {
            foreach (var p in ring)
            {
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY));
        var crossings = new List<float>();
        for (int y = y0; y <= y1; y++)
        {
            float cy = y + 0.5f;
            crossings.Clear();
            foreach (var ring in rings)
            {
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    var a = ring[i];
                    var b = ring[j];
                    if ((a.Y > cy) == (b.Y > cy)) continue;
                    crossings.Add(a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling(crossings[k] - 0.5f)), x1 = Math.Min(width - 1, (int)Math.Floor(crossings[k + 1] - 0.5f));
                for (int x = x0; x <= x1; x++) cell(x, y);
            }
        }
    }

    /// <summary>Calls <paramref name="cell"/> for every cell whose center lies within <paramref name="radius"/> of the polyline (coordinates in cells).</summary>
    public static void Stroke(IReadOnlyList<Vector2> line, float radius, int width, int height, Action<int, int> cell)
    {
        var done = new HashSet<(int, int)>();
        for (int i = 0; i + 1 < line.Count || i == 0 && line.Count == 1; i++)
        {
            var a = line[i];
            var b = line[Math.Min(i + 1, line.Count - 1)];
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - radius)), x1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + radius));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - radius)), y1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + radius));
            var ab = b - a;
            float length2 = ab.LengthSquared();
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float t = length2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / length2, 0, 1) : 0;
                    if (Vector2.Distance(p, a + ab * t) <= radius && done.Add((x, y))) cell(x, y);
                }
            }
        }
    }

    /// <summary>Gets the part of a ring inside the rectangle (Sutherland–Hodgman; the rectangle is convex, so one ring or none).</summary>
    public static List<Vector2> ClipRing(IReadOnlyList<Vector2> ring, float x0, float z0, float x1, float z1)
    {
        var output = ring.ToList();
        foreach (var (inside, cross) in new (Func<Vector2, bool> Inside, Func<Vector2, Vector2, Vector2> Cross)[]
        {
            (p => p.X >= x0, (a, b) => Vector2.Lerp(a, b, (x0 - a.X) / (b.X - a.X))),
            (p => p.X <= x1, (a, b) => Vector2.Lerp(a, b, (x1 - a.X) / (b.X - a.X))),
            (p => p.Y >= z0, (a, b) => Vector2.Lerp(a, b, (z0 - a.Y) / (b.Y - a.Y))),
            (p => p.Y <= z1, (a, b) => Vector2.Lerp(a, b, (z1 - a.Y) / (b.Y - a.Y))),
        })
        {
            if (output.Count == 0) break;
            var input = output;
            output = new List<Vector2>(input.Count + 4);
            for (int i = 0; i < input.Count; i++)
            {
                var current = input[i];
                var previous = input[(i + input.Count - 1) % input.Count];
                bool now = inside(current), before = inside(previous);
                if (now)
                {
                    if (!before) output.Add(cross(previous, current));
                    output.Add(current);
                }
                else if (before)
                {
                    output.Add(cross(previous, current));
                }
            }
        }
        return output.Count >= 3 ? output : [];
    }

    /// <summary>Gets a ring's centroid (its area's center of mass; the mean of its points if it has no area).</summary>
    public static Vector2 Centroid(IReadOnlyList<Vector2> ring)
    {
        double area = 0, cx = 0, cy = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double cross = (double)a.X * b.Y - (double)b.X * a.Y;
            area += cross;
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }
        if (Math.Abs(area) < 1e-9)
        {
            var sum = Vector2.Zero;
            foreach (var p in ring) sum += p;
            return ring.Count == 0 ? sum : sum / ring.Count;
        }
        return new Vector2((float)(cx / (3 * area)), (float)(cy / (3 * area)));
    }

    /// <summary>Gets the convex hull of points, counterclockwise on screen (Andrew's monotone chain).</summary>
    public static List<Vector2> ConvexHull(IEnumerable<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        static double Cross(Vector2 o, Vector2 a, Vector2 b) => (double)(a.X - o.X) * (b.Y - o.Y) - (double)(a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                // Keeps right turns in math coordinates, which are left turns on screen: counterclockwise on screen.
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) >= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    /// <summary>Gets the smallest rectangle around points (rotating calipers: one side lies on an edge of their hull).</summary>
    public static OrientedRectangle MinimumRectangle(IEnumerable<Vector2> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count == 0) return default;
        if (hull.Count == 1) return new OrientedRectangle(hull[0], 0, 0, Vector2.UnitX);
        OrientedRectangle best = default;
        double bestArea = double.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            var edge = hull[(i + 1) % hull.Count] - hull[i];
            if (edge.LengthSquared() < 1e-12f) continue;
            var u = Vector2.Normalize(edge);
            var v = new Vector2(-u.Y, u.X);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var p in hull)
            {
                float a = Vector2.Dot(p, u), b = Vector2.Dot(p, v);
                minU = Math.Min(minU, a);
                maxU = Math.Max(maxU, a);
                minV = Math.Min(minV, b);
                maxV = Math.Max(maxV, b);
            }
            double area = (double)(maxU - minU) * (maxV - minV);
            if (area >= bestArea) continue;
            bestArea = area;
            var center = u * (minU + maxU) / 2 + v * (minV + maxV) / 2;
            float along = maxU - minU, across = maxV - minV;
            best = along >= across ? new OrientedRectangle(center, along, across, u) : new OrientedRectangle(center, across, along, v);
        }
        return best;
    }
}
