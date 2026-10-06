using System.Globalization;
using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Paraglider.Polar;

/// <summary>One sample of the continuous recording (SI units: m/s, degrees, s).</summary>
/// <param name="Time">The simulated time.</param>
/// <param name="SpeedBar">The speed bar input (0–1).</param>
/// <param name="Brake">The brake input on both sides (0–1).</param>
/// <param name="Airspeed">The pilot's airspeed.</param>
/// <param name="HorizontalSpeed">The horizontal speed over the ground (no wind).</param>
/// <param name="VerticalSpeed">The vertical speed (negative when sinking).</param>
/// <param name="AngleOfAttack">The center strip's angle of attack.</param>
/// <param name="MinCellPressure">The lowest cell pressure (1 inflated, below 0.4 collapsing).</param>
public readonly record struct PolarSample(
    float Time, float SpeedBar, float Brake, float Airspeed, float HorizontalSpeed, float VerticalSpeed, float AngleOfAttack, float MinCellPressure);

/// <summary>A steady-state point of the polar: the averages over the measuring window of one control setting.</summary>
public sealed record PolarPoint
{
    /// <summary>Gets the setting's name, e.g. "Speed bar 50%" or "Brakes 30%".</summary>
    public string Label { get; init; } = string.Empty;

    public float SpeedBar { get; init; }
    public float Brake { get; init; }

    /// <summary>Gets the mean airspeed (m/s).</summary>
    public float Airspeed { get; init; }

    /// <summary>Gets the mean horizontal speed (m/s).</summary>
    public float HorizontalSpeed { get; init; }

    /// <summary>Gets the mean sink rate (m/s), total-energy compensated: speed lost or gained while measuring counts as height.</summary>
    public float SinkRate { get; init; }

    /// <summary>Gets the glide ratio: horizontal speed / sink rate.</summary>
    public float GlideRatio { get; init; }

    /// <summary>Gets the mean center angle of attack (degrees).</summary>
    public float AngleOfAttack { get; init; }

    /// <summary>Gets the standard deviation of the vertical speed in the window (m/s): large values mean it never settled.</summary>
    public float SinkSpread { get; init; }

    /// <summary>Gets whether the glider flew steadily at this setting (small spread, no stall, cells inflated).</summary>
    public bool IsStable { get; init; }

    /// <summary>Gets whether the glider stalled (or its cells collapsed) at this setting.</summary>
    public bool IsStalled { get; init; }
}

/// <summary>A notable point of the polar (m/s).</summary>
/// <param name="Airspeed">The airspeed.</param>
/// <param name="SinkRate">The sink rate (positive down).</param>
/// <param name="GlideRatio">The glide ratio there.</param>
public readonly record struct PolarValue(float Airspeed, float SinkRate, float GlideRatio);

/// <summary>The key figures of a polar, from the fitted curve through the stable points.</summary>
public sealed record PolarSummary
{
    /// <summary>Gets hands-up flight: no brakes, no speed bar.</summary>
    public PolarValue Trim { get; init; }

    /// <summary>Gets full speed bar.</summary>
    public PolarValue FullSpeed { get; init; }

    /// <summary>Gets the lowest sink rate.</summary>
    public PolarValue MinSink { get; init; }

    /// <summary>Gets the best glide: where the tangent from the origin touches the curve.</summary>
    public PolarValue BestGlide { get; init; }

    /// <summary>Gets the slowest steady flight before the stall.</summary>
    public PolarValue MinSpeed { get; init; }

    /// <summary>Gets the largest steady sink rate (usually at full speed).</summary>
    public PolarValue MaxSink { get; init; }

    /// <summary>Gets the brake input the glider stalled at, or null when it didn't stall.</summary>
    public float? StallBrake { get; init; }

    /// <summary>Gets the speed range: full speed minus the slowest steady speed (m/s).</summary>
    public float SpeedRange => FullSpeed.Airspeed - MinSpeed.Airspeed;

    /// <summary>Gets the coefficients of the fitted sink rate over airspeed, s(v) = c₀ + c₁·v + c₂·v² + c₃·v³.</summary>
    public double[] FitCoefficients { get; init; } = [];

    /// <summary>Evaluates the fitted sink rate at <paramref name="airspeed"/> (m/s).</summary>
    public double FittedSink(double airspeed)
    {
        double s = 0, power = 1;
        foreach (double c in FitCoefficients)
        {
            s += c * power;
            power *= airspeed;
        }
        return s;
    }
}

/// <summary>How a polar is flown.</summary>
public sealed record PolarRecorderSettings
{
    /// <summary>Gets the speed bar settings flown (after trim), e.g. 0.25 … 1.</summary>
    public float[] SpeedBarSteps { get; init; } = [0.25f, 0.5f, 0.75f, 1f];

