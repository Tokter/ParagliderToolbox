using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>A starting point of a new terrain: how big and detailed, for which kind of game.</summary>
/// <param name="Name">The style's name.</param>
/// <param name="Use">What it is for.</param>
/// <param name="Settings">Its settings (the center is replaced by the chosen location).</param>
public sealed record TerrainStyle(string Name, string Use, TerrainSettings Settings);

/// <summary>A paragliding site to start a terrain at.</summary>
/// <param name="Name">The site's name.</param>
/// <param name="Location">Its center (between launch and landing).</param>
public sealed record TerrainSite(string Name, GeoPoint Location);

/// <summary>
/// The choices for a new terrain: its name, location (typed, pasted or a known site) and style; <see cref="CreateNode"/>
/// builds the node. The new-terrain dialog binds to it.
/// </summary>
public sealed partial class NewTerrainOptions : ObservableObject
{
    /// <summary>Gets the styles offered.</summary>
    public static IReadOnlyList<TerrainStyle> Styles { get; } =
    [
        new("Arcade", "Low poly, a few kilometers",
            new TerrainSettings { Size = 4000, Resolution = 32, TileSamples = 33, TextureSize = 64, Shading = TerrainShading.Faceted }),
        new("Game", "Detailed and textured, about 10 km",
            new TerrainSettings { Size = 10000, Resolution = 4, TileSamples = 129, TextureSize = 512 }),
        new("Simulator", "Large; full detail near the center",
            new TerrainSettings { Size = 30000, Resolution = 2, TileSamples = 257, TextureSize = 512, DetailSize = 3000 }),
    ];

    /// <summary>Gets the sites offered.</summary>
    public static IReadOnlyList<TerrainSite> Sites { get; } =
    [
        new("Interlaken", new GeoPoint(46.6863, 7.8632)),
        new("Grindelwald First", new GeoPoint(46.6400, 8.0480)),
        new("Fiesch", new GeoPoint(46.4060, 8.1250)),
        new("Engelberg", new GeoPoint(46.8199, 8.4072)),
        new("Annecy (Forclaz)", new GeoPoint(45.8130, 6.2460)),
        new("Ölüdeniz (Babadağ)", new GeoPoint(36.5400, 29.1400)),
    ];

    private string _name = "Interlaken";
    private bool _nameEdited;
    private string _location = Sites[0].Location.ToString();
    private TerrainStyle _style = Styles[1];

    /// <summary>Gets or sets the name (the site's until edited).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty)) _nameEdited = true;
        }
    }

    /// <summary>Gets or sets the location as typed.</summary>
    public string Location
    {
        get => _location;
        set
        {
            if (!SetProperty(ref _location, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(LocationInfo));
            OnPropertyChanged(nameof(IsValid));
        }
    }

    /// <summary>Gets whether the location is a position.</summary>
    public bool IsValid => GeoPoint.TryParse(_location, out _);

    /// <summary>Gets what the location is read as, or why it isn't one.</summary>
    public string LocationInfo => GeoPoint.TryParse(_location, out var point)
        ? $"{point}" + (SwissAlti3DSource.SwissBounds.Contains(point) ? " · Switzerland: swisstopo's 0.5 m terrain and 10 cm images" : " · Copernicus 30 m terrain and Sentinel-2 images")
        : "Latitude, longitude (46.6863, 7.8632), with N/E, or Swiss coordinates";

    /// <summary>Picks a site: its location, and its name unless one was typed.</summary>
    public void Choose(TerrainSite site)
    {
        Location = site.Location.ToString();
        if (!_nameEdited) SetProperty(ref _name, site.Name, nameof(Name));
    }

    /// <summary>Gets or sets the style.</summary>
    public TerrainStyle Style
    {
        get => _style;
        set
        {
            // Pressing the selected choice again keeps it (and puts its toggle back).
            if (!SetProperty(ref _style, value)) OnPropertyChanged();
        }
    }

    /// <summary>Gets a style's key numbers, e.g. "4 km · 32 m · 21 tiles · 13k triangles".</summary>
    public static string Summary(TerrainStyle style)
    {
        var layout = new TerrainLayout(style.Settings);
        long triangles = layout.LeafTriangleCount;
        string count = triangles >= 1_000_000 ? $"{triangles / 1e6:0.#}M" : $"{triangles / 1e3:0}k";
        return string.Create(CultureInfo.CurrentCulture,
            $"{layout.Extent / 1000:0.#} km · {layout.Spacing:0.##} m · {layout.Tiles.Count} tiles · {count} triangles");
    }

    /// <summary>Creates the terrain, or <c>null</c> when the location isn't valid.</summary>
    public TerrainNode? CreateNode()
    {
        if (!GeoPoint.TryParse(_location, out var center)) return null;
        return new TerrainNode(_style.Settings with { Center = center })
        {
            Name = string.IsNullOrWhiteSpace(_name) ? "Terrain" : _name.Trim(),
        };
    }
}
