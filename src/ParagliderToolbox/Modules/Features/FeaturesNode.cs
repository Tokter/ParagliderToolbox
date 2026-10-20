using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain.Features;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Modules.Features;

/// <summary>How finely features are looked for: the pixel size the aerial images are read at.</summary>
public enum FeatureDetail
{
    /// <summary>25 cm: narrow paths and small trees; four times the data and time of Normal.</summary>
    Fine,

    /// <summary>50 cm: paths, trees, buildings.</summary>
    Normal,

    /// <summary>1 m: roads and larger trees, a first look over a large area.</summary>
    Quick,
}

/// <summary>
/// The features of an area of the terrain it belongs to, for a game to place its models by: trees and bushes, roads and
/// paths, buildings, boulders and areas of forest, rock, water …, found in the aerial images and the terrain and surface
/// models (see <see cref="FeatureDetector"/>), with what to look for and how finely; the result is saved with the project.
/// </summary>
/// <remarks>
/// <see cref="SettingsVersion"/> counts the changes, so the detail view knows when the result is out of date. The result
/// is kept in the project file compressed (<see cref="FeatureFiles.ToBytes"/>).
/// </remarks>
[Inspectable]
public partial class FeaturesNode : ProjectNode, IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>The property categories in display order.</summary>
    public static readonly string[] CategoryOrder = ["Area", "Look for", "Outlook", "Found"];

    private string _center = "";
    private double _size = 1000;
    private FeatureDetail _detail = FeatureDetail.Normal;
    private bool _trees = true, _bushes = true, _roads = true, _buildings = true, _rocks = true, _areas = true, _surfaceModel = true;
    private bool _map = true, _lines = true, _landmarks = true;
    private double _minTreeHeight = 3;
    private RoadSource _roadsFrom = RoadSource.OpenStreetMap;
    private FeatureSet? _result;
    private int _resultVersion;
    private bool _loading;

    /// <summary>Initializes features of a square kilometer around the terrain's center.</summary>
    public FeaturesNode()
    {
        Name = "Features";
    }

    void IJsonOnDeserializing.OnDeserializing() => _loading = true;

    void IJsonOnDeserialized.OnDeserialized()
    {
        _loading = false;
        _resultVersion = SettingsVersion;
    }

    /// <summary>Gets a number that changes whenever a setting changes.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public int SettingsVersion { get; private set; }

    /// <summary>Gets the terrain the features belong to, or <c>null</c>.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public TerrainNode? Terrain => Parent as TerrainNode;

    /// <summary>Gets or sets the features found, or <c>null</c> before a search.</summary>
    [InspectableIgnore]
    public FeatureSet? Result
    {
        get => _result;
        set
        {
            if (ReferenceEquals(_result, value)) return;
            SetProperty(ref _result, value);
            if (!_loading) _resultVersion = SettingsVersion;
            foreach (string name in ResultNames) OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsOutdated));
        }
    }

    /// <summary>Gets whether the result is of other settings than the current ones.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public bool IsOutdated => _result != null && _resultVersion != SettingsVersion;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        if (_loading) return true;
        SettingsVersion++;
        foreach (string outlook in OutlookNames) OnPropertyChanged(outlook);
        OnPropertyChanged(nameof(IsOutdated));
        return true;
    }

    private static readonly string[] OutlookNames = [nameof(AreaSummary), nameof(Estimate)];

    private static readonly string[] ResultNames =
    [
        nameof(Found), nameof(TreeSummary), nameof(RoadSummary), nameof(BuildingSummary), nameof(RockSummary), nameof(AreaFeatures), nameof(LineSummary),
        nameof(LandmarkSummary), nameof(Notes),
    ];

    /// <summary>Gets the pixel size of the detail (m).</summary>
    public static double SpacingOf(FeatureDetail detail) => detail switch
    {
        FeatureDetail.Fine => 0.25,
        FeatureDetail.Quick => 1,
        _ => 0.5,
    };

    /// <summary>Gets what to look for (in the finest images there are, whatever source the terrain prefers for its look).</summary>
    public FeatureSettings Settings() => new()
    {
        Spacing = SpacingOf(_detail),
        Trees = _trees,
        Bushes = _bushes,
        MinTreeHeight = _minTreeHeight,
        Roads = _roads,
        RoadsFrom = _roadsFrom,
        Buildings = _buildings,
        Rocks = _rocks,
        Areas = _areas,
        SurfaceModel = _surfaceModel,
        Map = _map,
        Lines = _lines,
        Landmarks = _landmarks,
    };

    /// <summary>Gets the area to look in, in the frame of <paramref name="terrain"/> (centered on its center): the square around the center, inside the terrain.</summary>
    public FeatureArea Area(TerrainNode terrain)
    {
        double half = terrain.Layout.Extent / 2;
        var frame = new LocalFrame(terrain.Snapshot().Center);
        var (x, z) = GeoPoint.TryParse(_center, out var point) ? frame.FromGeo(point) : (0, 0);
        var whole = new FeatureArea(-half, -half, 2 * half, 2 * half);
        return FeatureArea.Around(x, z, _size).Intersect(whole) ?? FeatureArea.Around(0, 0, Math.Min(_size, 2 * half));
    }

    #region Area

    [InspectableProperty("Center", "Area", Order = 1,
        Description = "Where to look: coordinates as maps copy them (46.6863, 7.8632), degrees or Swiss coordinates; empty for the terrain's center.")]
    public string Center
    {
        get => _center;
        set
        {
            string text = value?.Trim() ?? "";
            if (text.Length > 0 && !GeoPoint.TryParse(text, out _)) return;
            Set(ref _center, text);
        }
    }

    [InspectableProperty("Size (m)", "Area", Order = 2,
        Description = "The side of the square to look in (inside the terrain). The time and the downloads grow with its area: 1 km² takes a few seconds at 50 cm once its data is downloaded.")]
    public double Size { get => _size; set => Set(ref _size, Math.Clamp(Math.Round(value), 50, 20000)); }

    [InspectableProperty("Detail", "Area", Order = 3,
        Description = "Fine reads the aerial images at 25 cm (narrow paths, small trees; four times the data and time), Normal at 50 cm, Quick at 1 m (roads and larger trees).")]
    public FeatureDetail Detail { get => _detail; set => Set(ref _detail, value); }

    #endregion

    #region Look for

    [InspectableProperty("OpenStreetMap", "Look for", Order = 9,
        Description = "Read OpenStreetMap too: the buildings (everywhere; their heights measured where a surface model is, else from the map's tags or their kind), the land use and natural areas, single trees and tree rows, stones, lines and landmarks, and (as Roads from says) the roads. Its data is under the Open Database License: credit \"© OpenStreetMap contributors\", and data derived from it stays under the ODbL when shared.")]
    public bool Map { get => _map; set => Set(ref _map, value); }

    [InspectableProperty("Trees", "Look for", Order = 10, Description = "Each tree: where its trunk stands, its height and crown, conifer or broadleaf (where a surface model measures them, or the map's forests tell).")]
    public bool Trees { get => _trees; set => Set(ref _trees, value); }

    [InspectableProperty("Bushes", "Look for", Order = 11, Description = "Bushes and young trees from 1.2 m up to the least tree height, with the trees.")]
    public bool Bushes { get => _bushes; set => Set(ref _bushes, value); }

    [InspectableProperty("Least tree height (m)", "Look for", Order = 12, Description = "Lower ones are bushes.")]
    public double MinTreeHeight { get => _minTreeHeight; set => Set(ref _minTreeHeight, Math.Clamp(value, 1.5, 15)); }

    [InspectableProperty("Roads and paths", "Look for", Order = 13,
        Description = "Roads, tracks and paths as center lines with their width and surface, joined at their junctions.")]
    public bool Roads { get => _roads; set => Set(ref _roads, value); }

    [InspectableProperty("Roads from", "Look for", Order = 14,
        Description = "OpenStreetMap: the roads and paths as mapped, with their classes, names and junctions (the images where it can't be reached); the aerial images; or both (the images' roads where the map has none). OpenStreetMap's data is under the Open Database License: credit \"© OpenStreetMap contributors\", and road data derived from it stays under the ODbL when shared.")]
    public RoadSource RoadsFrom { get => _roadsFrom; set => Set(ref _roadsFrom, value); }

    [InspectableProperty("Buildings", "Look for", Order = 15,
        Description = "Buildings with their outline, height and roof: the map's, and where a surface model gives heights (Switzerland) those it finds that the map lacks.")]
    public bool Buildings { get => _buildings; set => Set(ref _buildings, value); }

    [InspectableProperty("Boulders", "Look for", Order = 16, Description = "Lone rocks in meadows, with their size.")]
    public bool Rocks { get => _rocks; set => Set(ref _rocks, value); }

    [InspectableProperty("Areas", "Look for", Order = 17,
        Description = "Outlines of forests, rock, scree, cliffs, gravel, asphalt, water and snow, and from the map scrub, grassland, fields, orchards, vineyards, wetland, sand and built-up land.")]
    public bool Areas { get => _areas; set => Set(ref _areas, value); }

    [InspectableProperty("Surface model", "Look for", Order = 18,
        Description = "Use a surface model (the top of trees and buildings: swissSURFACE3D in Switzerland) where there is one: trees with measured heights, and buildings. Without, crowns are found in the images and their heights estimated.")]
    public bool SurfaceModel { get => _surfaceModel; set => Set(ref _surfaceModel, value); }

    [InspectableProperty("Lines", "Look for", Order = 19,
        Description = "The map's lines: rivers and streams, power lines and aerialways (their towers and pylons, how high), railways, fences, walls and hedges, cliffs' edges.")]
    public bool Lines { get => _lines; set => Set(ref _lines, value); }

    [InspectableProperty("Landmarks", "Look for", Order = 20,
        Description = "The map's landmarks: peaks and passes, paragliding takeoffs and landings, masts and towers, crosses, huts and shelters, signposts, benches, viewpoints, springs, lift stations.")]
    public bool Landmarks { get => _landmarks; set => Set(ref _landmarks, value); }

    #endregion

    #region Outlook (computed)

    [InspectableProperty("Area", "Outlook", Order = 20, IsReadOnly = true)]
    public string AreaSummary
    {
        get
        {
            if (Terrain is not { } terrain) return string.Create(CultureInfo.CurrentCulture, $"{_size / 1000:0.##} × {_size / 1000:0.##} km (under a terrain)");
            var area = Area(terrain);
            return string.Create(CultureInfo.CurrentCulture, $"{area.Width / 1000:0.##} × {area.Depth / 1000:0.##} km ({area.Width * area.Depth / 1e6:0.##} km²)");
        }
    }

    [InspectableProperty("Time", "Outlook", Order = 21, IsReadOnly = true,
        Description = "Roughly, with the data downloaded; downloading it first takes longer.")]
    public string Estimate
    {
        get
        {
            double square = Terrain is { } terrain ? Area(terrain) is var area ? area.Width * area.Depth / 1e6 : 0 : _size * _size / 1e6;
            double spacing = SpacingOf(_detail);
            double seconds = 2 + 3 * square * 0.25 / (spacing * spacing);
            return seconds < 90 ? string.Create(CultureInfo.CurrentCulture, $"about {seconds:0} s") : string.Create(CultureInfo.CurrentCulture, $"about {seconds / 60:0} min");
        }
    }

    #endregion

    #region Found (computed)

    [InspectableProperty("Found", "Found", Order = 30, IsReadOnly = true)]
    public string Found => _result is not { } r ? "not yet"
        : string.Create(CultureInfo.CurrentCulture,
            $"{r.Width / 1000:0.##} × {r.Depth / 1000:0.##} km at {r.Spacing * 100:0} cm in {r.Elapsed.TotalSeconds:0} s; a surface model covers {r.SurfaceShare:P0}");

    [InspectableProperty("Trees", "Found", Order = 31, IsReadOnly = true)]
    public string TreeSummary
    {
        get
        {
            if (_result is not { } r) return "–";
            var trees = r.Trees.Where(t => t.Kind != TreeKind.Bush).ToList();
            if (trees.Count == 0) return r.Trees.Count == 0 ? "none" : string.Create(CultureInfo.CurrentCulture, $"{r.Trees.Count} bushes");
            int conifers = trees.Count(t => t.Kind == TreeKind.Conifer), broadleaf = trees.Count(t => t.Kind == TreeKind.Broadleaf);
            float median = trees.Select(t => t.Height).Order().ElementAt(trees.Count / 2);
            string kinds = conifers + broadleaf > 0 ? string.Create(CultureInfo.CurrentCulture, $" ({conifers} conifers, {broadleaf} broadleaf)") : "";
            string estimated = trees.Any(t => t.Estimated) ? " (estimated)" : "";
            return string.Create(CultureInfo.CurrentCulture,
                $"{trees.Count}{kinds}, {median:0} m high{estimated}; {r.Trees.Count - trees.Count} bushes");
        }
    }

    [InspectableProperty("Roads", "Found", Order = 32, IsReadOnly = true, Description = "Length by kind: main roads, roads, tracks, paths; and where they come from.")]
    public string RoadSummary => _result is not { } r ? "–"
        : r.Roads.Count == 0 ? "none"
        : string.Create(CultureInfo.CurrentCulture,
            $"{Km(r.LengthOf(RoadKind.MainRoad))} main roads, {Km(r.LengthOf(RoadKind.Road))} roads, {Km(r.LengthOf(RoadKind.Track))} tracks, {Km(r.LengthOf(RoadKind.Path))} paths; {r.Junctions.Count} junctions; {Origins(r)}");

    private static string Origins(FeatureSet result)
    {
        bool map = result.Roads.Any(road => road.Origin == FeatureOrigin.Map), image = result.Roads.Any(road => road.Origin == FeatureOrigin.Image);
        return map && image ? "from OpenStreetMap and the images" : map ? "from OpenStreetMap" : "traced in the images";
    }

    [InspectableProperty("Buildings", "Found", Order = 33, IsReadOnly = true, Description = "How many, how many measured by a surface model (the others' heights from the map's tags or their kind), and from where.")]
    public string BuildingSummary => _result is not { } r ? "–"
        : r.Buildings.Count == 0 ? "none"
        : string.Create(CultureInfo.CurrentCulture,
            $"{r.Buildings.Count}, {r.Buildings.Count(b => b.Roof == RoofShape.Pitched)} with pitched roofs, {r.Buildings.Count(b => b.Measured)} measured; {r.Buildings.Count(b => b.Origin == FeatureOrigin.Map)} from OpenStreetMap");

    [InspectableProperty("Boulders", "Found", Order = 34, IsReadOnly = true)]
    public string RockSummary => _result is not { } r ? "–" : r.Rocks.Count == 0 ? "none" : r.Rocks.Count.ToString(CultureInfo.CurrentCulture);

    [InspectableProperty("Areas", "Found", Order = 35, IsReadOnly = true)]
    public string AreaFeatures => _result is not { } r ? "–"
        : r.Areas.Count == 0 ? "none"
        : string.Join(", ", r.Areas.GroupBy(a => a.Kind).OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.CurrentCulture, $"{FeatureFiles.Name(g.Key)} {g.Sum(a => a.Size) / 1e4:0.##} ha")));

    [InspectableProperty("Lines", "Found", Order = 36, IsReadOnly = true)]
    public string LineSummary => _result is not { } r ? "–"
        : r.Lines.Count == 0 ? "none"
        : string.Join(", ", r.Lines.GroupBy(l => l.Kind).OrderBy(g => g.Key).Select(g => $"{Words(g.Key.ToString())} {Km(g.Sum(l => l.Length))}"));

    [InspectableProperty("Landmarks", "Found", Order = 37, IsReadOnly = true)]
    public string LandmarkSummary => _result is not { } r ? "–"
        : r.Landmarks.Count == 0 ? "none"
        : string.Join(", ", r.Landmarks.GroupBy(l => l.Kind).OrderBy(g => g.Key).Select(g => string.Create(CultureInfo.CurrentCulture, $"{g.Count()} {Words(g.Key.ToString())}")));

    // "MinorPowerLine" reads "minor power line".
    private static string Words(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? char.ToLowerInvariant(c).ToString() : c.ToString()));

    [InspectableProperty("Notes", "Found", Order = 38, IsReadOnly = true)]
    [MultilineText]
    public string Notes => _result is not { } r ? "" : string.Join("\n", r.Notes);

    private static string Km(double meters) =>
        meters >= 1000 ? string.Create(CultureInfo.CurrentCulture, $"{meters / 1000:0.#} km") : string.Create(CultureInfo.CurrentCulture, $"{meters:0} m");

    #endregion
}
