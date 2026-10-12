using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Atelier.Core.Inspection;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Gpu;
using ParagliderToolbox.Airflow.Sun;
using ParagliderToolbox.Airflow.Surface;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Modules.Terrain;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>How finely an airflow is simulated: the cell size (from the terrain's size) and the simulated time.</summary>
public enum AirflowDetail
{
    /// <summary>About 64 cells across, 20 minutes: a first look in seconds.</summary>
    Quick,

    /// <summary>About 100 cells across, 30 minutes.</summary>
    Normal,

    /// <summary>About 150 cells across, 40 minutes: finer thermals and eddies, several times slower.</summary>
    Fine,

    /// <summary>
    /// About 200 cells across (50 m over 10 km), 90 minutes, the last hour averaged: the thermals resolved, rising,
    /// building cumulus and dying in their shadows over time (minutes on a graphics card, hours without).
    /// </summary>
    Thermals,

    /// <summary>The cell size and times as set.</summary>
    Custom,
}

/// <summary>A sounding preset of an airflow (by id), edited with a list of the presets.</summary>
/// <param name="Id">The preset's id.</param>
public sealed record SoundingChoice(string Id);

/// <summary>
/// An airflow simulation over the terrain it belongs to: the weather (a morning sounding and the wind), the date and
/// time (which set the sun), how finely to simulate, and the result (an <see cref="AirflowField"/>), saved with the
/// project so a game can use it.
/// </summary>
/// <remarks>
/// <see cref="SettingsVersion"/> counts the changes, so the detail view knows when the result is out of date. The result
/// is kept in the project file as the airflow file's bytes.
/// </remarks>
[Inspectable]
public partial class AirflowNode : ProjectNode, IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>The property categories in display order.</summary>
    public static readonly string[] CategoryOrder = ["Weather", "Time", "Simulation", "Outlook", "Result", "Over time"];

    private string _soundingId = SoundingPresets.Find(null).Id;
    private double _windDirection = 270, _windSpeed = 15;
    private int _month = 7, _day = 15;
    private double _timeOfDay = 14, _utcOffset = 2;
    private AirflowDetail _detail = AirflowDetail.Normal;
    private double _cellSize = 100, _layerHeight = 50, _topClearance = 2500, _duration = 30, _averaging = 10;
    private bool _heating = true, _clouds = true, _cloudShadows = true;
    private AirflowField? _result;
    private int _resultVersion;
    private bool _loading;

    /// <summary>Initializes an airflow of a classic summer afternoon with a light west wind.</summary>
    public AirflowNode()
    {
        Name = "Airflow";
    }

    // While loading, the saved values are taken as they are: the detail level doesn't recompute them, and they don't
    // switch it to Custom; the saved result is of the saved settings.
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

    /// <summary>Gets the terrain the airflow belongs to, or <c>null</c>.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public TerrainNode? Terrain => Parent as TerrainNode;

    /// <summary>Gets the chosen sounding preset.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public SoundingPreset Preset => SoundingPresets.Find(_soundingId);

    /// <summary>Gets or sets the result, or <c>null</c> before a simulation.</summary>
    [InspectableIgnore]
    public AirflowField? Result
    {
        get => _result;
        set
        {
            if (ReferenceEquals(_result, value)) return;
            // Another result's film is gone (SetResult sets the new one).
            Film = null;
            SetProperty(ref _result, value);
            _resultVersion = SettingsVersion;
            foreach (string name in ResultNames) OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsOutdated));
        }
    }

    /// <summary>Gets the result's film (the run over time, kept while the app runs, not saved), or <c>null</c>.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public AirflowFilm? Film { get; private set; }

    /// <summary>Sets the result and its film.</summary>
    public void SetResult(AirflowField? result, AirflowFilm? film)
    {
        Result = result;
        Film = film;
        OnPropertyChanged(nameof(Film));
    }

    /// <summary>Gets whether the result is of other settings than the current ones.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public bool IsOutdated => _result != null && _resultVersion != SettingsVersion;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        SettingsVersion++;
        foreach (string summary in EstimateNames) OnPropertyChanged(summary);
        OnPropertyChanged(nameof(IsOutdated));
        return true;
    }

    private static readonly string[] EstimateNames = [nameof(Grid), nameof(Estimate), nameof(Sun), nameof(Expected), nameof(Lakes)];

    private static readonly string[] ResultNames =
        [nameof(Simulated), nameof(Thermals), nameof(BestClimb), nameof(ThermalTops), nameof(CloudBase), nameof(ComputeTime), nameof(ResultGrid),
            nameof(Followed), nameof(ThermalLives), nameof(CloudLives), nameof(Sites), nameof(BaseFlow)];

    /// <summary>Gets the simulation settings over <paramref name="terrain"/> (its center sets the sun).</summary>
    public AirflowSettings Settings(TerrainNode? terrain)
    {
        var preset = Preset;
        return new AirflowSettings
        {
            WindDirection = _windDirection,
            WindSpeed = _windSpeed / 3.6,
            LocalTime = LocalTime,
            UtcOffset = _utcOffset,
            Latitude = terrain?.Latitude ?? 46.6863,
            Longitude = terrain?.Longitude ?? 7.8632,
            Sounding = preset.Sounding,
            SoundingName = preset.Name,
            CellSize = _cellSize,
            LayerHeight = _layerHeight,
            TopClearance = _topClearance,
            Duration = _duration * 60,
            AveragingTime = Math.Min(_averaging, _duration) * 60,
            Heating = _heating,
            Moisture = _clouds,
            CloudShadows = _cloudShadows,
            // Cells fine enough to resolve the thermals: the air entering on the upwind sides convects at once.
            InflowTurbulence = _cellSize <= ResolvedThermals ? 0.3 : 0,
            // … and their thermals and clouds are followed, with the base flow between them.
            TrackConvection = _cellSize <= ResolvedThermals,
        };
    }

    /// <summary>The largest cell size that resolves thermals (m): their cores are a few hundred meters wide.</summary>
    public const double ResolvedThermals = 75;

    private DateTime LocalTime => new DateTime(2026, _month, Math.Min(_day, DateTime.DaysInMonth(2026, _month))).AddHours(_timeOfDay);

    #region Weather

    [InspectableProperty("Sounding", "Weather", Order = 1,
        Description = "The morning sounding: how stable the air is and how moist, so how high thermals go and where clouds form. Choosing one sets its season's date (and the föhn's wind).")]
    [JsonIgnore]
    public SoundingChoice Sounding
    {
        get => new(_soundingId);
        set
        {
            if (value is null || !Set(ref _soundingId, SoundingPresets.Find(value.Id).Id, nameof(SoundingId))) return;
            OnPropertyChanged(nameof(Sounding));
            OnPropertyChanged(nameof(SoundingInfo));
            var preset = Preset;
            Date = new DateTime(2026, preset.Month, preset.Day).ToString("d MMMM", CultureInfo.InvariantCulture);
            if (preset.WindDirection is { } direction) WindDirection = direction;
            if (preset.WindSpeed is { } speed) WindSpeed = Math.Round(speed * 3.6);
        }
    }

    // The choice is saved as the preset's id.
    [InspectableIgnore]
    public string SoundingId
    {
        get => _soundingId;
        set
        {
            if (Set(ref _soundingId, SoundingPresets.Find(value).Id)) OnPropertyChanged(nameof(Sounding));
        }
    }

    [InspectableProperty("About it", "Weather", Order = 2, IsReadOnly = true)]
    public string SoundingInfo => Preset.Description;

    [InspectableProperty("Wind from (°)", "Weather", Order = 3,
        Description = "Where the wind comes from, clockwise from north: 0 north, 90 east, 180 south, 270 west.")]
    public double WindDirection { get => _windDirection; set => Set(ref _windDirection, ((Math.Round(value) % 360) + 360) % 360); }

    [InspectableProperty("Wind speed (km/h)", "Weather", Order = 4,
        Description = "The wind above the ground's friction, about 800 m above the ground; near the ground and in the valleys it is weaker. 0 for a calm day.")]
    public double WindSpeed { get => _windSpeed; set => Set(ref _windSpeed, Math.Clamp(value, 0, 120)); }

    #endregion

    #region Time

    [InspectableProperty("Date", "Time", Order = 10, Description = "The day: 15 July, 15.7. or 2026-07-15. It sets the sun's path (the year doesn't matter).")]
    [JsonIgnore]
    public string Date
    {
        get => new DateTime(2026, _month, Math.Min(_day, DateTime.DaysInMonth(2026, _month))).ToString("d MMMM", CultureInfo.InvariantCulture);
        set
        {
            if (!TryParseDate(value, out var date)) return;
            Month = date.Month;
            Day = date.Day;
        }
    }

    [InspectableIgnore]
    public int Month
    {
        get => _month;
        set
        {
            if (Set(ref _month, Math.Clamp(value, 1, 12))) OnPropertyChanged(nameof(Date));
        }
    }

    [InspectableIgnore]
    public int Day
    {
        get => _day;
        set
        {
            if (Set(ref _day, Math.Clamp(value, 1, 31))) OnPropertyChanged(nameof(Date));
        }
    }

    [InspectableProperty("Time", "Time", Order = 11, Description = "The local time of the afternoon to simulate, as 14:00. The sun and the day's heating until then set the thermals.")]
    [JsonIgnore]
    public string Time
    {
        get => TimeSpan.FromHours(_timeOfDay).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        set
        {
            if (TryParseTime(value, out double hours)) TimeOfDay = hours;
        }
    }

    [InspectableIgnore]
    public double TimeOfDay
    {
        get => _timeOfDay;
        set
        {
            if (Set(ref _timeOfDay, Math.Clamp(value, 0, 23.99))) OnPropertyChanged(nameof(Time));
        }
    }

    [InspectableProperty("UTC offset (h)", "Time", Order = 12, Description = "The local time's offset from UTC: 2 for central European summer time, 1 in winter.")]
    public double UtcOffset { get => _utcOffset; set => Set(ref _utcOffset, Math.Clamp(value, -12, 14)); }

    #endregion

    #region Simulation

    [InspectableProperty("Detail", "Simulation", Order = 20,
        Description = "Quick (64 cells across the terrain, 20 min), Normal (100, 30 min), Fine (150, 40 min) or Thermals (200, 90 min: the thermals and their cumulus coming and going); editing the cell size or the times makes it Custom.")]
    [JsonPropertyOrder(1)]
    public AirflowDetail Detail
    {
        get => _detail;
        set
        {
            if (_loading)
            {
                _detail = value;
                return;
            }
            ApplyDetail(value, Terrain?.Layout.Extent ?? 10240);
        }
    }

    /// <summary>Sets the detail and, unless it is Custom, its cell size and times over a terrain of <paramref name="extent"/> meters.</summary>
    public void ApplyDetail(AirflowDetail detail, double extent)
    {
        Set(ref _detail, detail, nameof(Detail));
        if (detail == AirflowDetail.Custom) return;
        var (cell, layer, duration, averaging, clearance) = DetailSettings(detail, extent);
        SetQuietly(ref _cellSize, cell, nameof(CellSize));
        SetQuietly(ref _layerHeight, layer, nameof(LayerHeight));
        SetQuietly(ref _duration, duration, nameof(Duration));
        SetQuietly(ref _averaging, averaging, nameof(Averaging));
        SetQuietly(ref _topClearance, clearance, nameof(TopClearance));
        foreach (string summary in EstimateNames) OnPropertyChanged(summary);
    }

    /// <summary>
    /// Gets the cell size, layer height, times and the height above the terrain of a detail level over a terrain of
    /// <paramref name="extent"/> meters. Thermals reaches higher: its cumulus grow up to the sounding's stable layer, and
    /// the damping layer in the grid's top quarter mustn't cut them.
    /// </summary>
    public static (double CellSize, double LayerHeight, double Duration, double Averaging, double TopClearance) DetailSettings(AirflowDetail detail, double extent)
    {
        var (across, duration, averaging) = detail switch
        {
            AirflowDetail.Quick => (64, 20.0, 8.0),
            AirflowDetail.Fine => (150, 40.0, 12.0),
            AirflowDetail.Thermals => (200, 90.0, 60.0),
            _ => (100, 30.0, 10.0),
        };
        double cell = Math.Clamp(Math.Round(extent / across / 5) * 5, 25, 500);
        double layer = Math.Max(20, Math.Round(cell / 2 / 5) * 5);
        return (cell, layer, duration, averaging, detail == AirflowDetail.Thermals ? 3500 : 2500);
    }

    // Applies the detail level's value without switching to Custom.
    private void SetQuietly(ref double field, double value, string name)
    {
        if (!SetProperty(ref field, value, name)) return;
        SettingsVersion++;
    }

    // Editing an explicit setting makes the detail Custom.
    private void SetExplicit(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (_loading)
        {
            field = value;
            return;
        }
        if (!Set(ref field, value, name)) return;
        if (_detail != AirflowDetail.Custom) Set(ref _detail, AirflowDetail.Custom, nameof(Detail));
    }

    [InspectableProperty("Cell size (m)", "Simulation", Order = 21,
        Description = "The horizontal size of the solver's cells: 50–100 m shows passes and thermals over a 10 km terrain; halving it makes it about 16 times slower.")]
    public double CellSize { get => _cellSize; set => SetExplicit(ref _cellSize, Math.Clamp(value, 10, 2000)); }

    [InspectableProperty("Layer height (m)", "Simulation", Order = 22, Description = "The height of the cells: about half the cell size.")]
    public double LayerHeight { get => _layerHeight; set => SetExplicit(ref _layerHeight, Math.Clamp(value, 5, 500)); }

    [InspectableProperty("Above the terrain (m)", "Simulation", Order = 23,
        Description = "How far the air above the highest ground is simulated: above the thermals' tops, so the flow aloft is free.")]
    public double TopClearance { get => _topClearance; set => SetExplicit(ref _topClearance, Math.Clamp(value, 500, 10000)); }

    [InspectableProperty("Simulated time (min)", "Simulation", Order = 24,
        Description = "How long the flow develops: the wind needs about the time it takes to cross the terrain, thermals 20 minutes or more.")]
    public double Duration { get => _duration; set => SetExplicit(ref _duration, Math.Clamp(value, 1, 240)); }

    [InspectableProperty("Averaged (min)", "Simulation", Order = 25,
        Description = "The time at the end the result averages: the mean wind, the turbulence (how much it varies) and how often the air rises.")]
    public double Averaging { get => _averaging; set => SetExplicit(ref _averaging, Math.Clamp(value, 0.5, 120)); }

    [InspectableProperty("Sun", "Simulation", Order = 26, Description = "Whether the sun heats the ground (thermals); without it only the wind and the stratification act.")]
    public bool Heating { get => _heating; set => Set(ref _heating, value); }

    [InspectableProperty("Clouds", "Simulation", Order = 27, Description = "Whether rising moist air condenses into clouds (their latent heat strengthens the thermals).")]
    public bool Clouds { get => _clouds; set => Set(ref _clouds, value); }

    [InspectableProperty("Cloud shadows", "Simulation", Order = 28,
        Description = "Whether the clouds shade the ground: in a cumulus' shadow the ground's heat fades within minutes and the thermal under it dies (the life cycle of a thermal and its cloud).")]
    public bool CloudShadows { get => _cloudShadows; set => Set(ref _cloudShadows, value); }

    #endregion

    #region Outlook (computed)

    // The terrain's relief isn't known before it is built: about 1.5 km, as in the Alps.
    private (int Nx, int Ny, int Nz) EstimatedGrid
    {
        get
        {
            double extent = Terrain?.Layout.Extent ?? 10240;
            int n = Math.Max(4, (int)(extent / _cellSize));
            return (n, Math.Max(4, (int)Math.Ceiling((1500 + _topClearance) / _layerHeight)), n);
        }
    }

    [InspectableProperty("Grid", "Outlook", Order = 30, IsReadOnly = true, Description = "The solver's cells (the levels depend on the terrain's relief, about 1.5 km assumed).")]
    public string Grid
    {
        get
        {
            var (nx, ny, nz) = EstimatedGrid;
            return string.Create(CultureInfo.CurrentCulture, $"{nx} × {nz} columns, about {ny} levels ({(double)nx * ny * nz / 1e6:0.##}M cells)");
        }
    }

    [InspectableProperty("Time to simulate", "Outlook", Order = 31, IsReadOnly = true,
        Description = "Roughly, on a desktop computer: on its graphics card (about 30 times faster) when it has one the solver can use.")]
    public string Estimate
    {
        get
        {
            var (nx, ny, nz) = EstimatedGrid;
            double steps = _duration * 60 / Math.Min(4, _layerHeight / 8);
            bool card = AirflowGpu.IsKnownAvailable == true;
            double seconds = (double)nx * ny * nz * steps * (card ? 0.009e-6 : 0.25e-6) + 3;
            string where = card ? " on the graphics card" : AirflowGpu.IsKnownAvailable == false ? " (no graphics card)" : "";
            return seconds < 90 ? string.Create(CultureInfo.CurrentCulture, $"about {seconds:0} s{where}")
                : string.Create(CultureInfo.CurrentCulture, $"about {seconds / 60:0} min{where}");
        }
    }

    /// <summary>Updates the estimate once the graphics card has been looked for.</summary>
    internal void OnDeviceKnown() => OnComputedPropertyChanged(nameof(Estimate));

    [InspectableProperty("Sun", "Outlook", Order = 32, IsReadOnly = true, Description = "Where the sun is then (azimuth clockwise from north, elevation).")]
    public string Sun
    {
        get
        {
            var settings = Settings(Terrain);
            var sun = SolarPosition.At(settings.Latitude, settings.Longitude, settings.UtcTime);
            return sun.IsUp ? string.Create(CultureInfo.CurrentCulture, $"{sun.Azimuth:0}°, {sun.Elevation:0}° high") : "below the horizon";
        }
    }

    [InspectableProperty("Lakes", "Outlook", Order = 34, IsReadOnly = true,
        Description = "The lakes' surface temperature in the season (6 K colder per km higher): colder than the afternoon air, they cool it and stop thermals; warmer, as in autumn, they warm it.")]
    public string Lakes => string.Create(CultureInfo.CurrentCulture, $"{SurfaceHeating.WaterTemperature(_month, _day, 500):0} °C at 500 m");

    [InspectableProperty("Expected", "Outlook", Order = 33, IsReadOnly = true,
        Description = "What the sounding makes of the day's sun over flat meadows at its station: the thermals' top and the cloud base.")]
    public string Expected => Describe(Preset, Settings(Terrain));

    /// <summary>Describes what a sounding makes of the day's sun over flat meadows at its station (for the presets' cards).</summary>
    public static string Describe(SoundingPreset preset, AirflowSettings settings)
    {
        if (!settings.Heating) return "no sun: no thermals";
        var flat = HeightField.Sample(0, 0, 100, 2, 2, (_, _) => (float)SoundingPresets.StationHeight);
        var (heat, water) = SurfaceHeating.Accumulate(flat, null, settings.Latitude, settings.Longitude, settings.UtcTime, TimeSpan.FromMinutes(30));
        var layer = BoundaryLayer.Grow(preset.Sounding, SoundingPresets.StationHeight, heat[0], water[0]);
        if (layer.Depth < 200) return "hardly any thermals";
        return layer.HasClouds
            ? string.Create(CultureInfo.CurrentCulture, $"thermals to {layer.Top:#,0} m, cumulus from {layer.CloudBase:#,0} m")
            : string.Create(CultureInfo.CurrentCulture, $"thermals to {layer.Top:#,0} m, blue (no cumulus)");
    }

    #endregion

    #region Result (computed)

    [InspectableProperty("Simulated", "Result", Order = 40, IsReadOnly = true)]
    public string Simulated => _result is not { } r ? "not yet"
        : string.Create(CultureInfo.CurrentCulture,
            $"{r.Info.LocalTime:d MMMM, HH:mm}, {r.Info.Sounding}, wind {r.Info.WindDirection:0}° {r.Info.WindSpeed * 3.6:0} km/h; {r.Info.SimulatedTime / 60:0} min, the last {r.Info.AveragedTime / 60:0} averaged");

    [InspectableProperty("Thermals", "Result", Order = 41, IsReadOnly = true, Description = "Where the air rises on average within the boundary layer.")]
    public int Thermals => _result?.Thermals.Count ?? 0;

    [InspectableProperty("Best climb", "Result", Order = 42, IsReadOnly = true, Description = "The strongest thermal's mean climb in its core (before a glider's own sink).")]
    public string BestClimb => _result?.Thermals.Count > 0 ? string.Create(CultureInfo.CurrentCulture, $"{_result.Thermals.Max(t => t.Climb):0.0} m/s") : "–";

    [InspectableProperty("Thermal tops", "Result", Order = 43, IsReadOnly = true, Description = "The boundary layer's top, the middle half of the terrain's columns.")]
    public string ThermalTops => _result is null ? "–" : Range(_result.BoundaryLayerTop);

    [InspectableProperty("Cloud base", "Result", Order = 44, IsReadOnly = true, Description = "Where rising air condenses, where it reaches that high (cumulus).")]
    public string CloudBase
    {
        get
        {
            if (_result is not { } r) return "–";
            var bases = r.CloudBase.Where((b, i) => !float.IsNaN(b) && b < r.BoundaryLayerTop[i]).ToArray();
            return bases.Length == 0 ? "no cumulus" : Range(bases);
        }
    }

    [InspectableProperty("Computed in", "Result", Order = 45, IsReadOnly = true)]
    public string ComputeTime => _result is null ? "–" : string.Create(CultureInfo.CurrentCulture,
        $"{_result.Info.ComputeTime:0.#} s, {_result.Info.Steps} steps{(_result.Info.Device.Length > 0 ? ", " + _result.Info.Device : "")}");

    [InspectableProperty("Grid", "Result", Order = 46, IsReadOnly = true)]
    public string ResultGrid => _result is not { } r ? "–"
        : string.Create(CultureInfo.CurrentCulture, $"{r.Nx} × {r.Nz} × {r.Ny} cells of {r.CellSize:0} × {r.LayerHeight:0} m");

    #endregion

    #region Over time (computed)

    [InspectableProperty("Followed", "Over time", Order = 50, IsReadOnly = true,
        Description = "A run that resolves thermals (cells of 75 m or less, as detail Thermals) follows its thermals and cumulus through the averaged time: what a game spawns its own by.")]
    public string Followed => _result?.Convection is not { } c ? "not followed: thermals need cells of 75 m or less (detail Thermals)"
        : string.Create(CultureInfo.CurrentCulture, $"{c.Thermals.Count} thermals and {c.Clouds.Count} cumulus in {c.ObservedTime / 60:0} min");

    [InspectableProperty("Thermals", "Over time", Order = 51, IsReadOnly = true,
        Description = "How many rose per hour over the terrain; how long they lived (the median, and the longest tenth from); their core's climb (and the strongest); their core's width.")]
    public string ThermalLives => _result?.Convection is not { } c || c.Thermals.Count == 0 ? "–"
        : string.Create(CultureInfo.CurrentCulture,
            $"{c.ThermalRate:0} an hour, living {c.ThermalMedian(t => t.Duration) / 60:0.#} min (a tenth over {Tenth(c.Thermals.Where(t => !t.Cut).Select(t => t.Duration)) / 60:0}), climbing {c.ThermalMedian(t => t.Climb):0.0} m/s (to {c.ThermalMedian(t => t.Peak):0.0}), {c.ThermalMedian(t => t.Diameter):0} m wide");

    [InspectableProperty("Cumulus", "Over time", Order = 52, IsReadOnly = true,
        Description = "How many formed per hour; how long they lived and grew; their base; the share of the thermals that fed one.")]
    public string CloudLives => _result?.Convection is not { } c || c.Clouds.Count == 0 ? "–"
        : string.Create(CultureInfo.CurrentCulture,
            $"{c.CloudRate:0} an hour, living {c.CloudMedian(k => k.Duration) / 60:0.#} min (growing {c.CloudMedian(k => k.Growth) / 60:0.#} min), base {c.CloudMedian(k => k.Base):#,0} m; {c.CloudShare:P0} of the thermals feed one");

    [InspectableProperty("Thermal sites", "Over time", Order = 53, IsReadOnly = true, Description = "The places thermals rose from again and again, and how often at the busiest.")]
    public string Sites => _result?.Convection is not { } c ? "–"
        : c.Sites.Count == 0 ? "none" : string.Create(CultureInfo.CurrentCulture, $"{c.Sites.Count}, the busiest {c.Sites[0].Rate:0} an hour");

    [InspectableProperty("Base flow", "Over time", Order = 54, IsReadOnly = true,
        Description = "The mean wind of the air between the thermals and clouds (Without thermals, B, shows it): it sinks where the thermals rise on average.")]
    public string BaseFlow
    {
        get
        {
            if (_result is not { HasBaseFlow: true } r) return "–";
            double mean = 0, based = 0;
            int n = 0;
            for (int column = 0; column < r.Nx * r.Nz; column++)
            {
                int j = Math.Clamp((int)((r.GroundHeight[column] + 300 - r.Y0) / r.LayerHeight), 0, r.Ny - 1);
                int f = r.Index(column % r.Nx, j, column / r.Nx);
                mean += r.WindY[f];
                based += r.BaseWindY![f];
                n++;
            }
            return string.Create(CultureInfo.CurrentCulture, $"300 m above the ground it rises {based / n:+0.00;−0.00} m/s on average (the mean {mean / n:+0.00;−0.00})");
        }
    }

    // Where the longest tenth begins.
    private static double Tenth(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[sorted.Length * 9 / 10];
    }

    private static string Range(float[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? "–" : string.Create(CultureInfo.CurrentCulture, $"{sorted[sorted.Length / 4]:#,0}–{sorted[sorted.Length * 3 / 4]:#,0} m");
    }

    #endregion

    /// <summary>Parses a day: "15 July", "15 Jul", "15.7.", "15.07.2026", "2026-07-15" or the current culture's dates.</summary>
    public static bool TryParseDate(string? text, out DateTime date)
    {
        string[] formats = ["d MMMM", "d MMM", "MMMM d", "MMM d", "d.M.", "d.M", "d.M.yyyy", "yyyy-MM-dd", "d MMMM yyyy"];
        text = text?.Trim() ?? "";
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
               || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    /// <summary>Parses a time of day: "14:00", "14:30", "14" or "14.5" (hours).</summary>
    public static bool TryParseTime(string? text, out double hours)
    {
        text = text?.Trim() ?? "";
        if (TimeSpan.TryParseExact(text, [@"h\:mm", @"hh\:mm", @"h\:m"], CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1))
        {
            hours = time.TotalHours;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hours) && hours >= 0 && hours < 24;
    }
}
