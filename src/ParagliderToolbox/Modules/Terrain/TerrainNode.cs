using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>The kind of data a <see cref="SourceChoice"/> picks a source of.</summary>
public enum SourceKind
{
    /// <summary>Heights.</summary>
    Elevation,

    /// <summary>Aerial or satellite images.</summary>
    Imagery,
}

/// <summary>A preferred source of a terrain (<c>null</c> id: the finest that covers each point), edited with a list of the registered sources.</summary>
/// <param name="Kind">Elevation or imagery.</param>
/// <param name="Id">The source's id, or <c>null</c> for automatic.</param>
public sealed record SourceChoice(SourceKind Kind, string? Id);

/// <summary>
/// A terrain in the project: where it is, how big and fine, how it is split into tiles and levels of detail and how
/// it is colored (see <see cref="TerrainSettings"/>), edited in the property editor and saved with the project. The
/// detail view builds it from the elevation and imagery sources and shows it with its levels of detail.
/// </summary>
/// <remarks>
/// The built terrain isn't saved: it is built again from the sources' data, which stays in the download cache.
/// <see cref="SettingsVersion"/> counts the changes, so the preview knows when its terrain is out of date.
/// </remarks>
[Inspectable]
public partial class TerrainNode : ProjectNode
{
    /// <summary>The property categories in display order.</summary>
    public static readonly string[] CategoryOrder = ["Location", "Area", "Tiles", "Surface", "Sources", "Summary"];

    private TerrainSettings _settings = new();
    private TerrainLayout? _layout;

    /// <summary>Initializes a terrain around Interlaken.</summary>
    public TerrainNode()
    {
        Name = "Terrain";
    }

    /// <summary>Initializes a terrain with <paramref name="settings"/>.</summary>
    public TerrainNode(TerrainSettings settings) : this()
    {
        _settings = settings;
    }

    /// <summary>Gets a number that changes whenever a setting changes.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public int SettingsVersion { get; private set; }

    /// <summary>Gets the settings.</summary>
    public TerrainSettings Snapshot() => _settings;

    /// <summary>Gets the layout of the settings.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public TerrainLayout Layout => _layout ??= new TerrainLayout(_settings);

    private bool Set<T>(T current, T value, Func<TerrainSettings, T, TerrainSettings> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        _settings = apply(_settings, value);
        _layout = null;
        SettingsVersion++;
        OnPropertyChanged(name);
        foreach (string summary in SummaryNames) OnPropertyChanged(summary);
        return true;
    }

    private static readonly string[] SummaryNames =
    [
        nameof(ActualSize), nameof(ActualTileSize), nameof(LevelSummary), nameof(TileCount), nameof(SampleCount), nameof(Triangles),
        nameof(TextureResolution), nameof(SwissCoordinates),
    ];

    #region Location

    [InspectableProperty("Coordinates", "Location", Order = 1,
        Description = "The center: latitude, longitude as maps copy them (46.6863, 7.8632), degrees with N/E (46°41'10.7\"N 7°51'47.5\"E) or Swiss coordinates (2'632'500 1'169'500).")]
    [JsonIgnore]
    public string Coordinates
    {
        get => _settings.Center.ToString();
        set
        {
            if (!GeoPoint.TryParse(value, out var point) || point == _settings.Center) return;
            Set(_settings.Center, point, (s, v) => s with { Center = v }, nameof(Coordinates));
            OnPropertyChanged(nameof(Latitude));
            OnPropertyChanged(nameof(Longitude));
        }
    }

    [InspectableProperty("Latitude (°)", "Location", Order = 2, Description = "North positive.")]
    public double Latitude
    {
        get => _settings.Center.Latitude;
        set
        {
            if (Set(_settings.Center.Latitude, Math.Clamp(value, -84, 84), (s, v) => s with { Center = s.Center with { Latitude = v } })) OnPropertyChanged(nameof(Coordinates));
        }
    }

