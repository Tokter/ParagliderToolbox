using System.Numerics;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>A road traced in an image: its center line (image coordinates, a pixel's center at +0.5), width and surface, and its end nodes.</summary>
/// <param name="Points">The center line.</param>
/// <param name="Width">Its width (m).</param>
/// <param name="Surface">What its surface is.</param>
/// <param name="Start">The node at its first point (an index into <see cref="RoadTraces.Nodes"/>).</param>
/// <param name="End">The node at its last point.</param>
public sealed record RoadTrace(List<Vector2> Points, float Width, RoadSurface Surface, int Start, int End);

/// <summary>The roads traced in an image and the nodes where they end (junctions and dead ends), in image coordinates.</summary>
/// <param name="Roads">The roads, each between two nodes.</param>
/// <param name="Nodes">The nodes.</param>
public sealed record RoadTraces(List<RoadTrace> Roads, List<Vector2> Nodes);

/// <summary>
/// Traces roads and paths in a ground cover map (<see cref="GroundCovers.Classify"/>): the asphalt, gravel and soil
/// pixels as a mask at <see cref="WorkingSpacing"/>, closed and its small holes filled, thinned to a skeleton, the
/// skeleton as a graph (<see cref="SkeletonGraph"/>) whose short spurs are pruned, and what isn't a road dropped: steep
/// lines (rock slabs), very wide ones (the middle of squares and gravel pits), lines among rocks (scree breaks into gray
/// pieces), wide, uneven lines away from buildings (bare patches), lines in bare ground away from buildings unless they
/// are smoother than the bare ground around them (measured, brightness variation within a meter over that of the bare
/// ground within 10 m: roads 0.3–0.4 in SWISSIMAGE, 0.45–0.65 in NAIP; lines in scree 0.7–1.1), and short asphalt
/// away from buildings (dark gray streaks of rock).
/// </summary>
public static class RoadFinder
{
    /// <summary>The pixel size the roads are traced at (m): finer images are read in blocks of pixels this size.</summary>
    public const double WorkingSpacing = 0.5;

    /// <summary>Traces the roads of a cover map.</summary>
    /// <param name="covers">The ground cover of every pixel, row by row.</param>
    /// <param name="slope">The ground's slope at every pixel (degrees).</param>
    /// <param name="texture">How much the brightness varies within about a meter at every pixel (<see cref="GroundCovers.Classify"/>'s texture).</param>
    /// <param name="width">The pixels across.</param>
    /// <param name="height">The pixels down.</param>
    /// <param name="spacing">The pixel size (m).</param>
    public static RoadTraces Find(GroundCover[] covers, float[] slope, float[] texture, int width, int height, double spacing)
    {
        int f = Math.Max(1, (int)Math.Round(WorkingSpacing / spacing));
        int w = width / f, h = height / f;
        double sp = spacing * f;
        var mask = new bool[w * h];
        var surface = new GroundCover[w * h];
        var rock = new float[w * h];
        var building = new float[w * h];
        var steep = new float[w * h];
        var smooth = new float[w * h];
        var bare = new float[w * h];
        Parallel.For(0, h, y =>
        {
            Span<int> tally = stackalloc int[16];
            for (int x = 0; x < w; x++)
            {
                tally.Clear();
                int roadLike = 0, rocks = 0;
                float variation = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    for (int dx = 0; dx < f; dx++)
                    {
                        int j = (y * f + dy) * width + x * f + dx;
                        var c = covers[j];
                        variation += texture[j];
                        if (c is GroundCover.Paved or GroundCover.Gravel or GroundCover.Soil)
                        {
                            roadLike++;
                            tally[(int)c]++;
                        }
                        else if (c == GroundCover.Rock) rocks++;
                    }
                }
                int i = y * w + x;
                smooth[i] = variation / (f * f);
                bare[i] = (roadLike + rocks) / (float)(f * f);
                mask[i] = roadLike * 2 >= f * f;
                surface[i] = tally[(int)GroundCover.Paved] >= Math.Max(tally[(int)GroundCover.Gravel], tally[(int)GroundCover.Soil]) ? GroundCover.Paved
                    : tally[(int)GroundCover.Gravel] >= tally[(int)GroundCover.Soil] ? GroundCover.Gravel : GroundCover.Soil;
                rock[i] = rocks / (float)(f * f);
                int center = (y * f + f / 2) * width + x * f + f / 2;
                building[i] = covers[center] == GroundCover.Building ? 1 : 0;
                steep[i] = slope[center];
            }
        });

        // Not in rock (scree breaks into gray pieces), not on cliffs.
        int Pixels(double meters) => Math.Max(1, (int)Math.Round(meters / sp));
        var rockShare = Rasters.BoxMean(rock, w, h, Pixels(3));
        var rockAround = Rasters.BoxMean(rock, w, h, Pixels(6));
        var buildingsAround = Rasters.BoxMean(building, w, h, Pixels(30));
        var bareAround = Rasters.BoxMean(bare, w, h, Pixels(10));
        // How much the bare ground around varies: the texture of its pixels within 10 m.
        var bareVariation = new float[w * h];
        for (int i = 0; i < bareVariation.Length; i++) bareVariation[i] = bare[i] * smooth[i];
        bareVariation = Rasters.BoxMean(bareVariation, w, h, Pixels(10));
        for (int i = 0; i < bareVariation.Length; i++) bareVariation[i] = bareAround[i] > 1e-3f ? bareVariation[i] / bareAround[i] : 0;
        // A road's edge pixels see the meadow beside it, so they vary as much as rock: the road takes them back.
        var edges = Skeletons.Dilate(mask, w, h, 1);
        for (int i = 0; i < mask.Length; i++) mask[i] |= edges[i] && rock[i] >= 0.5f;
        for (int i = 0; i < mask.Length; i++) mask[i] &= rockShare[i] < 0.3f && steep[i] < 40;
        mask = Skeletons.Erode(Skeletons.Dilate(mask, w, h, Pixels(1)), w, h, Pixels(1));
        // Small holes (stones in scree, cars on roads) would leave little loops.
        Skeletons.FillHoles(mask, w, h, (int)(40 / (sp * sp)));
        var (labels, sizes) = Skeletons.Components(mask, w, h);
        int smallest = (int)(15 / (sp * sp));
        for (int i = 0; i < mask.Length; i++) if (mask[i] && sizes[labels[i]] < smallest) mask[i] = false;

        var distance = Skeletons.DistanceTransform(mask, w, h);
        var graph = SkeletonGraph.Build(Skeletons.Thin((bool[])mask.Clone(), w, h), w, h);
        // Spurs: the branches thinning grows from a road's edges toward its corners and bulges, about as long as the road is wide.
        graph.Prune((edge, junction) => edge.Length < 2.4 * distance[graph.Nodes[junction].Y * w + graph.Nodes[junction].X] + Pixels(3));

        // The width: twice the median distance from the center line to the edge (a pixel less: the center pixel's own).
        double Width(SkeletonGraph.Edge e)
        {
            var inner = e.Pixels.Count > 6 ? e.Pixels.Skip(2).SkipLast(2) : e.Pixels;
            var d = inner.Select(p => distance[p.Y * w + p.X]).Order().ToArray();
            return Math.Max(1, 2 * d[d.Length / 2] - 1) * sp;
        }
        var degrees = graph.Degrees();
        // Blobs: a short line with two free ends, not much longer than wide.
        graph.Remove(e => e.Length * sp < 2.5 * Width(e) && degrees[e.A] == 1 && degrees[e.B] == 1);
        graph.Remove(e =>
        {
            var slopes = e.Pixels.Select(p => steep[p.Y * w + p.X]).Order().ToArray();
            double rocky = e.Pixels.Average(p => rockAround[p.Y * w + p.X]);
            double width = Width(e);
            // Steep (rock slabs and cliff bands), wide (the middle of squares and gravel pits) or among rocks.
            if (slopes[slopes.Length / 2] > 32 || width > 14 || rocky > 0.2) return true;
            // Wide and uneven away from buildings: the middle of a bare patch or scree, not a road.
            var widths = e.Pixels.Select(p => (double)distance[p.Y * w + p.X]).ToArray();
            double mean = widths.Average(), cv = Math.Sqrt(widths.Average(v => (v - mean) * (v - mean))) / mean;
            bool town = e.Pixels.Average(p => buildingsAround[p.Y * w + p.X]) > 0.005;
            if (town) return false;
            if (width > 6 && cv > 0.4) return true;
            // In bare ground: a line in scree or a gravel patch, unless smoother than the ground around, like a road.
            double around = e.Pixels.Average(p => bareAround[p.Y * w + p.X]);
            if (around <= 0.5) return false;
            double ratio = e.Pixels.Average(p => smooth[p.Y * w + p.X]) / Math.Max(1e-4, e.Pixels.Average(p => bareVariation[p.Y * w + p.X]));
            return ratio > 0.7 || around > 0.8 && ratio > 0.5;
        });
        graph.MergeChains();
        // Short pieces on their own: patches, not roads; and short asphalt away from buildings: asphalt roads are long,
        // dark gray streaks of scree and rock aren't.
        foreach (var component in graph.Components())
        {
            double length = component.Sum(e => e.Length) * sp;
            var pixels = component.SelectMany(e => e.Pixels).ToList();
            bool town = pixels.Average(p => buildingsAround[p.Y * w + p.X]) > 0.005;
            bool paved = pixels.Count(p => surface[p.Y * w + p.X] == GroundCover.Paved) * 2 > pixels.Count;
            if (length < 15 || !town && paved && length < 80) graph.Remove(e => component.Contains(e));
        }

        // In image coordinates: a working pixel's center is f/2 into its block of image pixels.
        Vector2 Image(double x, double y) => new((float)(x * f + f / 2.0), (float)(y * f + f / 2.0));
        var roads = new List<RoadTrace>();
        foreach (var edge in graph.Edges)
        {
            var tally = new int[16];
            foreach (var p in edge.Pixels) tally[(int)surface[p.Y * w + p.X]]++;
            var kind = tally[(int)GroundCover.Paved] >= Math.Max(tally[(int)GroundCover.Gravel], tally[(int)GroundCover.Soil]) ? RoadSurface.Paved
                : tally[(int)GroundCover.Gravel] >= tally[(int)GroundCover.Soil] ? RoadSurface.Gravel : RoadSurface.Dirt;
            var line = Skeletons.Simplify(Smooth(edge.Pixels), 0.5).Select(p => Image(p.X, p.Y)).ToList();
            roads.Add(new RoadTrace(line, (float)Width(edge), kind, edge.A, edge.B));
        }
        return new RoadTraces(roads, graph.Nodes.Select(n => Image(n.X, n.Y)).ToList());
    }

    // A moving average over five pixels takes out the skeleton's stair steps; the ends stay where they are.
    private static List<(double X, double Y)> Smooth(List<(int X, int Y)> pixels)
    {
        var smooth = new List<(double X, double Y)>(pixels.Count);
        for (int i = 0; i < pixels.Count; i++)
        {
            int reach = Math.Min(2, Math.Min(i, pixels.Count - 1 - i));
            double x = 0, y = 0;
            for (int k = -reach; k <= reach; k++)
            {
                x += pixels[i + k].X;
                y += pixels[i + k].Y;
            }
            smooth.Add((x / (2 * reach + 1), y / (2 * reach + 1)));
        }
        return smooth;
    }
}
