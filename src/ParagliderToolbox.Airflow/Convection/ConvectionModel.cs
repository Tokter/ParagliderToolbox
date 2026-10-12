using System.Numerics;

namespace ParagliderToolbox.Airflow.Convection;

/// <summary>
/// Thermals and cumulus for a game, played over an airflow's base flow as the run that made it had them: they rise at
/// its rate, each like one of the thermals it followed (where it rose from, give or take 50 m; how long it lived, its
/// climb, width, height, lean and drift, give or take a little), grow and fade by its climb's life cycle, lean and drift;
/// those whose model fed a cumulus grow one that starts as a wisp, builds and decays by the clouds' life cycle and drifts
/// with the wind at its base. <see cref="WindAt"/> is the base flow with the live thermals' lift.
/// </summary>
/// <remarks>
/// <para>
/// A thermal's vertical wind is its climb at its life's moment, along its leaning axis, falling with the distance from
/// it as a Gaussian to half at its core's radius, and with the height: from half near the ground to its full in the
/// lower fifth, and to nothing over the top fifth. The air between thermals is the base flow, which already sinks where
/// the run's thermals rose on average.
/// </para>
/// <para>
/// The model is deterministic: the same field, seed and times give the same thermals and clouds however the time is cut
/// into <see cref="Advance"/> steps (births are drawn in order, positions follow from the time). It needs a field with
/// <see cref="AirflowField.Convection"/> (a run that followed its thermals); without one it has no thermals and its wind
/// is the mean wind.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var model = new ConvectionModel(AirflowField.Load(stream), seed: 42);
/// // Every frame:
/// model.Advance(deltaTime);
/// Vector3 wind = model.WindAt(x, y, z);
/// foreach (var cloud in model.Clouds) DrawCumulus(cloud.X, cloud.Z, cloud.Base, cloud.TopNow, cloud.DiameterNow, cloud.Share);
/// </code>
/// </example>
public sealed class ConvectionModel
{
    // How far a thermal rises from its model's root (m, the standard deviation), and how much its others vary.
    private const double RootSpread = 40;
    private const double Spread = 0.1;

    // How long a thermal or cloud takes to appear and to vanish at most (s).
    private const double ThermalFade = 60;
    private const double CloudFade = 45;

    // How far from where a thermal's model rose the run's cumulus it may grow formed (m).
    private const double CloudReach = 1500;

    // A cumulus is at most this many times as wide as its thermal's core.
    private const double CloudWidth = 2.5;

    // The height above the ground the thermals' lift is matched to the run's at (m).
    private const double MatchHeight = 300;

    // A thermal's mean strength over its life, its fades included.
    private const double MeanEnvelope = 0.9;

    private readonly AirflowField _field;
    private readonly Random _random;
    private readonly ThermalLife[] _templates;
    private readonly double[] _durations, _cloudChances;
    private readonly CloudLife[][] _nearClouds;
    private readonly float[] _thermalCycle, _cloudCycle;
    private readonly List<LiveThermal> _thermals = [];
    private readonly List<LiveCloud> _clouds = [];
    private readonly List<LiveCloud> _pending = [];
    private double _nextBirth;

    /// <summary>Starts the model over a field, already as busy as the run was (it has played <paramref name="warmUp"/> seconds).</summary>
    /// <param name="field">The airflow, from a run that followed its thermals.</param>
    /// <param name="seed">The random sequence's seed: the same seed gives the same thermals.</param>
    /// <param name="warmUp">How long it plays before its time 0, so thermals and clouds of every age are about (s).</param>
    public ConvectionModel(AirflowField field, int seed = 0, double warmUp = 3600)
    {
        _field = field;
        _random = new Random(seed);
        var climate = field.Convection;
        _templates = climate?.Thermals.Where(t => t.Duration > 0 && t.Height > 0 && t.Diameter > 0).ToArray() ?? [];
        ThermalRate = _templates.Length > 0 ? climate!.ThermalRate : 0;
        _thermalCycle = Normalized(climate?.ThermalCycle, mean: true);
        _cloudCycle = Normalized(climate?.CloudCycle, mean: false);
        (_durations, LifeStretch) = Durations(climate, _templates, ThermalRate);
        // The thermals like those that fed a cumulus grow one as often as cumulus formed over thermals in the run.
        double fed = climate is null || climate.Clouds.Count == 0 ? 0 : climate.CloudRate * climate.Clouds.Count(c => c.ThermalId >= 0) / climate.Clouds.Count;
        double feeding = _templates.Length == 0 ? 0 : ThermalRate * _templates.Count(t => t.CloudId >= 0) / _templates.Length;
        CloudChance = feeding > 0 ? Math.Clamp(fed / feeding, 0, 1) : 0;
        _cloudChances = _templates.Select(t => t.CloudId < 0 ? 0 : CloudChance).ToArray();
        // The run's cumulus over thermals that formed near where each thermal rose (all of them where none did).
        var fedClouds = climate?.Clouds.Where(c => c.ThermalId >= 0 && c.Duration > 0).ToArray() ?? [];
        _nearClouds = _templates.Select(t =>
        {
            var near = fedClouds.Where(c => (c.X - t.X) * (c.X - t.X) + (c.Z - t.Z) * (c.Z - t.Z) < CloudReach * CloudReach).ToArray();
            return near.Length > 0 ? near : fedClouds;
        }).ToArray();
        Skirt = SkirtShare(climate);
        Time = -Math.Max(0, warmUp);
        _nextBirth = Time + NextGap();
        Advance(Math.Max(0, warmUp));
    }