    /// <summary>Gets the symmetric brake settings flown (after the speed bar), until the glider stalls.</summary>
    public float[] BrakeSteps { get; init; } = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f];

    /// <summary>Gets the time to settle in trim at the start (s).</summary>
    public float StartSeconds { get; init; } = 12;

    /// <summary>Gets the time the controls move from one setting to the next (s).</summary>
    public float RampSeconds { get; init; } = 1.5f;

    /// <summary>Gets the time to settle after the ramp (s).</summary>
    public float SettleSeconds { get; init; } = 8;

    /// <summary>Gets the time the steady values are averaged over (s): long enough to span a slow pitch oscillation.</summary>
    public float MeasureSeconds { get; init; } = 8;

    /// <summary>Gets the interval of the continuous samples (s).</summary>
    public float SampleInterval { get; init; } = 0.25f;

    /// <summary>Gets the simulation step (s).</summary>
    public float StepTime { get; init; } = 1 / 60f;

    /// <summary>Gets the angle of attack (degrees) above which a setting counts as stalled.</summary>
    public float StallAngleOfAttack { get; init; } = 22;

    /// <summary>Gets the vertical speed spread (m/s) below which a setting counts as steady.</summary>
    public float StableSpread { get; init; } = 0.6f;

    /// <summary>
    /// Gets the simulated flight time (s) of the whole sweep. A recording takes less when the glider stalls before the
    /// last brake setting.
    /// </summary>
    public float FlightSeconds =>
        StartSeconds + SettleSeconds + MeasureSeconds + (SpeedBarSteps.Length + BrakeSteps.Length) * (RampSeconds + SettleSeconds + MeasureSeconds);

    /// <summary>Returns <paramref name="count"/> evenly spaced settings up to <paramref name="maximum"/>: 4 and 1 give 0.25, 0.5, 0.75, 1.</summary>
    public static float[] EvenSteps(int count, float maximum = 1) =>
        Enumerable.Range(1, Math.Max(0, count)).Select(i => maximum * i / count).ToArray();

    /// <summary>Formats a control setting as a percentage: 0.25 → "25%", 0.125 → "12.5%".</summary>
    public static string Percent(float setting) => string.Create(CultureInfo.CurrentCulture, $"{setting * 100:0.#}%");
}

/// <summary>
/// A recorded polar: the design it was flown with, the continuous samples, the steady points per control setting and
/// the key figures. Self-contained, so it can be stored with a project and compared with other recordings (for example by
/// an optimizer trying parameter changes).
/// </summary>
public sealed record PolarRecording
{
    /// <summary>Gets when it was recorded.</summary>
    public DateTime Recorded { get; init; } = DateTime.Now;

    /// <summary>Gets the design that was flown.</summary>
    public GliderDesign? Design { get; init; }

    /// <summary>Gets the settings it was flown with.</summary>
    public PolarRecorderSettings Settings { get; init; } = new();

    /// <summary>Gets the proxy's node count (the simulation's resolution).</summary>
    public int ProxyNodes { get; init; }

    /// <summary>Gets the continuous samples.</summary>
    public List<PolarSample> Samples { get; init; } = [];

    /// <summary>Gets the steady points, in the order flown.</summary>
    public List<PolarPoint> Points { get; init; } = [];

    /// <summary>Gets the key figures.</summary>
    public PolarSummary Summary { get; init; } = new();

    /// <summary>Gets whether the recording ran to the end (not canceled).</summary>
    public bool IsComplete { get; init; }
}

/// <summary>CSV exports of a recording (invariant culture, SI units).</summary>
public static class PolarCsv
{
    /// <summary>Writes the steady points: one row per control setting.</summary>
    public static string Points(PolarRecording recording)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var csv = new System.Text.StringBuilder("Setting,SpeedBar,Brake,AirspeedMs,AirspeedKmh,HorizontalSpeedMs,SinkRateMs,GlideRatio,AngleOfAttackDeg,SinkSpreadMs,Steady,Stalled\n");
        foreach (var p in recording.Points)
        {
            csv.AppendLine(string.Create(ci,
                $"{p.Label},{p.SpeedBar:0.###},{p.Brake:0.###},{p.Airspeed:0.###},{p.Airspeed * 3.6:0.##},{p.HorizontalSpeed:0.###},{p.SinkRate:0.###},{p.GlideRatio:0.##},{p.AngleOfAttack:0.##},{p.SinkSpread:0.###},{p.IsStable},{p.IsStalled}"));
        }
        return csv.ToString();
    }

    /// <summary>Writes the continuous samples.</summary>
    public static string Samples(PolarRecording recording)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var csv = new System.Text.StringBuilder("TimeS,SpeedBar,Brake,AirspeedMs,HorizontalSpeedMs,VerticalSpeedMs,AngleOfAttackDeg,MinCellPressure\n");
        foreach (var s in recording.Samples)
        {
            csv.AppendLine(string.Create(ci,
                $"{s.Time:0.###},{s.SpeedBar:0.###},{s.Brake:0.###},{s.Airspeed:0.###},{s.HorizontalSpeed:0.###},{s.VerticalSpeed:0.###},{s.AngleOfAttack:0.##},{s.MinCellPressure:0.###}"));
        }
        return csv.ToString();
    }
}
