using System.Text.Json.Serialization;

namespace ParagliderToolbox.Airflow.Convection;

/// <summary>
/// One thermal's life, as a run that resolves thermals followed it (see <see cref="ConvectionCensus"/>): an updraft core
/// warmer than the air around it, from its first frame to its last.
/// </summary>
/// <param name="Id">The thermal's number in its census.</param>
/// <param name="Start">When it was first seen (s of simulated time).</param>
/// <param name="Duration">How long it lived (s): from its first frame to one interval past its last.</param>
/// <param name="X">Where it rose from at first: east (m, the terrain's frame).</param>
/// <param name="Z">Where it rose from at first: south (m).</param>
/// <param name="Ground">The ground's height there (m above sea level).</param>
/// <param name="Bottom">The core's lowest point above the ground, on average over its life (m): near 0 for a thermal still fed from the ground, more for a bubble that left it.</param>
/// <param name="Top">The highest its core reached (m above sea level).</param>
/// <param name="Climb">The mean climb in its core, on average over its life (m/s).</param>
/// <param name="Peak">The strongest climb in its core (m/s).</param>
/// <param name="Diameter">Its core's mean diameter (m).</param>
/// <param name="LeanX">How far its core leans east per meter of height (the core's top beside its bottom).</param>
/// <param name="LeanZ">How far its core leans south per meter of height.</param>
/// <param name="DriftX">How fast it moved east over its life (m/s).</param>
/// <param name="DriftZ">How fast it moved south over its life (m/s).</param>
/// <param name="CloudId">The cumulus it fed (<see cref="CloudLife.Id"/>), or -1.</param>
/// <param name="Cut">Whether it lived before or after the census (its duration is too short).</param>
public sealed record ThermalLife(int Id, double Start, double Duration, float X, float Z, float Ground, float Bottom, float Top, float Climb,
    float Peak, float Diameter, float LeanX, float LeanZ, float DriftX, float DriftZ, int CloudId, bool Cut)
{
    /// <summary>Gets how high its core reached above the ground it rose from (m).</summary>
    [JsonIgnore]
    public float Height => Top - Ground;
}

/// <summary>One cumulus' life: from its first wisp to its last frame.</summary>
/// <param name="Id">The cloud's number in its census.</param>
/// <param name="Start">When it was first seen (s of simulated time).</param>
/// <param name="Duration">How long it lived (s).</param>
/// <param name="X">Where it was when largest: east (m).</param>
/// <param name="Z">Where it was when largest: south (m).</param>
/// <param name="Base">Its lowest base (m above sea level).</param>
/// <param name="Top">Its highest top (m above sea level).</param>
/// <param name="Diameter">Its largest diameter (m, of a circle of its area).</param>
/// <param name="Water">The most water it held (t).</param>
/// <param name="Growth">How long it grew: from its first frame to when it held the most water (s).</param>
/// <param name="ThermalId">The thermal that fed it first (<see cref="ThermalLife.Id"/>), or -1.</param>
/// <param name="Cut">Whether it lived before or after the census.</param>
public sealed record CloudLife(int Id, double Start, double Duration, float X, float Z, float Base, float Top, float Diameter, float Water,
    double Growth, int ThermalId, bool Cut)
{
    /// <summary>Gets its shape over its life (its columns around its center), or <c>null</c> in files from before shapes.</summary>
    public CloudShape? Shape { get; init; }

    /// <summary>Gets how fast it moved east over its life (m/s).</summary>
    public float DriftX { get; init; }

    /// <summary>Gets how fast it moved south over its life (m/s).</summary>
    public float DriftZ { get; init; }
}

/// <summary>
/// A cumulus' shape over its life: the columns it filled around its center (which moved with it), each with when in the
/// cloud's life it was cloudy, its bottom and top above the cloud's base, and its most water. A wisp's few columns
/// appear first, the cloud spreads and builds, and its columns vanish as it decays.
/// </summary>
/// <param name="Columns">The grid's columns from west to east.</param>
/// <param name="Rows">The grid's rows from north to south.</param>
/// <param name="CellSize">A column's width (m).</param>
/// <param name="West">The grid's west edge from the cloud's center (m).</param>
/// <param name="North">The grid's north edge from the cloud's center (m).</param>
/// <param name="Appear">When each column became cloudy, as a share of the cloud's life (0–255 for 0–1; row by row from north).</param>
/// <param name="Vanish">When each column cleared (0–255); one that never was cloudy has both 0.</param>
/// <param name="Bottom">Each column's lowest cloud above the cloud's base (steps of <see cref="HeightStep"/>).</param>
/// <param name="Top">Each column's highest cloud above the cloud's base (steps of <see cref="HeightStep"/>); 0 for none.</param>
/// <param name="Water">Each column's most cloud water (steps of <see cref="WaterStep"/> kg/m²); 0 for none.</param>
public sealed record CloudShape(int Columns, int Rows, float CellSize, float West, float North, byte[] Appear, byte[] Vanish, byte[] Bottom,
    byte[] Top, byte[] Water)
{
    /// <summary>The height of a <see cref="Bottom"/> or <see cref="Top"/> step (m).</summary>
    public const float HeightStep = 10;

    /// <summary>The water of a <see cref="Water"/> step (kg/m²).</summary>
    public const float WaterStep = 0.002f;

    /// <summary>Gets whether a column (row by row) is cloudy at a share of the cloud's life (0–1).</summary>
    public bool IsCloudy(int column, double share)
    {
        if (Water[column] == 0) return false;
        double at = share * 255;
        return at >= Appear[column] && at <= Math.Max(Vanish[column], Appear[column] + 1);
    }
}