    /// <summary>
    /// Gets the strength of the weak lift around every thermal's core, as a share of its climb (out to three core radii):
    /// so the thermals carry as much air up as the run's carried above its base flow, 300 m above the ground (the run's
    /// updrafts are wider than their cores).
    /// </summary>
    public float Skirt { get; }

    /// <summary>
    /// Gets how much longer the thermals the census cut (alive when it began or ended) are taken to live than it saw: so
    /// many that the rate times the mean life is as many thermals as the run had about.
    /// </summary>
    public double LifeStretch { get; }

    /// <summary>Gets the chance that a thermal like one that fed a cumulus in the run grows one: as often as cumulus formed over thermals there.</summary>
    public double CloudChance { get; }

    // The thermals' lives, those the census cut stretched (see LifeStretch).
    private static (double[] Durations, double Stretch) Durations(ConvectionClimate? climate, ThermalLife[] templates, double rate)
    {
        var durations = templates.Select(t => t.Duration).ToArray();
        if (climate is null || templates.Length == 0 || rate <= 0) return (durations, 1);
        double about = climate.Thermals.Sum(t => t.Duration) / (climate.ObservedTime + climate.Interval);
        double needed = about / (rate / 3600) * templates.Length;
        double whole = templates.Where(t => !t.Cut).Sum(t => t.Duration), cut = templates.Where(t => t.Cut).Sum(t => t.Duration);
        double stretch = cut > 0 ? Math.Clamp((needed - whole) / cut, 1, 10) : 1;
        for (int i = 0; i < templates.Length; i++)
        {
            if (templates[i].Cut) durations[i] *= stretch;
        }
        return (durations, stretch);
    }

    // The skirt's strength that carries the air the run's updrafts carried beyond their cores, 300 m above the ground.
    private float SkirtShare(ConvectionClimate? climate)
    {
        if (climate is null || !_field.HasBaseFlow || _templates.Length == 0 || climate.Area <= 0) return 0;
        // What the run's thermals carried: its mean vertical wind above its base flow.
        double target = 0;
        int columns = _field.Nx * _field.Nz;
        for (int column = 0; column < columns; column++)
        {
            int j = Math.Clamp((int)((_field.GroundHeight[column] + MatchHeight - _field.Y0) / _field.LayerHeight), 0, _field.Ny - 1);
            int f = _field.Index(column % _field.Nx, j, column / _field.Nx);
            target += _field.WindY[f] - _field.BaseWindY![f];
        }
        target /= columns;
        // What the model's carry on average: their cores, and a skirt of strength 1.
        double cores = 0, skirts = 0;
        for (int i = 0; i < _templates.Length; i++)
        {
            var t = _templates[i];
            if (t.Height <= MatchHeight) continue;
            double radius = t.Diameter / 2, lift = t.Climb * LiveThermal.Profile((float)(MatchHeight / t.Height)) * _durations[i];
            cores += lift * Math.PI * radius * radius / Math.Log(2);
            skirts += lift * Math.PI * 9 * radius * radius;
        }
        double perArea = ThermalRate / 3600 / _templates.Length * MeanEnvelope / climate.Area;
        cores *= perArea;
        skirts *= perArea;
        return skirts > 0 ? (float)Math.Clamp((target - cores) / skirts, 0, 1) : 0;
    }

    /// <summary>Gets the field.</summary>
    public AirflowField Field => _field;

