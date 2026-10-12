using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Airflow;
using Atelier.Core.Threading;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Gpu;
using ParagliderToolbox.Modules.Terrain;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// The choices for a new airflow over a terrain: the sounding (a preset, which sets its season's date), the wind, the
/// time of day and the detail; <see cref="CreateNode"/> builds the node. The new-airflow dialog binds to it.
/// </summary>
public sealed partial class NewAirflowOptions : ObservableObject
{
    /// <summary>The compass directions offered, with the degrees the wind comes from.</summary>
    public static IReadOnlyList<(string Name, double Degrees)> Directions { get; } =
        [("N", 0), ("NE", 45), ("E", 90), ("SE", 135), ("S", 180), ("SW", 225), ("W", 270), ("NW", 315)];

    private readonly TerrainNode? _terrain;
    private SoundingPreset _preset = SoundingPresets.Find(null);
    private string _name = "";
    private bool _nameEdited;
    private string _windDirection = "270", _windSpeed = "15", _time = "14:00";
    private AirflowDetail _detail = AirflowDetail.Normal;

    /// <summary>Initializes the choices for an airflow over <paramref name="terrain"/>.</summary>
    public NewAirflowOptions(TerrainNode? terrain)
    {
        _terrain = terrain;
        _name = DefaultName();
        // The estimates depend on whether the solver runs on a graphics card (looking for it takes a few seconds).
        if (AirflowGpu.IsKnownAvailable is null)
        {
            Task.Run(() => AirflowGpu.IsAvailable).ContinueWith(_ => Dispatcher.Post(() => OnPropertyChanged(nameof(HasGraphicsCard))), TaskScheduler.Default);
        }
    }

    /// <summary>Gets whether the solver runs on a graphics card: <c>null</c> while it is looked for.</summary>
    public bool? HasGraphicsCard => AirflowGpu.IsKnownAvailable;

    /// <summary>Gets the terrain.</summary>
    public TerrainNode? Terrain => _terrain;

    /// <summary>Gets or sets the name (made of the sounding and the time until edited).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty)) _nameEdited = true;
        }
    }

    /// <summary>Gets or sets the sounding preset (its wind, for the föhn, becomes the wind).</summary>
    public SoundingPreset Preset
    {
        get => _preset;
        set
        {
            if (!SetProperty(ref _preset, value)) return;
            if (value.WindDirection is { } direction) WindDirection = direction.ToString(CultureInfo.InvariantCulture);
            if (value.WindSpeed is { } speed) WindSpeed = Math.Round(speed * 3.6).ToString(CultureInfo.InvariantCulture);
            UpdateName();
        }
    }

    /// <summary>Gets or sets the direction the wind comes from (degrees, as typed).</summary>
    public string WindDirection
    {
        get => _windDirection;
        set
        {
            if (SetProperty(ref _windDirection, value ?? "")) OnPropertyChanged(nameof(Direction));
        }
    }

    /// <summary>Gets the direction as a number, or <c>null</c> when it isn't one.</summary>
    public double? Direction => double.TryParse(_windDirection, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? ((d % 360) + 360) % 360 : null;

    /// <summary>Gets or sets the wind speed (km/h, as typed).</summary>
    public string WindSpeed { get => _windSpeed; set => SetProperty(ref _windSpeed, value ?? ""); }

    /// <summary>Gets or sets the local time (as typed, 14:00).</summary>
    public string Time
    {
        get => _time;
        set
        {
            if (!SetProperty(ref _time, value ?? "")) return;
            UpdateName();
            _summaries = null;
            OnPropertyChanged(nameof(Summaries));
        }
    }

    /// <summary>Gets or sets the detail.</summary>
    public AirflowDetail Detail { get => _detail; set => SetProperty(ref _detail, value); }

    /// <summary>Gets the summary of every preset at the chosen time (its season's date, over flat meadows): thermal top and cloud base.</summary>
    public IReadOnlyDictionary<string, string> Summaries =>
        _summaries ??= SoundingPresets.All.ToDictionary(p => p.Id, p =>
        {
            var node = CreateNode(p);
            return node.Date + ": " + AirflowNode.Describe(p, node.Settings(_terrain));
        });

    private Dictionary<string, string>? _summaries;

    /// <summary>Gets what <paramref name="detail"/> means over this terrain: the cell size and an estimate of the time.</summary>
    public string DetailInfo(AirflowDetail detail)
    {
        var node = new AirflowNode();
        node.ApplyDetail(detail, _terrain?.Layout.Extent ?? 10240);
        return string.Create(CultureInfo.CurrentCulture, $"{node.CellSize:0} m cells, {node.Duration:0} min, {node.Estimate}");
    }

    /// <summary>Gets why the choices can't make an airflow, or <c>null</c> when they can.</summary>
    public string? Problem =>
        Direction is null ? $"'{_windDirection}' isn't a direction (degrees: 270 for a west wind)."
        : !double.TryParse(_windSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) || speed < 0 ? $"'{_windSpeed}' isn't a wind speed (km/h)."
        : !AirflowNode.TryParseTime(_time, out _) ? $"'{_time}' isn't a time of day (14:00)."
        : null;

    /// <summary>Creates the airflow node of the choices (with <paramref name="preset"/> instead of the chosen one, when given).</summary>
    public AirflowNode CreateNode(SoundingPreset? preset = null)
    {
        preset ??= _preset;
        var node = new AirflowNode { Name = string.IsNullOrWhiteSpace(_name) ? DefaultName() : _name.Trim() };
        node.SoundingId = preset.Id;
        node.Month = preset.Month;
        node.Day = preset.Day;
        if (Direction is { } direction) node.WindDirection = direction;
        if (double.TryParse(_windSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)) node.WindSpeed = speed;
        if (AirflowNode.TryParseTime(_time, out double hours)) node.TimeOfDay = hours;
        node.UtcOffset = UtcOffset(_terrain?.Latitude ?? 46.7, _terrain?.Longitude ?? 7.9, preset.Month, preset.Day);
        node.ApplyDetail(_detail, _terrain?.Layout.Extent ?? 10240);
        return node;
    }

    /// <summary>
    /// Gets the usual offset of local time from UTC at a place and day: European summer time from the last Sunday of March
    /// to the last Sunday of October (western European time west of 7.5° W, central up to 22.5° E, eastern east of it);
    /// elsewhere the longitude's hour.
    /// </summary>
    public static double UtcOffset(double latitude, double longitude, int month, int day)
    {
        if (latitude < 34 || latitude > 72 || longitude < -10 || longitude > 40) return Math.Round(longitude / 15);
        // Western (Portugal, the British Isles), central or eastern European time.
        double standard = longitude > 22.5 ? 2 : longitude < -7.5 ? 0 : 1;
        var date = new DateTime(2026, month, Math.Min(day, DateTime.DaysInMonth(2026, month)));
        bool summer = date >= LastSunday(3) && date < LastSunday(10);
        return standard + (summer ? 1 : 0);

        static DateTime LastSunday(int m)
        {
            var last = new DateTime(2026, m, DateTime.DaysInMonth(2026, m));
            return last.AddDays(-(int)last.DayOfWeek);
        }
    }

    private string DefaultName() => $"{_preset.Name}, {_time}";

    private void UpdateName()
    {
        if (_nameEdited) return;
        _name = DefaultName();
        OnPropertyChanged(nameof(Name));
    }
}