    [InspectableProperty("Longitude (°)", "Location", Order = 3, Description = "East positive.")]
    public double Longitude
    {
        get => _settings.Center.Longitude;
        set
        {
            if (Set(_settings.Center.Longitude, Math.Clamp(value, -180, 180), (s, v) => s with { Center = s.Center with { Longitude = v } })) OnPropertyChanged(nameof(Coordinates));
        }
    }

    [InspectableProperty("Swiss coordinates", "Location", Order = 4, IsReadOnly = true, Description = "The center in the Swiss grid (LV95), inside Switzerland.")]
    public string SwissCoordinates
    {
        get
        {
            if (!SwissAlti3DSource.SwissBounds.Contains(_settings.Center)) return "–";
            var (e, n) = SwissGrid.ToLv95(_settings.Center);
            return string.Create(CultureInfo.InvariantCulture, $"{e:#,0} / {n:#,0}").Replace(',', '\'');
        }
    }

    #endregion

    #region Area

    [InspectableProperty("Size (km)", "Area", Order = 10,
        Description = "The side of the square area around the center, rounded to whole tiles: 2–5 km for an arcade map, 20–100 km for a simulator.")]
    public double Size { get => _settings.Size / 1000; set => Set(_settings.Size, Math.Clamp(value, 0.05, 400) * 1000, (s, v) => s with { Size = v }); }

    [InspectableProperty("Full detail within (km)", "Area", Order = 11,
        Description = "The square around the center with the finest level; each coarser level covers twice as much. 0 for full detail everywhere (large areas get expensive).")]
    public double DetailSize { get => _settings.DetailSize / 1000; set => Set(_settings.DetailSize, Math.Clamp(value, 0, 400) * 1000, (s, v) => s with { DetailSize = v }); }

    #endregion

    #region Tiles

    [InspectableProperty("Resolution (m)", "Tiles", Order = 20,
        Description = "The distance between the finest height samples: 1–2 m shows every ridge (the Swiss data has 0.5 m), 20–50 m for low poly; 30 m is the global data's own.")]
    public double Resolution { get => _settings.Resolution; set => Set(_settings.Resolution, Math.Clamp(value, 0.25, 500), (s, v) => s with { Resolution = v }); }

    [InspectableProperty("Samples per tile", "Tiles", Order = 21,
        Description = "Height samples per tile side: 17, 33, 65, 129, 257, 513 or 1025 (2^k + 1, as game engines' heightmaps need). Larger tiles mean fewer draw calls.")]
    public int TileSamples { get => _settings.TileSamples; set => Set(_settings.TileSamples, TerrainSettings.NearestTileSamples(value), (s, v) => s with { TileSamples = v }); }

    [InspectableProperty("Levels of detail", "Tiles", Order = 22,
        Description = "Each level halves the resolution and doubles the tiles' size (a quadtree); 0 for as many as reach a single tile.")]
    public int LodLevels { get => _settings.LodLevels; set => Set(_settings.LodLevels, Math.Clamp(value, 0, 12), (s, v) => s with { LodLevels = v }); }

    #endregion

    #region Surface

    [InspectableProperty("Texture", "Surface", Order = 30, Description = "Aerial images (elevation colors where there are none), colors from the height and slope, or none.")]
    public TerrainTexture Texture { get => _settings.Texture; set => Set(_settings.Texture, value, (s, v) => s with { Texture = v }); }

    [InspectableProperty("Texture size (px)", "Surface", Order = 31, Description = "Texture pixels per tile side (a power of two); every level has the same, so coarser levels are coarser.")]
    public int TextureSize { get => _settings.TextureSize; set => Set(_settings.TextureSize, TerrainSettings.NearestTextureSize(value), (s, v) => s with { TextureSize = v }); }

    [InspectableProperty("Shading", "Surface", Order = 32, Description = "Smooth and textured, or faceted (low poly): every triangle flat in one color.")]
    public TerrainShading Shading { get => _settings.Shading; set => Set(_settings.Shading, value, (s, v) => s with { Shading = v }); }

    #endregion

    #region Sources