    /// <summary>Gets the model's time (s; 0 when it started).</summary>
    public double Time { get; private set; }

    /// <summary>Gets how many thermals rise per hour (the run's), before <see cref="Activity"/>.</summary>
    public double ThermalRate { get; }

    /// <summary>Gets or sets how busy the sky is: 1 as in the run, 0.5 half as many thermals, 0 none.</summary>
    public double Activity { get; set; } = 1;

    /// <summary>Gets the thermals alive now.</summary>
    public IReadOnlyList<LiveThermal> Thermals => _thermals;

    /// <summary>Gets the cumulus alive now.</summary>
    public IReadOnlyList<LiveCloud> Clouds => _clouds;

    /// <summary>Gets how many thermals have risen since the model began (its warm-up's too).</summary>
    public int Born { get; private set; }

    /// <summary>Plays the model on by <paramref name="seconds"/>: thermals rise, grow, drift and die; cumulus grow and decay.</summary>
    public void Advance(double seconds)
    {
        double end = Time + Math.Max(0, seconds);
        // The thermals born on the way, in order (their positions follow from the time, so the steps don't matter).
        while (_nextBirth <= end)
        {
            double birth = _nextBirth;
            _nextBirth += NextGap();
            if (Activity > 0 && _templates.Length > 0) Spawn(birth);
        }
        Time = end;
        foreach (var cloud in _pending.Where(c => c.Born <= end).ToList())
        {
            _pending.Remove(cloud);
            _clouds.Add(cloud);
        }
        _thermals.RemoveAll(t => end > t.Born + t.Duration || !Inside(t.X(end), t.Z(end)));
        _clouds.RemoveAll(c => end > c.Born + c.Duration || !Inside(c.X(end), c.Z(end)));
        foreach (var thermal in _thermals) thermal.Update(end, _field, Envelope(thermal, end));
        foreach (var cloud in _clouds) cloud.Update(end, CloudShare(cloud, end));
    }

    /// <summary>Gets the wind at a point now (m/s): the base flow with the live thermals' lift.</summary>
    public Vector3 WindAt(double x, double y, double z)
    {
        var wind = _field.BaseWindAt(x, y, z);
        wind.Y += LiftAt(x, y, z);
        return wind;
    }

    /// <summary>Gets the live thermals' vertical wind at a point now (m/s), without the base flow.</summary>
    public float LiftAt(double x, double y, double z)
    {
        float lift = 0;
        foreach (var thermal in _thermals) lift += thermal.LiftAt(x, y, z);
        return lift;
    }

    // The time to the next thermal: exponential, at the rate (when there is none, never).
    private double NextGap()
    {
        double rate = ThermalRate * Math.Max(Activity, 1e-3) / 3600;
        if (rate <= 0) return double.PositiveInfinity;
        return -Math.Log(1 - _random.NextDouble()) / rate;
    }

    // A thermal like one of the run's, near where it rose, and its cumulus when its model fed one.
    private void Spawn(double birth)
    {
        int index = _random.Next(_templates.Length);
        var model = _templates[index];
        double x = model.X + Gaussian() * RootSpread, z = model.Z + Gaussian() * RootSpread;
        double Vary() => 1 + (2 * _random.NextDouble() - 1) * Spread;
        double duration = _durations[index] * Vary(), climb = model.Climb * Vary(), diameter = model.Diameter * Vary(), height = model.Height * Vary();
        bool cloudy = _random.NextDouble() < _cloudChances[index];
        if (!Inside(x, z)) return;
        float ground = _field.GroundAt(x, z);
        var thermal = new LiveThermal(model, birth, duration, (float)x, (float)z, ground + (float)height, (float)climb, (float)diameter, model.Bottom,
            Skirt);
        _thermals.Add(thermal);
        Born++;
        if (!cloudy || _field.Convection is not { } climate || model.CloudId >= climate.Clouds.Count) return;

        // Its cumulus forms when the run's did after its thermal rose (by half its life at the latest), where the thermal's
        // axis reaches the cloud's base, and lives as long as the run's (cumulus come and go over a thermal that stays).
        // It is one of the run's cumulus over thermals that formed near where its model rose, drawn evenly (a thermal's own
        // is more often one of the long-lived fields many thermals fed): its life, shape, size, depth, water and drift; its
        // base its own's. Near: a ridge's banks of cloud stay over the ridge, the valley's small cumulus over the valley.
        var own = climate.Clouds[model.CloudId];
        var near = _nearClouds[index];
        var life = near.Length > 0 ? near[_random.Next(near.Length)] : own;
        var cloud = own with
        {
            Duration = life.Duration, Growth = life.Growth, Diameter = life.Diameter, Water = life.Water, Top = own.Base + Math.Max(0, life.Top - life.Base),
            Shape = life.Shape, DriftX = life.DriftX, DriftZ = life.DriftZ,
        };
        double delay = Math.Clamp(own.Start - model.Start, 0, duration / 2);
        double at = birth + delay;
        float rootX = thermal.X(at), rootZ = thermal.Z(at), above = cloud.Base - _field.GroundAt(rootX, rootZ);
        float cx = rootX + model.LeanX * above, cz = rootZ + model.LeanZ * above;
        // It drifts as the run's did (a recorded shape), else with the base flow at its base; without a shape it is no
        // wider than 2.5 times its thermal's core (a dome).
        var wind = cloud.Shape != null ? new Vector3(cloud.DriftX, 0, cloud.DriftZ) : _field.BaseWindAt(cx, cloud.Base, cz);
        double width = (cloud.Shape != null ? cloud.Diameter : Math.Min(cloud.Diameter, CloudWidth * diameter)) * Vary();
        _pending.Add(new LiveCloud(cloud, at, cloud.Duration * Vary(), cx, cz, wind.X, wind.Z, (float)width, thermal));
    }

