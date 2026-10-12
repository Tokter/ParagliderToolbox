namespace ParagliderToolbox.Airflow.Atmosphere;

/// <summary>A premade sounding with the day of the year it is typical of (and a wind that goes with it, for some).</summary>
/// <param name="Id">A stable id (saved in projects).</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">What kind of flying day it makes.</param>
/// <param name="Month">The month it is typical of.</param>
/// <param name="Day">The day of the month.</param>
/// <param name="Sounding">The morning sounding.</param>
public sealed record SoundingPreset(string Id, string Name, string Description, int Month, int Day, Sounding Sounding)
{
    /// <summary>Gets the wind direction (degrees it blows from) that typically goes with it, or <c>null</c>.</summary>
    public double? WindDirection { get; init; }

    /// <summary>Gets the wind speed (m/s) that typically goes with it, or <c>null</c>.</summary>
    public double? WindSpeed { get; init; }
}

/// <summary>
/// Premade morning soundings of the northern Alpine foreland (as the radiosonde of Payerne, 490 m, measures them in the
/// early morning): the seasons, and stable and unstable days, from strong spring thermals to a summer inversion, an
/// autumn cold pool and föhn.
/// </summary>
public static class SoundingPresets
{
    private const double Station = 490;

    /// <summary>Gets the presets.</summary>
    public static IReadOnlyList<SoundingPreset> All { get; } =
    [
        new("spring-unstable", "Spring, unstable",
            "Cold air aloft after a front: strong, bumpy thermals and cumulus, a good cross-country day.", 4, 20,
            Make(962, (490, 6, 2), (800, 6.5, 1), (1000, 4.5, 0), (1500, 1.5, -2.5), (2000, -2, -6), (2500, -5.5, -10), (3000, -9, -14),
                (3500, -12.5, -18), (4000, -16, -22), (5000, -23, -30), (6000, -30, -38), (8000, -45, -52), (10000, -58, -65))),
        new("spring-stable", "Spring, inversion",
            "Warm air aloft: an inversion at about 1700 m stops the thermals, smooth and hazy below it.", 5, 10,
            Make(965, (490, 10, 6), (800, 11, 5), (1200, 9, 3), (1600, 7, 1), (1700, 9, -5), (2000, 8, -8), (2500, 5, -12), (3000, 2, -16),
                (4000, -4, -22), (5000, -11, -28), (6000, -18, -34), (8000, -33, -45), (10000, -48, -58))),
        new("summer-classic", "Summer, classic",
            "A fine summer day: good thermals, in the mountains to 3000 m and more, a stable layer above 3800 m.", 7, 15,
            Make(963, (490, 15, 11), (700, 17, 10), (1000, 16, 8), (1500, 12.5, 5), (2000, 9, 2), (2500, 5.5, -1), (3000, 2, -4),
                (3500, -1.5, -8), (3800, -2, -14), (4000, -2.5, -18), (4500, -5.5, -22), (5000, -9, -25), (6000, -16, -30), (8000, -31, -42),
                (10000, -46, -55))),
        new("summer-thunderstorm", "Summer, thundery",
            "Moist and unstable all the way up: strong thermals, low cumulus that grow into showers and thunderstorms.", 7, 25,
            Make(958, (490, 18, 15), (700, 19.5, 14), (1000, 18, 13), (1500, 14.5, 11), (2000, 11, 8), (2500, 7.5, 5), (3000, 4, 2),
                (3500, 0.5, -1.5), (4000, -3, -5), (5000, -10, -13), (6000, -17.5, -21), (7000, -25, -29), (8000, -33, -38), (10000, -49, -55))),
        new("summer-stable", "Summer, high pressure",
            "Subsiding air: a strong inversion at 1800 m, dry and hazy, weak blue thermals.", 8, 10,
            Make(968, (490, 17, 12), (800, 20, 10), (1200, 19, 8), (1700, 16, 5), (1800, 19, -2), (2200, 18, -6), (3000, 12, -10),
                (4000, 5, -15), (5000, -2, -20), (6000, -9, -26), (8000, -25, -38), (10000, -40, -52))),
        new("autumn-inversion", "Autumn, valley inversion",
            "A cold pool in the valleys under warm air: fog or haze below 1000 m, weak thermals above it.", 10, 15,
            Make(966, (490, 5, 4.5), (700, 6, 5), (900, 9, 3), (1000, 11, 0), (1500, 9, -3), (2000, 6, -6), (3000, 0, -12),
                (4000, -6, -18), (5000, -13, -25), (6000, -20, -32), (8000, -35, -45), (10000, -50, -60))),
        new("winter", "Winter, cold and stable",
            "Short days, a low sun and an inversion: hardly any thermals; the wind and the terrain make the lift.", 1, 20,
            Make(970, (490, -2, -4), (800, 1, -6), (1200, 2, -10), (1500, 0, -12), (2000, -3, -14), (3000, -9, -20), (4000, -16, -27),
                (5000, -23, -33), (6000, -30, -40), (8000, -45, -55), (10000, -58, -68))),
        new("foehn", "Föhn",
            "Strong south wind over a stable, dry layer: lee waves and rotors north of the ridges, turbulent valleys.", 3, 25,
            Make(955, (490, 14, 4), (1000, 11, 2), (1500, 8, 0), (2000, 5, -2), (2500, 2.5, -4), (3000, 0, -6), (4000, -5, -11),
                (5000, -11, -17), (6000, -18, -24), (8000, -33, -38), (10000, -48, -55)))
        {
            WindDirection = 180, WindSpeed = 50 / 3.6,
        },
    ];

    /// <summary>Gets the preset with <paramref name="id"/>, or the classic summer day.</summary>
    public static SoundingPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[2];

    private static Sounding Make(double pressure, params (double Height, double Temperature, double DewPoint)[] levels) =>
        new(levels.Select(l => new SoundingLevel(l.Height, l.Temperature, l.DewPoint)), pressure);

    /// <summary>Gets the height of the station the presets were measured at (m).</summary>
    public static double StationHeight => Station;
}