/// <summary>
/// A place thermals rise from again and again (a trigger): where, how often, and what its thermals are like on average.
/// A game spawns thermals there at <see cref="Rate"/>.
/// </summary>
/// <param name="X">East (m).</param>
/// <param name="Z">South (m).</param>
/// <param name="Ground">The ground's height (m above sea level).</param>
/// <param name="Rate">How many thermals rise from it per hour.</param>
/// <param name="Duration">How long they live (s, the median of those seen whole).</param>
/// <param name="Climb">Their mean climb in the core (m/s).</param>
/// <param name="Peak">Their mean strongest climb (m/s).</param>
/// <param name="Diameter">Their mean core diameter (m).</param>
/// <param name="Top">How high they reach on average (m above sea level).</param>
/// <param name="CloudShare">The share of them that feed a cumulus.</param>
/// <param name="LeanX">How far they lean east per meter of height.</param>
/// <param name="LeanZ">How far they lean south per meter of height.</param>
/// <param name="Count">The thermals seen rising from it.</param>
public sealed record ThermalSite(float X, float Z, float Ground, double Rate, double Duration, float Climb, float Peak, float Diameter, float Top,
    float CloudShare, float LeanX, float LeanZ, int Count);

/// <summary>
/// The thermals and cumulus of a run, followed over its averaged time (see <see cref="ConvectionCensus"/>): every
/// thermal's and every cloud's life, and the sites thermals rise from. What a game spawns its own thermals and clouds by,
/// over the base flow (<see cref="AirflowField.BaseFlow()"/>).
/// </summary>
public sealed class ConvectionClimate
{
    /// <summary>Gets when the census began (s of simulated time).</summary>
    public double Start { get; init; }

    /// <summary>Gets when it ended (s of simulated time).</summary>
    public double End { get; init; }

    /// <summary>Gets the time between its frames (s).</summary>
    public double Interval { get; init; }

    /// <summary>Gets the area it looked at (m²).</summary>
    public double Area { get; init; }

    /// <summary>Gets every thermal's life.</summary>
    public IReadOnlyList<ThermalLife> Thermals { get; init; } = [];

    /// <summary>Gets every cumulus' life.</summary>
    public IReadOnlyList<CloudLife> Clouds { get; init; } = [];

    /// <summary>Gets the sites thermals rise from again and again, the busiest first.</summary>
    public IReadOnlyList<ThermalSite> Sites { get; init; } = [];

    /// <summary>The steps of the life cycles: tenths of a life.</summary>
    public const int CycleSteps = 10;

    /// <summary>
    /// Gets how a thermal's climb goes over its life, on average: in tenths of its life (<see cref="CycleSteps"/>), as a
    /// share of its mean climb (NaN where none was seen). A game shapes its thermals' lives by it.
    /// </summary>
    public float[] ThermalCycle { get; init; } = [];

    /// <summary>Gets how a cumulus' water goes over its life, on average: in tenths of its life, as a share of its most.</summary>
    public float[] CloudCycle { get; init; } = [];

    /// <summary>Gets the time it watched (s).</summary>
    [JsonIgnore]
    public double ObservedTime => End - Start;

    /// <summary>Gets how many thermals rose per hour (not those there when it began).</summary>
    [JsonIgnore]
    public double ThermalRate => Rate(Thermals.Count(t => t.Start > Start));

    /// <summary>Gets how many cumulus formed per hour.</summary>
    [JsonIgnore]
    public double CloudRate => Rate(Clouds.Count(c => c.Start > Start));

    /// <summary>Gets the share of the thermals that fed a cumulus.</summary>
    [JsonIgnore]
    public double CloudShare => Thermals.Count == 0 ? 0 : (double)Thermals.Count(t => t.CloudId >= 0) / Thermals.Count;

    /// <summary>Gets the median of a quantity of the thermals seen whole (or NaN for none).</summary>
    public double ThermalMedian(Func<ThermalLife, double> quantity) => Median(Thermals.Where(t => !t.Cut).Select(quantity));

    /// <summary>Gets the median of a quantity of the clouds seen whole (or NaN for none).</summary>
    public double CloudMedian(Func<CloudLife, double> quantity) => Median(Clouds.Where(c => !c.Cut).Select(quantity));

    private double Rate(int count) => ObservedTime > 0 ? count / ObservedTime * 3600 : 0;

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