    private bool Inside(double x, double z) =>
        x >= _field.X0 && x <= _field.X0 + _field.Nx * _field.CellSize && z >= _field.Z0 && z <= _field.Z0 + _field.Nz * _field.CellSize;

    private double Gaussian()
    {
        double u = 1 - _random.NextDouble(), v = _random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    // A thermal's climb at its age: the run's life cycle, fading in and out.
    private float Envelope(LiveThermal thermal, double time)
    {
        double age = time - thermal.Born, share = Math.Clamp(age / thermal.Duration, 0, 1);
        double fade = Math.Min(ThermalFade, thermal.Duration / 4);
        return (float)(Cycle(_thermalCycle, share) * Ramp(age, fade) * Ramp(thermal.Duration - age, fade));
    }

    // A cloud's share of its most water at its age: from a wisp to its most and back.
    private float CloudShare(LiveCloud cloud, double time)
    {
        double age = time - cloud.Born, share = Math.Clamp(age / cloud.Duration, 0, 1);
        double fade = Math.Min(CloudFade, cloud.Duration / 4);
        return (float)(Cycle(_cloudCycle, share) * Ramp(cloud.Duration - age, fade) * Math.Max(0.15, Ramp(age, fade)));
    }

    private static double Ramp(double t, double length)
    {
        if (length <= 0) return 1;
        double s = Math.Clamp(t / length, 0, 1);
        return s * s * (3 - 2 * s);
    }

    // A life cycle at a share of the life (linear between the tenths' middles).
    private static double Cycle(float[] cycle, double share)
    {
        double f = share * cycle.Length - 0.5;
        int i = (int)Math.Floor(f);
        if (i < 0) return cycle[0];
        if (i >= cycle.Length - 1) return cycle[^1];
        double t = f - i;
        return cycle[i] * (1 - t) + cycle[i + 1] * t;
    }

    // A life cycle with its gaps filled, scaled to a mean (or a most) of 1; flat without one.
    private static float[] Normalized(float[]? cycle, bool mean)
    {
        if (cycle is not { Length: > 0 } || cycle.All(float.IsNaN)) return Enumerable.Repeat(1f, ConvectionClimate.CycleSteps).ToArray();
        var filled = (float[])cycle.Clone();
        float last = filled.First(v => !float.IsNaN(v));
        for (int i = 0; i < filled.Length; i++)
        {
            if (float.IsNaN(filled[i])) filled[i] = last;
            last = filled[i];
        }
        float scale = mean ? filled.Average() : filled.Max();
        return scale > 0 ? filled.Select(v => v / scale).ToArray() : Enumerable.Repeat(1f, filled.Length).ToArray();
    }
}

/// <summary>A thermal of a <see cref="ConvectionModel"/>: where it rises now, how high and wide, how strongly.</summary>
public sealed class LiveThermal
{
    internal LiveThermal(ThermalLife model, double born, double duration, float x, float z, float top, float climb, float diameter, float bottom,
        float skirt)
    {
        Skirt = skirt;
        Model = model;
        Born = born;
        Duration = duration;
        RootX = x;
        RootZ = z;
        Top = top;
        Climb = climb;
        Diameter = diameter;
        Bottom = Math.Max(0, bottom);
    }

