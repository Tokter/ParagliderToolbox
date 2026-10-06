using System.Numerics;

namespace ParagliderToolbox.Paraglider.Mathematics;

/// <summary>
/// Triangulates simple polygons with holes by ear clipping, after bridging each hole into the outline (Eberly, "Triangulation
/// by Ear Clipping"). Used for ribs with cross-vent holes and the tip panels.
/// </summary>
public static class PolygonTriangulator
{
    /// <summary>
    /// Triangulates <paramref name="outline"/> minus <paramref name="holes"/>. Vertex indices refer to the outline's
    /// points first, then each hole's points in order.
    /// </summary>
    /// <returns>Index triples, counterclockwise in the plane.</returns>
    public static List<int> Triangulate(IReadOnlyList<Vector2> outline, IReadOnlyList<IReadOnlyList<Vector2>>? holes = null)
    {
        var points = new List<Vector2>(outline);
        var polygon = Enumerable.Range(0, outline.Count).ToList();
        if (SignedArea(points, polygon) < 0) polygon.Reverse();

        if (holes != null && holes.Count > 0)
        {
            var holeRings = new List<List<int>>();
            foreach (var hole in holes)
            {
                if (hole.Count < 3) continue;
                int start = points.Count;
                points.AddRange(hole);
                var ring = Enumerable.Range(start, hole.Count).ToList();
                if (SignedArea(points, ring) > 0) ring.Reverse(); // holes clockwise
                holeRings.Add(ring);
            }
            foreach (var ring in holeRings.OrderByDescending(r => r.Max(i => points[i].X)))
            {
                polygon = Bridge(points, polygon, ring);
            }
        }
        return ClipEars(points, polygon);
    }

    private static List<int> Bridge(List<Vector2> points, List<int> polygon, List<int> hole)
    {
        // The hole's rightmost vertex M, and the outline vertex visible from it along +X.
        int mi = 0;
        for (int i = 1; i < hole.Count; i++)
        {
            if (points[hole[i]].X > points[hole[mi]].X) mi = i;
        }
        var m = points[hole[mi]];

        int best = -1;
        float bestX = float.MaxValue;
        Vector2 hit = default;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = points[polygon[i]];
            var b = points[polygon[(i + 1) % polygon.Count]];
            if ((a.Y > m.Y) == (b.Y > m.Y)) continue;
            float t = (m.Y - a.Y) / (b.Y - a.Y);
            float x = a.X + t * (b.X - a.X);
            if (x < m.X - 1e-7f || x >= bestX) continue;
            bestX = x;
            hit = new Vector2(x, m.Y);
            best = a.X > b.X ? i : (i + 1) % polygon.Count;
        }
        if (best < 0)
        {
            // Not enclosed: ignore the hole.
            return polygon;
        }

        // A reflex vertex inside the triangle M-hit-P blocks the view: take the one with the smallest angle to +X.
        var p = points[polygon[best]];
        int chosen = best;
        float bestAngle = float.MaxValue;
        for (int i = 0; i < polygon.Count; i++)
        {
            var v = points[polygon[i]];
            if (v == p) continue;
            if (!IsReflex(points, polygon, i)) continue;
            if (!InTriangle(v, m, hit, p)) continue;
            float angle = MathF.Abs(MathF.Atan2(v.Y - m.Y, v.X - m.X));
            if (angle < bestAngle)
            {
                bestAngle = angle;
                chosen = i;
            }
        }

        // Splice: ... P, M, hole ..., M, P, ...
        var result = new List<int>(polygon.Count + hole.Count + 2);
        for (int i = 0; i <= chosen; i++) result.Add(polygon[i]);
        for (int k = 0; k <= hole.Count; k++) result.Add(hole[(mi + k) % hole.Count]);
        result.Add(polygon[chosen]);
        for (int i = chosen + 1; i < polygon.Count; i++) result.Add(polygon[i]);
        return result;
    }

    private static List<int> ClipEars(List<Vector2> points, List<int> polygon)
    {
        var triangles = new List<int>((polygon.Count - 2) * 3);
        var ring = new List<int>(polygon);
        int guard = 0;
        while (ring.Count > 3 && guard < ring.Count * ring.Count + 100)
        {
            guard++;
            bool clipped = false;
            for (int i = 0; i < ring.Count; i++)
            {
                int prev = ring[(i + ring.Count - 1) % ring.Count], cur = ring[i], next = ring[(i + 1) % ring.Count];
                var a = points[prev];
                var b = points[cur];
                var c = points[next];
                if (Cross(a, b, c) <= 1e-12f) continue; // reflex or degenerate
                bool empty = true;
                for (int j = 0; j < ring.Count; j++)
                {
                    int q = ring[j];
                    if (q == prev || q == cur || q == next) continue;
                    var v = points[q];
                    if (v == a || v == b || v == c) continue; // bridge duplicates
                    if (InTriangle(v, a, b, c))
                    {
                        empty = false;
                        break;
                    }
                }
                if (!empty) continue;
                triangles.Add(prev);
                triangles.Add(cur);
                triangles.Add(next);
                ring.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                // Numerical trouble (nearly collinear points): clip the flattest vertex anyway.
                int i = 0;
                float bestCross = float.MinValue;
                for (int k = 0; k < ring.Count; k++)
                {
                    float cross = Cross(points[ring[(k + ring.Count - 1) % ring.Count]], points[ring[k]], points[ring[(k + 1) % ring.Count]]);
                    if (cross > bestCross)
                    {
                        bestCross = cross;
                        i = k;
                    }
                }
                ring.RemoveAt(i);
            }
        }
        if (ring.Count == 3 && Cross(points[ring[0]], points[ring[1]], points[ring[2]]) > 0)
        {
            triangles.AddRange(ring);
        }
        return triangles;
    }

    private static bool IsReflex(List<Vector2> points, List<int> polygon, int i) =>
        Cross(points[polygon[(i + polygon.Count - 1) % polygon.Count]], points[polygon[i]], points[polygon[(i + 1) % polygon.Count]]) <= 0;

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static float SignedArea(List<Vector2> points, List<int> ring)
    {
        float area = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = points[ring[i]];
            var b = points[ring[(i + 1) % ring.Count]];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area / 2;
    }
}
