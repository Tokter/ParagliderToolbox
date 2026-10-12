namespace ParagliderToolbox.Airflow.Atmosphere;

/// <summary>One level of a <see cref="Sounding"/>.</summary>
/// <param name="Height">The height above sea level (m).</param>
/// <param name="Temperature">The temperature (°C).</param>
/// <param name="DewPoint">The dew point (°C), at most the temperature.</param>
public readonly record struct SoundingLevel(double Height, double Temperature, double DewPoint);

/// <summary>
/// A weather sounding: temperature and dew point by height, as a radiosonde measures them in the morning. It sets the
/// air's stability (how high thermals rise, where inversions stop them) and moisture (where clouds form) for the
/// solver, which starts from it and heats it with the sun.
/// </summary>
/// <remarks>
/// Between the levels the profile is linear; below the lowest level the temperature rises 6.5 K per km with the same dew
/// point spread, above the highest it stays the same. Pressures follow hydrostatically from
/// <see cref="SurfacePressure"/> at the lowest level. The profile is tabulated every 10 m, so lookups are cheap.
/// </remarks>
public sealed class Sounding
{
    private const double TableBottom = -1000, TableTop = 20000, TableStep = 10;
    private const double StandardLapseRate = 0.0065;

    private readonly double[] _temperature, _dewPoint, _pressure, _theta, _mixingRatio;

    /// <summary>Initializes a sounding from its levels (any order; at least one).</summary>
    /// <param name="levels">The levels.</param>
    /// <param name="surfacePressure">The pressure at the lowest level (hPa), or 0 for the standard atmosphere's.</param>
    public Sounding(IEnumerable<SoundingLevel> levels, double surfacePressure = 0)
    {
        Levels = levels.OrderBy(l => l.Height).Select(l => l with { DewPoint = Math.Min(l.DewPoint, l.Temperature) }).ToArray();
        if (Levels.Count == 0) throw new ArgumentException("A sounding needs at least one level.", nameof(levels));
        SurfacePressure = surfacePressure > 0 ? surfacePressure : StandardPressure(Levels[0].Height) / 100;

        int count = (int)((TableTop - TableBottom) / TableStep) + 1;
        _temperature = new double[count];
        _dewPoint = new double[count];
        _pressure = new double[count];
        _theta = new double[count];
        _mixingRatio = new double[count];
        for (int i = 0; i < count; i++)
        {
            var (t, td) = Interpolate(TableBottom + i * TableStep);
            _temperature[i] = t + Thermodynamics.ZeroCelsius;
            _dewPoint[i] = td + Thermodynamics.ZeroCelsius;
        }

        // Hydrostatic pressures from the lowest level, up and down (virtual temperature, layer means).
        int start = Math.Clamp((int)Math.Round((Levels[0].Height - TableBottom) / TableStep), 0, count - 1);
        _pressure[start] = SurfacePressure * 100;
        for (int i = start + 1; i < count; i++) _pressure[i] = _pressure[i - 1] * Math.Exp(-Thermodynamics.Gravity * TableStep / (Thermodynamics.DryAirConstant * LayerVirtual(i - 1, i)));
        for (int i = start - 1; i >= 0; i--) _pressure[i] = _pressure[i + 1] * Math.Exp(Thermodynamics.Gravity * TableStep / (Thermodynamics.DryAirConstant * LayerVirtual(i, i + 1)));
        for (int i = 0; i < count; i++)
        {
            _theta[i] = Thermodynamics.PotentialTemperature(_temperature[i], _pressure[i]);
            _mixingRatio[i] = Thermodynamics.SaturationMixingRatio(_dewPoint[i], _pressure[i]);
        }
    }

    /// <summary>Gets the levels, from low to high.</summary>
    public IReadOnlyList<SoundingLevel> Levels { get; }

    /// <summary>Gets the pressure at the lowest level (hPa).</summary>
    public double SurfacePressure { get; }

    /// <summary>Gets the temperature (K) at <paramref name="height"/> (m above sea level).</summary>
    public double TemperatureAt(double height) => Lookup(_temperature, height);

    /// <summary>Gets the dew point (K) at <paramref name="height"/>.</summary>
    public double DewPointAt(double height) => Lookup(_dewPoint, height);

    /// <summary>Gets the pressure (Pa) at <paramref name="height"/>.</summary>
    public double PressureAt(double height) => Lookup(_pressure, height);

    /// <summary>Gets the potential temperature (K) at <paramref name="height"/>.</summary>
    public double PotentialTemperatureAt(double height) => Lookup(_theta, height);

    /// <summary>Gets the water vapor mixing ratio (kg/kg) at <paramref name="height"/>.</summary>
    public double MixingRatioAt(double height) => Lookup(_mixingRatio, height);

    /// <summary>Gets the air density (kg/m³) at <paramref name="height"/>.</summary>
    public double DensityAt(double height) => Thermodynamics.Density(TemperatureAt(height), PressureAt(height));

    /// <summary>Gets the average lapse rate (K per km, positive when it gets colder upwards) between two heights.</summary>
    public double LapseRate(double from, double to) => (TemperatureAt(from) - TemperatureAt(to)) / (to - from) * 1000;

    /// <summary>Gets the pressure (Pa) of the standard atmosphere at <paramref name="height"/>.</summary>
    public static double StandardPressure(double height) => 101_325 * Math.Pow(1 - 2.25577e-5 * height, 5.25588);

    private double LayerVirtual(int a, int b)
    {
        double t = (_temperature[a] + _temperature[b]) / 2, td = (_dewPoint[a] + _dewPoint[b]) / 2;
        // The mixing ratio from the standard pressure is close enough for the virtual temperature's small correction.
        double q = Thermodynamics.SaturationMixingRatio(td, StandardPressure(TableBottom + a * TableStep));
        return t * (1 + 0.608 * q);
    }

    private (double Temperature, double DewPoint) Interpolate(double height)
    {
        var levels = Levels;
        if (height <= levels[0].Height)
        {
            double t = levels[0].Temperature + StandardLapseRate * (levels[0].Height - height);
            return (t, t - (levels[0].Temperature - levels[0].DewPoint));
        }
        if (height >= levels[^1].Height) return (levels[^1].Temperature, levels[^1].DewPoint);
        int upper = 1;
        while (levels[upper].Height < height) upper++;
        var a = levels[upper - 1];
        var b = levels[upper];
        double f = (height - a.Height) / Math.Max(b.Height - a.Height, 1e-6);
        return (a.Temperature + f * (b.Temperature - a.Temperature), a.DewPoint + f * (b.DewPoint - a.DewPoint));
    }

    private static double Lookup(double[] table, double height)
    {
        double position = (Math.Clamp(height, TableBottom, TableTop) - TableBottom) / TableStep;
        int i = Math.Min((int)position, table.Length - 2);
        double f = position - i;
        return table[i] + f * (table[i + 1] - table[i]);
    }
}