    /// <summary>Gets the run's thermal it plays.</summary>
    public ThermalLife Model { get; }

    /// <summary>Gets when it rose (s of the model's time).</summary>
    public double Born { get; }

    /// <summary>Gets how long it lives (s).</summary>
    public double Duration { get; }

    /// <summary>Gets where it rose: east (m).</summary>
    public float RootX { get; }

    /// <summary>Gets where it rose: south (m).</summary>
    public float RootZ { get; }

    /// <summary>Gets its top (m above sea level).</summary>
    public float Top { get; }

    /// <summary>Gets its climb in its core at its strongest moment (m/s, on average over its height).</summary>
    public float Climb { get; }

    /// <summary>Gets its core's diameter (m; its lift is half its axis' there).</summary>
    public float Diameter { get; }

    /// <summary>Gets how far above the ground its core begins (m).</summary>
    public float Bottom { get; }

    /// <summary>Gets the weak lift around its core, out to three core radii, as a share of its climb (<see cref="ConvectionModel.Skirt"/>).</summary>
    public float Skirt { get; }

    /// <summary>Gets how far its core leans east and south per meter of height.</summary>
    public Vector2 Lean => new(Model.LeanX, Model.LeanZ);

    /// <summary>Gets where its root is now: east (m).</summary>
    public float X0 { get; private set; }

    /// <summary>Gets where its root is now: south (m).</summary>
    public float Z0 { get; private set; }

    /// <summary>Gets the ground under its root now (m above sea level).</summary>
    public float Ground { get; private set; }

    /// <summary>Gets its climb now (m/s): <see cref="Climb"/> as its life's moment has it.</summary>
    public float Strength { get; private set; }

    /// <summary>Gets its age now (s).</summary>
    public double Age { get; private set; }

    /// <summary>Gets the point of its axis at a height (m above sea level), now.</summary>
    public Vector3 AxisAt(float y) => new(X0 + Model.LeanX * (y - Ground), y, Z0 + Model.LeanZ * (y - Ground));

    // It drifts as far as the run's moved while the census saw it, then stays (a plume over its trigger).
    internal float X(double time) => RootX + Model.DriftX * DriftTime(time);

    internal float Z(double time) => RootZ + Model.DriftZ * DriftTime(time);

    private float DriftTime(double time) => (float)Math.Clamp(time - Born, 0, Model.Duration);

    internal void Update(double time, AirflowField field, float envelope)
    {
        X0 = X(time);
        Z0 = Z(time);
        Ground = field.GroundAt(X0, Z0);
        Age = time - Born;
        Strength = Climb * envelope;
    }

    /// <summary>Gets its vertical wind at a point now (m/s).</summary>
    public float LiftAt(double x, double y, double z)
    {
        float bottom = Ground + Bottom, depth = Top - Ground;
        if (y < bottom || y > Top || depth <= 0 || Strength == 0) return 0;
        var axis = AxisAt((float)y);
        float radius = Diameter / 2, dx = (float)x - axis.X, dz = (float)z - axis.Z, r2 = dx * dx + dz * dz;
        // The core, half at its radius, and the skirt around it; nothing beyond two and a half radii of the one reaching farther.
        float r = r2 / (radius * radius), reach = Skirt > 0 ? 7.5f : 2.5f;
        if (r > reach * reach) return 0;
        float across = MathF.Pow(2, -r) + (Skirt > 0 ? Skirt * MathF.Exp(-r / 9) : 0);
        return Strength * Profile((float)((y - Ground) / depth)) * across;
    }

    // Its climb by its share of its height: half near the ground, full over the lower fifth, nothing at the top; a mean of 1.
    internal static float Profile(float share)
    {
        static float Smooth(float a, float b, float t)
        {
            float s = Math.Clamp((t - a) / (b - a), 0, 1);
            return s * s * (3 - 2 * s);
        }
        return (0.45f + 0.55f * Smooth(0, 0.2f, share)) * (1 - Smooth(0.8f, 1, share)) / 0.845f;
    }
}

/// <summary>A cumulus of a <see cref="ConvectionModel"/>: where it is now, its base and top, how wide, how much of it there is.</summary>
public sealed class LiveCloud
{
    internal LiveCloud(CloudLife model, double born, double duration, float x, float z, float windX, float windZ, float diameter, LiveThermal thermal)
    {
        Model = model;
        Born = born;
        Duration = duration;
        StartX = x;
        StartZ = z;
        WindX = windX;
        WindZ = windZ;
        Diameter = diameter;
        Thermal = thermal;
    }