    [InspectableProperty("Elevation", "Sources", Order = 40,
        Description = "Automatic takes every point from the finest source that has it (swissALTI3D in Switzerland, Copernicus elsewhere); a source chosen here goes first.")]
    [JsonIgnore]
    public SourceChoice ElevationSource { get => new(SourceKind.Elevation, _settings.ElevationSource); set => ElevationSourceId = value?.Id; }

    [InspectableProperty("Imagery", "Sources", Order = 41,
        Description = "Automatic takes every point from the finest source that has it (SWISSIMAGE in Switzerland, Sentinel-2 elsewhere); a source chosen here goes first.")]
    [JsonIgnore]
    public SourceChoice ImagerySource { get => new(SourceKind.Imagery, _settings.ImagerySource); set => ImagerySourceId = value?.Id; }

    // The choices are saved as the sources' ids.
    [InspectableIgnore]
    public string? ElevationSourceId
    {
        get => _settings.ElevationSource;
        set
        {
            if (Set(_settings.ElevationSource, string.IsNullOrEmpty(value) ? null : value, (s, v) => s with { ElevationSource = v })) OnPropertyChanged(nameof(ElevationSource));
        }
    }

    [InspectableIgnore]
    public string? ImagerySourceId
    {
        get => _settings.ImagerySource;
        set
        {
            if (Set(_settings.ImagerySource, string.IsNullOrEmpty(value) ? null : value, (s, v) => s with { ImagerySource = v })) OnPropertyChanged(nameof(ImagerySource));
        }
    }

    #endregion

    #region Summary (computed)

    [InspectableProperty("Actual size", "Summary", Order = 50, IsReadOnly = true, Description = "The area rounded to whole tiles of the finest level.")]
    public string ActualSize => string.Create(CultureInfo.CurrentCulture, $"{Layout.Extent / 1000:0.###} × {Layout.Extent / 1000:0.###} km");

    [InspectableProperty("Tile size", "Summary", Order = 51, IsReadOnly = true, Description = "The side of a finest level tile, and how many there are per side.")]
    public string ActualTileSize => string.Create(CultureInfo.CurrentCulture, $"{Layout.TileSize:0.#} m, {Layout.TilesAcross} per side");

    [InspectableProperty("Levels", "Summary", Order = 52, IsReadOnly = true, Description = "The levels of detail, from the finest spacing to the coarsest.")]
    public string LevelSummary => string.Create(CultureInfo.CurrentCulture,
        $"{Layout.LevelCount}: {Layout.Spacing:0.##} m to {Layout.SpacingAt(Layout.LevelCount - 1):0.##} m, {Layout.Roots.Count} root tile{(Layout.Roots.Count == 1 ? "" : "s")}");

    [InspectableProperty("Tiles", "Summary", Order = 53, IsReadOnly = true, Description = "All tiles of all levels.")]
    public int TileCount => Layout.Tiles.Count;

    [InspectableProperty("Height samples", "Summary", Order = 54, IsReadOnly = true, Description = "All levels' samples together.")]
    public string SampleCount => FormatCount(Layout.SampleCount);

    [InspectableProperty("Triangles", "Summary", Order = 55, IsReadOnly = true, Description = "The most detailed tiles everywhere, without skirts.")]
    public string Triangles => FormatCount(Layout.LeafTriangleCount);

    [InspectableProperty("Texture resolution", "Summary", Order = 56, IsReadOnly = true, Description = "The size of a texture pixel at the finest level.")]
    public string TextureResolution => _settings.Texture == TerrainTexture.None ? "–" : string.Create(CultureInfo.CurrentCulture, $"{Layout.TexelSizeAt(0):0.###} m");

    private static string FormatCount(long count) => count switch
    {
        >= 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{count / 1e6:0.#}M"),
        >= 1_000 => string.Create(CultureInfo.CurrentCulture, $"{count / 1e3:0.#}k"),
        _ => count.ToString(CultureInfo.CurrentCulture),
    };

    #endregion
}
