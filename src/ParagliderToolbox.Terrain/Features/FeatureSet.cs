using System.Numerics;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>What kind of tree (or bush) a <see cref="TreeFeature"/> is.</summary>
public enum TreeKind
{
    /// <summary>Not known (found in the image alone).</summary>
    Unknown,

    /// <summary>A pointed crown: spruce, fir, larch, pine.</summary>
    Conifer,

    /// <summary>A rounded crown: beech, oak, maple, fruit trees.</summary>
    Broadleaf,

    /// <summary>A bush or young tree, lower than the minimum tree height.</summary>
    Bush,
}

/// <summary>Where a feature was found.</summary>
public enum FeatureOrigin
{
    /// <summary>In the aerial images (and the surface model).</summary>
    Image,

    /// <summary>On a map (OpenStreetMap).</summary>
    Map,
}

/// <summary>A tree (or bush) found by its crown, or on a map.</summary>
/// <param name="Position">Where its trunk stands: x east, y the ground's height, z south (m).</param>
/// <param name="Height">Its height above the ground (m).</param>
/// <param name="CrownRadius">Its crown's radius (m).</param>
/// <param name="Kind">What kind of tree it is.</param>
/// <param name="Estimated">Whether the height is estimated (from the crown, or the map gives none) rather than measured.</param>
public readonly record struct TreeFeature(Vector3 Position, float Height, float CrownRadius, TreeKind Kind, bool Estimated)
{
    /// <summary>Gets where it was found.</summary>
    public FeatureOrigin Origin { get; init; }

    /// <summary>Gets the map's tags (genus, species, leaf_type …), or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>What a <see cref="RoadFeature"/> is: by its class on a map, or by its width and surface in the images.</summary>
public enum RoadKind
{
    /// <summary>A paved road 6 m wide or more: a main road, a village's main street.</summary>
    MainRoad,

    /// <summary>A paved road 3–6 m wide: a side road, a lane.</summary>
    Road,

    /// <summary>An unpaved road 3 m wide or more: a farm or forest track, a gravel road.</summary>
    Track,

    /// <summary>Narrower than a track or road: a footpath, a hiking trail.</summary>
    Path,
}

/// <summary>What a road's surface is.</summary>
public enum RoadSurface
{
    /// <summary>Asphalt or concrete.</summary>
    Paved,

    /// <summary>Gravel or light, compacted dirt.</summary>
    Gravel,

    /// <summary>Brown soil, a worn trail.</summary>
    Dirt,
}

/// <summary>How a road crosses the ground.</summary>
public enum RoadStructure
{
    /// <summary>On the ground.</summary>
    Ground,

    /// <summary>On a bridge: its points' heights run straight between its ends.</summary>
    Bridge,

    /// <summary>In a tunnel (or through a building): its points' heights run straight between its portals.</summary>
    Tunnel,
}

/// <summary>A road or path between two junctions (or ends): its center line with its width and surface.</summary>
/// <param name="Points">The center line: x east, y the ground's height, z south (m).</param>
/// <param name="Width">Its width (m).</param>
/// <param name="Surface">What its surface is.</param>
/// <param name="Kind">What it is, by its width and surface.</param>
/// <param name="Start">The junction at its first point (an index into <see cref="FeatureSet.Junctions"/>), or −1 for a dead end.</param>
/// <param name="End">The junction at its last point, or −1 for a dead end.</param>
public sealed record RoadFeature(IReadOnlyList<Vector3> Points, float Width, RoadSurface Surface, RoadKind Kind, int Start, int End)
{
    /// <summary>Gets where it was found.</summary>
    public FeatureOrigin Origin { get; init; }

    /// <summary>Gets how it crosses the ground.</summary>
    public RoadStructure Structure { get; init; }

    /// <summary>Gets its class on the map (OpenStreetMap's <c>highway</c>: "track", "path", "residential" …), or <c>null</c>.</summary>
    public string? Class { get; init; }

    /// <summary>Gets the map's tags kept with it (<see cref="MapRoads.KeptTags"/>: name, surface, sac_scale …), or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>Gets its length along the center line (m, horizontally).</summary>
    public double Length
    {
        get
        {
            double length = 0;
            for (int i = 1; i < Points.Count; i++) length += Vector2.Distance(new(Points[i].X, Points[i].Z), new(Points[i - 1].X, Points[i - 1].Z));
            return length;
        }
    }

    /// <summary>Gets the kind of a road of <paramref name="width"/> meters and <paramref name="surface"/>.</summary>
    public static RoadKind KindOf(double width, RoadSurface surface) => surface == RoadSurface.Paved
        ? width >= 6 ? RoadKind.MainRoad : width >= 3 ? RoadKind.Road : RoadKind.Path
        : width >= 3 ? RoadKind.Track : RoadKind.Path;
}

/// <summary>What a building's roof is like.</summary>
public enum RoofShape
{
    /// <summary>Flat: its top hardly higher than its eaves.</summary>
    Flat,

    /// <summary>Pitched: gable or hip, its ridge at least a meter above its eaves.</summary>
    Pitched,
}

/// <summary>A building: the rectangle around its footprint, its outline and its height.</summary>
/// <param name="Position">Its footprint's center: x east, y the lowest ground at its walls, z south (m).</param>
/// <param name="Length">The rectangle's long side (m).</param>
/// <param name="Width">The rectangle's short side (m).</param>
/// <param name="Angle">The long side's direction, clockwise from north (−z) toward east (+x), 0–180°.</param>
/// <param name="Height">Its top above the ground (m): the ridge, or the flat roof.</param>
/// <param name="Eaves">Its eaves above the ground (m): where the roof begins.</param>
/// <param name="Roof">Its roof's shape.</param>
/// <param name="Footprint">Its outline (x, z), counterclockwise seen from above, not closed (the last point isn't the first again).</param>
public sealed record BuildingFeature(Vector3 Position, float Length, float Width, float Angle, float Height, float Eaves, RoofShape Roof,
    IReadOnlyList<Vector2> Footprint)
{
    /// <summary>Gets where it was found (its outline; the heights are measured wherever a surface model is).</summary>
    public FeatureOrigin Origin { get; init; }

    /// <summary>Gets whether its heights are measured (a surface model) rather than from the map's tags or its class's usual height.</summary>
    public bool Measured { get; init; } = true;

    /// <summary>Gets its class on the map (OpenStreetMap's <c>building</c>: "house", "garage", "yes" …), or <c>null</c>.</summary>
    public string? Class { get; init; }

    /// <summary>Gets the map's tags (name, building:levels, roof:shape …), or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>A boulder: a lone rock in grass or forest.</summary>
/// <param name="Position">Its center on the ground: x east, y the ground's height, z south (m).</param>
/// <param name="Radius">Its radius seen from above (m).</param>
/// <param name="Height">How far it stands above the ground around it (m; at least half its radius).</param>
public readonly record struct RockFeature(Vector3 Position, float Radius, float Height)
{
    /// <summary>Gets where it was found.</summary>
    public FeatureOrigin Origin { get; init; }
}

/// <summary>What covers an <see cref="AreaFeature"/>.</summary>
public enum AreaKind
{
    /// <summary>Forest: trees close together.</summary>
    Forest,

    /// <summary>Bare rock and scree, less steep than a cliff.</summary>
    Rock,

    /// <summary>Rock faces steeper than 45°.</summary>
    Cliff,

    /// <summary>Gravel and bare soil: riverbeds, gravel pits, fine scree.</summary>
    Gravel,

    /// <summary>Asphalt and concrete: squares, parking lots, yards.</summary>
    Paved,

    /// <summary>Lakes and rivers.</summary>
    Water,

    /// <summary>Snow and ice: snowfields, glaciers.</summary>
    Snow,

    /// <summary>Scree and shingle: loose stones (from the map; the images call it rock or gravel).</summary>
    Scree,

    /// <summary>Scrub and heath: bushes.</summary>
    Scrub,

    /// <summary>Meadows, pastures, lawns and grassland.</summary>
    Grassland,

    /// <summary>Fields and allotments.</summary>
    Farmland,

    /// <summary>Orchards: fruit trees in rows.</summary>
    Orchard,

    /// <summary>Vineyards.</summary>
    Vineyard,

    /// <summary>Wetland: marsh, bog, reeds.</summary>
    Wetland,

    /// <summary>Sand and beaches.</summary>
    Sand,

    /// <summary>Built-up land: residential, commercial, industrial, farmyards.</summary>
    BuiltUp,
}

/// <summary>An area of one kind of ground: its outline and its holes (clearings in a forest, islands in a lake).</summary>
/// <param name="Kind">What covers it.</param>
/// <param name="Outline">Its outline (x, z), counterclockwise seen from above, not closed.</param>
/// <param name="Holes">Its holes, clockwise seen from above, not closed.</param>
public sealed record AreaFeature(AreaKind Kind, IReadOnlyList<Vector2> Outline, IReadOnlyList<IReadOnlyList<Vector2>> Holes)
{
    /// <summary>Gets where it was found.</summary>
    public FeatureOrigin Origin { get; init; }

    /// <summary>Gets its class on the map ("landuse=forest", "natural=scree" …), or <c>null</c>.</summary>
    public string? Class { get; init; }

    /// <summary>Gets the map's tags (name, leaf_type …), or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>Gets its size (m², the holes taken out).</summary>
    public double Size => Math.Abs(Polygons.SignedArea(Outline)) - Holes.Sum(h => Math.Abs(Polygons.SignedArea(h)));
}

/// <summary>What a <see cref="LineFeature"/> is.</summary>
public enum LineKind
{
    /// <summary>A stream or brook.</summary>
    Stream,

    /// <summary>A river.</summary>
    River,

    /// <summary>A canal.</summary>
    Canal,

    /// <summary>A ditch or drain.</summary>
    Ditch,

    /// <summary>A high-voltage power line on towers: its points are the towers.</summary>
    PowerLine,

    /// <summary>A minor power or telephone line on poles: its points are the poles.</summary>
    MinorPowerLine,

    /// <summary>A cable car, gondola, chair lift, drag lift or zip line: its points are the stations and pylons.</summary>
    Aerialway,

    /// <summary>A railway, funicular or tram line.</summary>
    Railway,

    /// <summary>A fence.</summary>
    Fence,

    /// <summary>A wall or retaining wall.</summary>
    Wall,

    /// <summary>A hedge.</summary>
    Hedge,

    /// <summary>A cliff's edge (the cliff falls away to the right of the line's direction).</summary>
    Cliff,
}

/// <summary>A line on a map that isn't a road: a waterway, a power line, an aerialway, a railway, a fence, a cliff's edge.</summary>
/// <param name="Points">The line: x east, y the ground's height, z south (m); for power lines and aerialways the towers, pylons and stations.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Width">Its width (m): of the water, the track, the wall; 0 for wires.</param>
/// <param name="Height">How high it stands (m): the wires at the towers, the fence; 0 where it lies on the ground.</param>
public sealed record LineFeature(IReadOnlyList<Vector3> Points, LineKind Kind, float Width, float Height)
{
    /// <summary>Gets how it crosses the ground (railways on bridges, streams in culverts).</summary>
    public RoadStructure Structure { get; init; }

    /// <summary>Gets its class on the map ("waterway=stream", "aerialway=chair_lift" …).</summary>
    public string? Class { get; init; }

    /// <summary>Gets the map's tags (name, voltage, aerialway:occupancy …), or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>Gets its length (m, horizontally).</summary>
    public double Length
    {
        get
        {
            double length = 0;
            for (int i = 1; i < Points.Count; i++) length += Vector2.Distance(new(Points[i].X, Points[i].Z), new(Points[i - 1].X, Points[i - 1].Z));
            return length;
        }
    }
}

/// <summary>What a <see cref="LandmarkFeature"/> is.</summary>
public enum LandmarkKind
{
    /// <summary>A summit.</summary>
    Peak,

    /// <summary>A pass or saddle.</summary>
    Saddle,

    /// <summary>A paragliding or hang gliding takeoff.</summary>
    Takeoff,

    /// <summary>A paragliding or hang gliding landing.</summary>
    Landing,

    /// <summary>A free flying site the map doesn't say more of.</summary>
    FlyingSite,

    /// <summary>A mast or antenna (an obstacle, often guyed).</summary>
    Mast,

    /// <summary>A tower: observation, communication, cooling … (<c>tower:type</c> in the tags).</summary>
    Tower,

    /// <summary>A cross: on a summit, by a path.</summary>
    Cross,

    /// <summary>A mountain or wilderness hut.</summary>
    Hut,

    /// <summary>A shelter.</summary>
    Shelter,

    /// <summary>A signpost of hiking trails.</summary>
    Guidepost,

    /// <summary>A bench.</summary>
    Bench,

    /// <summary>A viewpoint.</summary>
    Viewpoint,

    /// <summary>A spring.</summary>
    Spring,

    /// <summary>A cave's entrance.</summary>
    Cave,

    /// <summary>A station of a cable car or lift.</summary>
    Station,
}

/// <summary>A point of a map worth a model or a label: a peak, a takeoff, a mast, a hut, a signpost ….</summary>
/// <param name="Position">Where it is: x east, y the ground's height, z south (m).</param>
/// <param name="Kind">What it is.</param>
public sealed record LandmarkFeature(Vector3 Position, LandmarkKind Kind)
{
    /// <summary>Gets its name, or <c>null</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Gets its height above sea level as the map gives it (<c>ele</c>, m), or <c>null</c>.</summary>
    public float? Elevation { get; init; }

    /// <summary>Gets the height of the structure (a mast, a tower, a cross; m), or <c>null</c>.</summary>
    public float? Height { get; init; }

    /// <summary>Gets its class on the map ("natural=peak", "man_made=mast" …).</summary>
    public string? Class { get; init; }

    /// <summary>Gets the map's tags, or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>The ground cover of an area on a grid (a <see cref="GroundCover"/> per cell, row by row from the north-west).</summary>
/// <param name="X0">The x (east) of the grid's west edge (m).</param>
/// <param name="Z0">The z (south) of its north edge (m).</param>
/// <param name="Spacing">The cells' size (m).</param>
/// <param name="Columns">The cells across.</param>
/// <param name="Rows">The cells down.</param>
/// <param name="Cells">The cover of every cell, row by row from the north-west.</param>
public sealed record CoverMap(double X0, double Z0, double Spacing, int Columns, int Rows, byte[] Cells)
{
    /// <summary>Gets the cover at local (x, z), or <see cref="GroundCover.None"/> outside the map.</summary>
    public GroundCover At(double x, double z)
    {
        int column = (int)Math.Floor((x - X0) / Spacing), row = (int)Math.Floor((z - Z0) / Spacing);
        return column < 0 || row < 0 || column >= Columns || row >= Rows ? GroundCover.None : (GroundCover)Cells[row * Columns + column];
    }

    /// <summary>Gets the share of the cells covered by each kind (indexed by <see cref="GroundCover"/>).</summary>
    public double[] Shares()
    {
        var counts = new double[Enum.GetValues<GroundCover>().Length];
        foreach (byte cell in Cells) counts[Math.Min(cell, counts.Length - 1)]++;
        for (int i = 0; i < counts.Length; i++) counts[i] /= Math.Max(1, Cells.Length);
        return counts;
    }
}

/// <summary>
/// The features found in an area of a terrain's aerial images (and terrain and surface models), in the terrain's frame
/// (see <see cref="LocalFrame"/>): trees, roads and paths as a network, buildings, boulders, areas of forest, rock,
/// water … and the ground cover map, for a game to place its models by.
/// </summary>
public sealed class FeatureSet
{
    /// <summary>Gets the frame's origin (the terrain's center).</summary>
    public required GeoPoint Origin { get; init; }

    /// <summary>Gets the area searched: the x (east) of its west edge (m).</summary>
    public required double X0 { get; init; }

    /// <summary>Gets the area searched: the z (south) of its north edge (m).</summary>
    public required double Z0 { get; init; }

    /// <summary>Gets the area's east–west extent (m).</summary>
    public required double Width { get; init; }

    /// <summary>Gets the area's north–south extent (m).</summary>
    public required double Depth { get; init; }

    /// <summary>Gets the pixel size the images were read at (m).</summary>
    public required double Spacing { get; init; }

    /// <summary>Gets the share of the area a surface model covered (0–1): there trees' heights are measured and buildings found.</summary>
    public double SurfaceShare { get; init; }

    /// <summary>Gets the trees and bushes.</summary>
    public IReadOnlyList<TreeFeature> Trees { get; init; } = [];

    /// <summary>Gets the roads and paths, each between two junctions or ends.</summary>
    public IReadOnlyList<RoadFeature> Roads { get; init; } = [];

    /// <summary>Gets the junctions where roads meet (x, y the ground's height, z).</summary>
    public IReadOnlyList<Vector3> Junctions { get; init; } = [];

    /// <summary>Gets the buildings: the map's, and those a surface model finds that the map lacks.</summary>
    public IReadOnlyList<BuildingFeature> Buildings { get; init; } = [];

    /// <summary>Gets the boulders.</summary>
    public IReadOnlyList<RockFeature> Rocks { get; init; } = [];

    /// <summary>Gets the areas of forest, rock, water ….</summary>
    public IReadOnlyList<AreaFeature> Areas { get; init; } = [];

    /// <summary>Gets the lines of the map: waterways, power lines, aerialways, railways, fences, cliffs.</summary>
    public IReadOnlyList<LineFeature> Lines { get; init; } = [];

    /// <summary>Gets the landmarks of the map: peaks, flying sites, masts, huts, signposts ….</summary>
    public IReadOnlyList<LandmarkFeature> Landmarks { get; init; } = [];

    /// <summary>Gets the ground cover map, or <c>null</c>.</summary>
    public CoverMap? Covers { get; init; }

    /// <summary>Gets the credits of the data the features were found in, one per line.</summary>
    public string Attribution { get; init; } = "";

    /// <summary>Gets what to know about the result (sources too coarse, areas without data).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Gets how long the search took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Gets the length of the roads of <paramref name="kind"/> (m).</summary>
    public double LengthOf(RoadKind kind) => Roads.Where(r => r.Kind == kind).Sum(r => r.Length);
}