    /// <summary>Gets the run's cloud it plays.</summary>
    public CloudLife Model { get; }

    /// <summary>Gets the thermal that grew it.</summary>
    public LiveThermal Thermal { get; }

    /// <summary>Gets when it formed (s of the model's time).</summary>
    public double Born { get; }

    /// <summary>Gets how long it lives (s).</summary>
    public double Duration { get; }

    /// <summary>Gets where it formed (m).</summary>
    public float StartX { get; }

    /// <summary>Gets where it formed (m).</summary>
    public float StartZ { get; }

    /// <summary>Gets how it drifts (m/s, the base flow at its base where it formed).</summary>
    public float WindX { get; }

    /// <summary>Gets how it drifts (m/s).</summary>
    public float WindZ { get; }

    /// <summary>Gets its largest diameter (m).</summary>
    public float Diameter { get; }

    /// <summary>Gets its base (m above sea level).</summary>
    public float Base => Model.Base;

    /// <summary>Gets its highest top (m above sea level).</summary>
    public float Top => Model.Top;

    /// <summary>Gets where it is now: east (m).</summary>
    public float X0 { get; private set; }

    /// <summary>Gets where it is now: south (m).</summary>
    public float Z0 { get; private set; }

    /// <summary>Gets how much of it there is now: its share of its most water (0–1; a wisp at first).</summary>
    public float Share { get; private set; }

    /// <summary>Gets its diameter now (m): its area grows with its water.</summary>
    public float DiameterNow => Diameter * MathF.Sqrt(Share);

    /// <summary>Gets its top now (m above sea level).</summary>
    public float TopNow => Base + (Top - Base) * MathF.Sqrt(Share);

    /// <summary>Gets its water now (t).</summary>
    public float Water => Model.Water * Share;

    /// <summary>Gets its shape over its life (the run's cumulus' it plays), or <c>null</c>: then it is a dome of <see cref="DiameterNow"/>.</summary>
    public CloudShape? Shape => Model.Shape;

    /// <summary>Gets the share of its life gone (0–1).</summary>
    public double LifeShare => Duration > 0 ? Math.Clamp(Age / Duration, 0, 1) : 1;

    /// <summary>
    /// Gets its cloudy columns now, from its <see cref="Shape"/>: their centers, bottom and top (m above sea level; the
    /// tops grow and sink with its life cycle) and cloud water (kg/m²). None without a shape.
    /// </summary>
    public IEnumerable<CloudColumn> Columns()
    {
        if (Shape is not { } shape) yield break;
        double share = LifeShare;
        float depth = 0.35f + 0.65f * Share;
        for (int k = 0; k < shape.Rows; k++)
        {
            for (int i = 0; i < shape.Columns; i++)
            {
                int n = k * shape.Columns + i;
                if (!shape.IsCloudy(n, share)) continue;
                float bottom = Base + shape.Bottom[n] * CloudShape.HeightStep, top = Base + shape.Top[n] * CloudShape.HeightStep;
                yield return new CloudColumn(X0 + shape.West + (i + 0.5f) * shape.CellSize, Z0 + shape.North + (k + 0.5f) * shape.CellSize, bottom,
                    bottom + Math.Max(CloudShape.HeightStep, (top - bottom) * depth), shape.Water[n] * CloudShape.WaterStep * Math.Max(0.15f, Share));
            }
        }
    }

    /// <summary>Gets its age now (s).</summary>
    public double Age { get; private set; }

    internal float X(double time) => StartX + WindX * (float)(time - Born);

    internal float Z(double time) => StartZ + WindZ * (float)(time - Born);

    internal void Update(double time, float share)
    {
        X0 = X(time);
        Z0 = Z(time);
        Age = time - Born;
        Share = Math.Clamp(share, 0, 1);
    }
}

/// <summary>A cloudy column of a <see cref="LiveCloud"/> now.</summary>
/// <param name="X">Its center: east (m).</param>
/// <param name="Z">Its center: south (m).</param>
/// <param name="Bottom">Its lowest cloud (m above sea level).</param>
/// <param name="Top">Its highest cloud (m above sea level).</param>
/// <param name="Water">Its cloud water (kg/m²): from a wisp's few grams to a grown cumulus' hundreds.</param>
public readonly record struct CloudColumn(float X, float Z, float Bottom, float Top, float Water);
