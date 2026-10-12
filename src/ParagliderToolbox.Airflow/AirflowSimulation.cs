using System.Diagnostics;
using System.Numerics;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Convection;
using ParagliderToolbox.Airflow.Simulation;
using ParagliderToolbox.Airflow.Sun;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Airflow;

/// <summary>How to simulate an airflow: the weather, the time, the grid and the run.</summary>
public sealed record AirflowSettings
{
    /// <summary>Gets the direction the wind blows from (degrees clockwise from north: 270 is a west wind).</summary>
    public double WindDirection { get; init; } = 270;

    /// <summary>Gets the wind speed above the ground's friction (m/s), reached about 800 m above the ground.</summary>
    public double WindSpeed { get; init; } = 4;

    /// <summary>Gets the local date and time the airflow is of (it sets the sun).</summary>
    public DateTime LocalTime { get; init; } = new(2026, 7, 15, 14, 0, 0);

    /// <summary>Gets the local time's offset from UTC (hours; 2 for summer time in central Europe).</summary>
    public double UtcOffset { get; init; } = 2;

    /// <summary>Gets the latitude of the frame's origin (degrees).</summary>
    public double Latitude { get; init; } = 46.6863;

    /// <summary>Gets the longitude of the frame's origin (degrees).</summary>
    public double Longitude { get; init; } = 7.8632;

    /// <summary>Gets the morning sounding.</summary>
    public Sounding Sounding { get; init; } = SoundingPresets.Find(null).Sounding;

    /// <summary>Gets the sounding's name (for the result's record).</summary>
    public string SoundingName { get; init; } = SoundingPresets.Find(null).Name;

    /// <summary>Gets the horizontal cell size (m).</summary>
    public double CellSize { get; init; } = 100;

    /// <summary>Gets the cell height (m).</summary>
    public double LayerHeight { get; init; } = 50;

    /// <summary>Gets how far the grid reaches above the highest ground (m).</summary>
    public double TopClearance { get; init; } = 2500;

    /// <summary>Gets the simulated time (s).</summary>
    public double Duration { get; init; } = 1800;

    /// <summary>Gets the time at the end the result averages (s).</summary>
    public double AveragingTime { get; init; } = 600;

    /// <summary>Gets the longest time step (s).</summary>
    public double MaxTimeStep { get; init; } = 4;

    /// <summary>Gets whether the sun heats the ground (thermals); without it only the wind and the stratification act.</summary>
    public bool Heating { get; init; } = true;

    /// <summary>Gets whether vapor condenses into clouds.</summary>
    public bool Moisture { get; init; } = true;

    /// <summary>
    /// Gets whether the clouds shade the ground: in a cumulus' shadow the ground's heat falls to the diffuse light's within
    /// a few minutes, which cuts off the thermal under it (the life cycle of a thermal and its cloud).
    /// </summary>
    public bool CloudShadows { get; init; }

    /// <summary>
    /// Gets the amplitude of the warmth noise that sets the air entering on the upwind sides convecting at once (K), or 0
    /// for none: for cells fine enough to resolve thermals (see <see cref="AirflowConditions.InflowNoise"/>).
    /// </summary>
    public double InflowTurbulence { get; init; }

    /// <summary>
    /// Gets whether the run follows its thermals and cumulus over the averaged time (their lives, the sites they rise
    /// from: <see cref="AirflowField.Convection"/>) and keeps the base flow between them (<see cref="AirflowField.BaseFlow()"/>):
    /// for cells fine enough to resolve thermals (see <see cref="ConvectionCensus"/>).
    /// </summary>
    public bool TrackConvection { get; init; }

    /// <summary>
    /// Gets the lakes' surface temperature (°C), or <c>null</c> for the season's (see
    /// <see cref="SurfaceHeating.WaterTemperature"/>: 21 °C in July at 500 m, colder higher up).
    /// </summary>
    public double? WaterTemperature { get; init; }

    /// <summary>Gets the time in UTC.</summary>
    public DateTime UtcTime => DateTime.SpecifyKind(LocalTime.AddHours(-UtcOffset), DateTimeKind.Utc);
}

/// <summary>How far a simulation is.</summary>
/// <param name="Stage">What it is doing.</param>
/// <param name="Time">The simulated time (s).</param>
/// <param name="Duration">The time to simulate (s).</param>
/// <param name="Steps">The steps made.</param>
/// <param name="TimeStep">The last time step (s).</param>
/// <param name="PressureIterations">The pressure solver's iterations in the last step.</param>
/// <param name="Elapsed">The wall-clock time so far.</param>
/// <param name="Snapshot">The flow at this moment (every few seconds), or <c>null</c>.</param>
public sealed record AirflowProgress(string Stage, double Time, double Duration, int Steps, double TimeStep, int PressureIterations, TimeSpan Elapsed,
    AirflowField? Snapshot)
{
    /// <summary>Gets the share done (0–1).</summary>
    public double Fraction => Duration > 0 ? Math.Clamp(Time / Duration, 0, 1) : 0;
}

/// <summary>
/// An airflow simulation over a terrain: classifies the land cover (from aerial image colors, when given), heats the
/// ground with the sun of the chosen time, grows the morning sounding's boundary layer with the day's heat so far, lets
/// the <see cref="AirflowSolver"/> run with the wind and averages the end into an <see cref="AirflowField"/>.
/// </summary>
/// <example>
/// <code>
/// var terrain = HeightField.Sample(-5000, -5000, 25, 401, 401, (x, z) => myTerrain.Height(x, z));
/// var simulation = new AirflowSimulation(terrain, colors: null, new AirflowSettings { WindDirection = 300, WindSpeed = 5 });
/// AirflowField field = simulation.Run();
/// var sample = field.Sample(x, y, z); // sample.Wind, sample.Turbulence, sample.LiftChance …
/// </code>
/// </example>
public sealed class AirflowSimulation : IDisposable
{
    // How far the day's heat spreads before it makes the boundary layer (m): the layer mixes over the area, so a column's
    // layer grows over the area's mean ground with its mean heat (columns grown on their own would differ by kelvins at
    // the same height, which drives violent overturning).
    private const double HeatSpread = 3000;

    // The depth of the cool layer over water colder than the air, at the start (m).
    private const double LakeLayer = 60;

    // The noise in the starting warmth (K) up to this height above the ground (m).
    private const double Perturbation = 0.1;
    private const double Perturbed = 300;

    // The cloud water (kg/kg) a snapshot shows as full cloud: a grown cumulus'.
    private const float FullCloud = 5e-4f;

    /// <summary>Prepares a simulation of <paramref name="terrain"/>.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="colors">The aerial image color of every terrain sample (RGBA, red in the lowest byte; alpha 0 for none), or <c>null</c>.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <param name="solver">Makes the solver (on a graphics card: ParagliderToolbox.Airflow.Gpu), or <c>null</c> for the CPU's <see cref="AirflowSolver"/>.</param>
    public AirflowSimulation(HeightField terrain, uint[]? colors, AirflowSettings settings, CancellationToken cancellationToken = default,
        Func<AirflowGrid, AirflowConditions, IAirflowSolver>? solver = null)
    {
        Settings = settings;
        Terrain = terrain;
        Grid = new AirflowGrid(terrain, settings.CellSize, settings.LayerHeight, settings.TopClearance);
        cancellationToken.ThrowIfCancellationRequested();

        // The land cover of every terrain sample (lakes are flat: the relief within about 100 m tells them).
        Cover = new LandCover[terrain.Heights.Length];
        int reach = Math.Max(1, (int)Math.Round(50 / terrain.Spacing));
        Parallel.For(0, terrain.Rows, row =>
        {
            for (int column = 0; column < terrain.Columns; column++)
            {
                int i = row * terrain.Columns + column;
                double slope = Math.Acos(Math.Clamp(terrain.NormalAt(column, row).Y, -1, 1)) * 180 / Math.PI;
                Cover[i] = colors is null ? LandCover.Meadow : LandCovers.Classify(colors[i], slope, terrain.Heights[i], Relief(terrain, column, row, reach));
            }
        });

        Sun = SolarPosition.At(settings.Latitude, settings.Longitude, settings.UtcTime);
        int nx = Grid.Nx, nz = Grid.Nz, columns = nx * nz;
        ColumnHeat = new float[columns];
        ColumnEvaporation = new float[columns];
        ColumnShadedHeat = new float[columns];
        ColumnShadedEvaporation = new float[columns];
        ColumnRoughness = new float[columns];
        ColumnCover = new byte[columns];
        ColumnWater = new float[columns];
        ColumnWaterTemperature = new float[columns];
        // The sunlight on every sample, in the mountains' shadows, and in a cloud's shadow.
        var shadows = settings.Heating ? SurfaceHeating.Shadows(terrain, Sun) : null;
        var fluxes = settings.Heating ? SurfaceHeating.Fluxes(terrain, Cover, Sun, shadows) : null;
        var shaded = settings.Heating && settings.CloudShadows ? SurfaceHeating.Fluxes(terrain, Cover, Sun, Array.ConvertAll(Cover, _ => true)) : null;
        var local = settings.LocalTime;
        Parallel.For(0, nz, k =>
        {
            var counts = new int[Enum.GetValues<LandCover>().Length];
            for (int i = 0; i < nx; i++)
            {
                Array.Clear(counts);
                double heat = 0, latent = 0, shadedHeat = 0, shadedLatent = 0, logRoughness = 0, waterHeight = 0;
                int n = 0, water = 0;
                foreach (int s in Footprint(i, k))
                {
                    var cover = Cover[s];
                    counts[(int)cover]++;
                    logRoughness += Math.Log(LandCovers.Properties(cover).Roughness);
                    if (cover == LandCover.Water)
                    {
                        water++;
                        waterHeight += terrain.Heights[s];
                    }
                    else if (fluxes != null)
                    {
                        heat += fluxes[s].Sensible;
                        latent += fluxes[s].Latent;
                        if (shaded != null)
                        {
                            shadedHeat += shaded[s].Sensible;
                            shadedLatent += shaded[s].Latent;
                        }
                    }
                    n++;
                }
                int column = k * nx + i;
                // The land's share of the column; the water's heat comes from the solver, by its temperature.
                ColumnHeat[column] = (float)(heat / n);
                ColumnEvaporation[column] = (float)(latent / n / Thermodynamics.LatentHeat);
                ColumnShadedHeat[column] = (float)(shadedHeat / n);
                ColumnShadedEvaporation[column] = (float)(shadedLatent / n / Thermodynamics.LatentHeat);
                ColumnRoughness[column] = (float)Math.Exp(logRoughness / n);
                ColumnCover[column] = (byte)Array.IndexOf(counts, counts.Max());
                ColumnWater[column] = (float)water / n;
                double celsius = settings.WaterTemperature
                    ?? SurfaceHeating.WaterTemperature(local.Month, local.Day, water > 0 ? waterHeight / water : Grid.GroundHeight[column]);
                ColumnWaterTemperature[column] = (float)(celsius + Thermodynamics.ZeroCelsius);
            }
        });
        cancellationToken.ThrowIfCancellationRequested();

        // The boundary layer the day's heat has grown until the simulation starts.
        Layers = new MixedLayer[columns];
        var initialTemperature = new float[Grid.CellCount];
        var initialVapor = new float[Grid.CellCount];
        var sounding = settings.Sounding;
        float[] gathered = new float[columns], evaporated = new float[columns];
        var meanGround = Maps.Smooth(Grid.GroundHeight, nx, nz, (int)Math.Round(HeatSpread / Grid.CellSize));
        if (settings.Heating)
        {
            var columnField = HeightField.Sample(Grid.X0 + Grid.CellSize / 2, Grid.Z0 + Grid.CellSize / 2, Grid.CellSize, nx, nz, terrain.HeightAt);
            var covers = ColumnCover.Select(c => (LandCover)c).ToArray();
            var start = settings.UtcTime.AddSeconds(-settings.Duration / 2);
            (gathered, evaporated) = SurfaceHeating.Accumulate(columnField, covers, settings.Latitude, settings.Longitude, start, TimeSpan.FromMinutes(20));
            int radius = (int)Math.Round(HeatSpread / Grid.CellSize);
            gathered = Maps.Smooth(gathered, nx, nz, radius);
            evaporated = Maps.Smooth(evaporated, nx, nz, radius);
        }
        AccumulatedHeat = gathered;
        Parallel.For(0, columns, column =>
        {
            var area = BoundaryLayer.Grow(sounding, meanGround[column], gathered[column], evaporated[column]);
            double ground = Grid.GroundHeight[column];
            var layer = Layers[column] = area with
            {
                Ground = ground, Top = Math.Max(area.Top, ground),
                GroundTemperature = Math.Max(Thermodynamics.TemperatureOf(area.PotentialTemperature, sounding.PressureAt(ground)), sounding.TemperatureAt(ground)),
            };
            // Over water colder than the afternoon air, the air has cooled from below: a shallow stable layer, at the water
            // half way to its temperature (an afternoon's crossing of a lake).
            double water = ColumnWater[column];
            double waterTheta = Thermodynamics.PotentialTemperature(ColumnWaterTemperature[column], sounding.PressureAt(ground));
            double chill = settings.Heating && water > 0 ? Math.Max(0, layer.PotentialTemperature - waterTheta) / 2 * water : 0;
            for (int j = 0; j < Grid.Ny; j++)
            {
                int c = column * Grid.Ny + j;
                double y = Grid.LevelHeight(j);
                bool mixed = y < layer.Top;
                double warming = mixed ? Math.Max(0, layer.PotentialTemperature - sounding.PotentialTemperatureAt(y)) : 0;
                warming -= chill * Math.Max(0, 1 - (y - ground) / LakeLayer);
                initialTemperature[c] = (float)warming;
                initialVapor[c] = (float)(mixed ? layer.MixingRatio : sounding.MixingRatioAt(y));
            }
        });
        cancellationToken.ThrowIfCancellationRequested();

        // A little noise in the warmth near the ground (the same every run): a perfectly even start convects only from the
        // sides inwards, in rings, after minutes of heating; real ground and air are never even.
        {
            var grid = Grid;
            Parallel.For(0, grid.Nx * grid.Nz, column =>
            {
                double ground = grid.GroundHeight[column];
                for (int j = 0; j < grid.Ny; j++)
                {
                    double above = grid.LevelHeight(j) - ground;
                    if (above > Perturbed) break;
                    int c = column * grid.Ny + j;
                    uint hash = (uint)c * 2654435761u;
                    hash ^= hash >> 15;
                    hash *= 2246822519u;
                    hash ^= hash >> 13;
                    initialTemperature[c] += (float)(((hash & 0xFFFF) / 65535.0 - 0.5) * 2 * Perturbation);
                }
            });
        }

        var conditions = new AirflowConditions
        {
            Sounding = sounding,
            WindDirection = settings.WindDirection,
            WindSpeed = settings.WindSpeed,
            SensibleHeat = ColumnHeat,
            Evaporation = ColumnEvaporation,
            Roughness = ColumnRoughness,
            WaterShare = ColumnWater,
            WaterTemperature = ColumnWaterTemperature,
            InitialTemperature = initialTemperature,
            InitialVapor = initialVapor,
            Moisture = settings.Moisture,
            ShadedHeat = settings.Heating && settings.CloudShadows ? ColumnShadedHeat : null,
            ShadedEvaporation = settings.Heating && settings.CloudShadows ? ColumnShadedEvaporation : null,
            SunDirection = Sun.Direction,
            InflowNoise = settings.InflowTurbulence,
            InflowNoiseDepth = 2.0 / 3 * MixedDepth(),
        };
        Solver = solver?.Invoke(Grid, conditions) ?? new AirflowSolver(Grid, conditions);
    }

    /// <summary>Gets the settings.</summary>
    public AirflowSettings Settings { get; }

    /// <summary>Gets the terrain.</summary>
    public HeightField Terrain { get; }

    /// <summary>Gets the grid.</summary>
    public AirflowGrid Grid { get; }

    /// <summary>Gets the solver.</summary>
    public IAirflowSolver Solver { get; }

    /// <summary>Gets the sun.</summary>
    public SunPosition Sun { get; }

    /// <summary>Gets the land cover of every terrain sample.</summary>
    public LandCover[] Cover { get; }

    /// <summary>Gets the sensible heat flux of every column (W/m²).</summary>
    public float[] ColumnHeat { get; }

    /// <summary>Gets the evaporation of every column (kg/(m² s)).</summary>
    public float[] ColumnEvaporation { get; }

    /// <summary>Gets the sensible heat flux of every column in a cloud's shadow (W/m²: the land's, with the diffuse light only).</summary>
    public float[] ColumnShadedHeat { get; }

    /// <summary>Gets the evaporation of every column in a cloud's shadow (kg/(m² s)).</summary>
    public float[] ColumnShadedEvaporation { get; }

    /// <summary>Gets the roughness length of every column (m).</summary>
    public float[] ColumnRoughness { get; }

    /// <summary>Gets the main land cover of every column.</summary>
    public byte[] ColumnCover { get; }

    /// <summary>Gets the share of every column that is water.</summary>
    public float[] ColumnWater { get; }

    /// <summary>Gets the water's surface temperature of every column (K).</summary>
    public float[] ColumnWaterTemperature { get; }

    /// <summary>Gets the heat gathered since the morning of every column, spread over the area (J/m²).</summary>
    public float[] AccumulatedHeat { get; }

    /// <summary>Gets the mixed layer every column starts with.</summary>
    public MixedLayer[] Layers { get; }

    /// <summary>Gets or sets how often <see cref="Run"/> reports a snapshot of the flow (s of wall-clock time): live views of fine grids want them often.</summary>
    public double SnapshotInterval { get; set; } = 3;

    /// <summary>Runs the simulation to the end and returns the airflow averaged over its last <see cref="AirflowSettings.AveragingTime"/>.</summary>
    /// <param name="progress">Reports the progress (about four times a second, with a snapshot of the flow every <see cref="SnapshotInterval"/>).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public AirflowField Run(IProgress<AirflowProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        double duration = Settings.Duration, averageFrom = Math.Max(0, duration - Settings.AveragingTime);
        var clock = Stopwatch.StartNew();
        double lastReport = -1, lastSnapshot = 0, nextFrame = Solver.Time, nextCensus = averageFrom;
        AirflowCells? frameCells = null;
        if (FilmInterval > 0) Film = new AirflowFilm(Grid.Nx, Grid.Nz, Grid.CellSize, Grid.X0, Grid.Z0, Grid.GroundHeight);
        Census = Settings.TrackConvection ? new ConvectionCensus(Grid, Settings.Sounding) : null;
        while (Solver.Time < duration - 1e-6)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double before = Solver.Time;
            Solver.Step(Math.Min(Settings.MaxTimeStep, duration - Solver.Time));
            if (Solver.Time > averageFrom) Solver.Accumulate(Solver.Time - Math.Max(before, averageFrom));
            // One snapshot serves the film's frame and the census' when both are due.
            bool filming = Film != null && Solver.Time >= nextFrame - 1e-6;
            bool counting = Census != null && Solver.Time >= nextCensus - 1e-6;
            if (filming || counting) Solver.Snapshot(frameCells ??= new AirflowCells(Grid));
            if (filming)
            {
                nextFrame += FilmInterval;
                Film!.Add(FilmFrame(frameCells!));
            }
            if (counting)
            {
                nextCensus += CensusInterval;
                Census!.Add(Solver.Time, frameCells!);
            }
            double now = clock.Elapsed.TotalSeconds;
            if (progress != null && now - lastReport >= 0.25)
            {
                lastReport = now;
                AirflowField? snapshot = null;
                if (now - lastSnapshot >= SnapshotInterval)
                {
                    lastSnapshot = now;
                    snapshot = Snapshot();
                }
                progress.Report(new AirflowProgress("Simulating", Solver.Time, duration, Solver.Steps, Solver.LastTimeStep, Solver.PressureIterations, clock.Elapsed, snapshot));
            }
        }
        var averages = Solver.AveragedTime > 0 ? Solver.Averages() : null;
        var field = averages != null ? Build(averages, Solver.AveragedTime) : Snapshot();
        if (Census is { Frames: > 1 } census)
        {
            progress?.Report(new AirflowProgress("Following", Solver.Time, duration, Solver.Steps, Solver.LastTimeStep, Solver.PressureIterations, clock.Elapsed, null));
            field.Convection = census.Climate();
            if (census.BaseWind(averages ?? Solver.Snapshot()) is { } wind)
            {
                (field.BaseWindX, field.BaseWindY, field.BaseWindZ) = (ToField(wind.X), ToField(wind.Y), ToField(wind.Z));
            }
        }
        field.Info = field.Info with { ComputeTime = clock.Elapsed.TotalSeconds, Device = Solver.Device };
        progress?.Report(new AirflowProgress("Done", Solver.Time, duration, Solver.Steps, Solver.LastTimeStep, Solver.PressureIterations, clock.Elapsed, field));
        return field;
    }

    // An array in the solver's cell layout in the field's (air only).
    private float[] ToField(float[] cells)
    {
        var grid = Grid;
        var field = new float[cells.Length];
        Parallel.For(0, grid.Nz, k =>
        {
            for (int i = 0; i < grid.Nx; i++)
            {
                for (int j = 0; j < grid.Ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (Solver.IsAir(c)) field[(j * grid.Nz + k) * grid.Nx + i] = cells[c];
                }
            }
        });
        return field;
    }

    /// <summary>Gets or sets how often <see cref="Run"/> keeps a frame of the <see cref="Film"/> (s of simulated time), or 0 for no film.</summary>
    public double FilmInterval { get; set; }

    /// <summary>
    /// Gets or sets how often the census looks at the flow over the averaged time (s of simulated time), with
    /// <see cref="AirflowSettings.TrackConvection"/>: often enough that a thermal moves less than its width between looks.
    /// </summary>
    public double CensusInterval { get; set; } = 30;

    /// <summary>Gets the census of the run's thermals and clouds (from <see cref="Run"/>, with <see cref="AirflowSettings.TrackConvection"/>), or <c>null</c>.</summary>
    public ConvectionCensus? Census { get; private set; }

    /// <summary>Gets the film of the run (from <see cref="Run"/>, with <see cref="FilmInterval"/>), or <c>null</c>.</summary>
    public AirflowFilm? Film { get; private set; }

    // A frame of the film: every column's lift at the film's heights, its cloud and its sunshine.
    private AirflowFilmFrame FilmFrame(AirflowCells cells)
    {
        var grid = Grid;
        int columns = grid.Nx * grid.Nz, ny = grid.Ny;
        var heights = AirflowFilm.LiftHeights;
        var lift = new sbyte[heights.Length * columns];
        var bases = new ushort[columns];
        var tops = new ushort[columns];
        var water = new byte[columns];
        var sunshine = new byte[columns];
        var density = new float[ny];
        for (int j = 0; j < ny; j++) density[j] = (float)Settings.Sounding.DensityAt(grid.LevelHeight(j));
        Parallel.For(0, columns, column =>
        {
            int first = column * ny;
            double ground = grid.GroundHeight[column];
            for (int n = 0; n < heights.Length; n++)
            {
                // Between the levels around the height.
                double f = Math.Clamp((ground + heights[n] - grid.Y0) / grid.LayerHeight - 0.5, 0, ny - 1);
                int j = Math.Min((int)f, ny - 2);
                float t = (float)(f - j), w = cells.Y[first + j] * (1 - t) + cells.Y[first + j + 1] * t;
                lift[n * columns + column] = (sbyte)Math.Clamp(Math.Round(w / AirflowFilm.LiftStep), sbyte.MinValue, sbyte.MaxValue);
            }
            double path = 0;
            int bottom = -1, top = -1;
            for (int j = 0; j < ny; j++)
            {
                float q = cells.CloudWater[first + j];
                if (q <= Simulation.AirflowSetup.CloudThreshold) continue;
                if (bottom < 0) bottom = j;
                top = j;
                path += q * density[j] * grid.LayerHeight;
            }
            if (bottom >= 0)
            {
                bases[column] = (ushort)Math.Clamp(grid.Y0 + bottom * grid.LayerHeight, 1, ushort.MaxValue);
                tops[column] = (ushort)Math.Clamp(grid.Y0 + (top + 1) * grid.LayerHeight, 1, ushort.MaxValue);
                water[column] = (byte)Math.Clamp(Math.Ceiling(path / AirflowFilm.WaterStep), 1, 255);
            }
            float heat = ColumnHeat[column];
            sunshine[column] = heat > 30 ? (byte)Math.Clamp(Math.Round(cells.SurfaceHeat[column] / heat * 255), 0, 255) : (byte)255;
        });
        return new AirflowFilmFrame(Solver.Time, lift, bases, tops, water, sunshine);
    }

    /// <summary>Gets the flow at this moment as a field (no averages: the turbulence is the sub-grid part, the lift and cloud either 0 or 1).</summary>
    public AirflowField Snapshot() => Build(Solver.Snapshot(), 0);

    /// <summary>Gets the running averages so far as a field (with its thermals), or a snapshot when nothing is averaged yet.</summary>
    public AirflowField Averaged() => Solver.AveragedTime > 0 ? Build(Solver.Averages(), Solver.AveragedTime) : Snapshot();

    /// <summary>Frees the solver's memory (on a graphics card, its buffers).</summary>
    public void Dispose() => Solver.Dispose();

    // The boundary layer's mean depth over the grid (m): the mixed layers'.
    private double MixedDepth()
    {
        var grid = Grid;
        double sum = 0;
        int n = 0;
        for (int column = 0; column < grid.Nx * grid.Nz; column++)
        {
            sum += Math.Max(0, Layers[column].Top - grid.GroundHeight[column]);
            n++;
        }
        return n > 0 ? Math.Max(200, sum / n) : 1000;
    }

    // The highest minus the lowest height within reach of a sample.
    private static double Relief(HeightField terrain, int column, int row, int reach)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int b = Math.Max(0, row - reach); b <= Math.Min(terrain.Rows - 1, row + reach); b++)
        {
            for (int a = Math.Max(0, column - reach); a <= Math.Min(terrain.Columns - 1, column + reach); a++)
            {
                float h = terrain.Heights[b * terrain.Columns + a];
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }
        }
        return max - min;
    }

    // The terrain samples in a column's footprint (the nearest one when the terrain is coarser).
    private IEnumerable<int> Footprint(int i, int k)
    {
        var t = Terrain;
        double x0 = Grid.X0 + i * Grid.CellSize, z0 = Grid.Z0 + k * Grid.CellSize;
        int a0 = Math.Max(0, (int)Math.Ceiling((x0 - t.X0) / t.Spacing)), a1 = Math.Min(t.Columns - 1, (int)Math.Floor((x0 + Grid.CellSize - t.X0) / t.Spacing - 1e-9));
        int b0 = Math.Max(0, (int)Math.Ceiling((z0 - t.Z0) / t.Spacing)), b1 = Math.Min(t.Rows - 1, (int)Math.Floor((z0 + Grid.CellSize - t.Z0) / t.Spacing - 1e-9));
        if (a1 < a0 || b1 < b0)
        {
            int a = Math.Clamp((int)Math.Round((x0 + Grid.CellSize / 2 - t.X0) / t.Spacing), 0, t.Columns - 1);
            int b = Math.Clamp((int)Math.Round((z0 + Grid.CellSize / 2 - t.Z0) / t.Spacing), 0, t.Rows - 1);
            yield return b * t.Columns + a;
            yield break;
        }
        for (int b = b0; b <= b1; b++)
        {
            for (int a = a0; a <= a1; a++) yield return b * t.Columns + a;
        }
    }

    private AirflowField Build(AirflowCells cells, double averaged)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        var field = new AirflowField(nx, ny, nz, grid.CellSize, grid.LayerHeight, grid.X0, grid.Y0, grid.Z0);
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k), f = field.Index(i, j, k);
                    if (!Solver.IsAir(c)) continue;
                    field.WindX[f] = cells.X[c];
                    field.WindY[f] = cells.Y[c];
                    field.WindZ[f] = cells.Z[c];
                    field.Turbulence[f] = cells.Turbulence[c];
                    field.LiftChance[f] = cells.LiftChance[c];
                    field.LiftStrength[f] = cells.LiftStrength[c];
                    // A moment's cloud by its water: wisps faint, a grown cumulus full.
                    field.Cloud[f] = averaged > 0 ? cells.Cloud[c] : Math.Min(1, cells.CloudWater[c] / FullCloud);
                    field.Temperature[f] = cells.Temperature[c];
                    field.Vapor[f] = cells.Vapor[c];
                }
            }
        });
        Array.Copy(grid.GroundHeight, field.GroundHeight, grid.GroundHeight.Length);
        Array.Copy(cells.SurfaceHeat, field.SurfaceHeat, cells.SurfaceHeat.Length);
        Array.Copy(ColumnCover, field.LandCover, ColumnCover.Length);
        Diagnose(field, cells.Vapor);
        field.Thermals = FindThermals(field, cells.WaterHeat);
        field.Triggers = FindTriggers();
        field.Info = new AirflowInfo
        {
            Latitude = Settings.Latitude, Longitude = Settings.Longitude, WindDirection = Settings.WindDirection, WindSpeed = Settings.WindSpeed,
            LocalTime = Settings.LocalTime, UtcOffset = Settings.UtcOffset, Sounding = Settings.SoundingName,
            SunAzimuth = Sun.Azimuth, SunElevation = Sun.Elevation, SimulatedTime = Solver.Time, AveragedTime = averaged, Steps = Solver.Steps,
            Created = DateTime.UtcNow,
        };
        return field;
    }

    // The boundary layer top (where the air gets warmer than the air at the ground), the cloud base of rising ground air
    // and the thermals' velocity scale, per column.
    private void Diagnose(AirflowField field, float[] vapor)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny;
        Parallel.For(0, nx * grid.Nz, column =>
        {
            int i = column % nx, k = column / nx;
            int first = grid.FirstLevel[column];
            double ground = grid.GroundHeight[column];
            if (first >= ny - 1)
            {
                field.BoundaryLayerTop[column] = (float)ground;
                field.CloudBase[column] = float.NaN;
                return;
            }
            double Theta(int j) => Solver.BackgroundTemperature(j) + field.Temperature[field.Index(i, j, k)];
            int surface = first + 1 < ny && grid.Air[grid.Cell(i, first, k)] < 0.5f ? first + 1 : first;
            double thetaSurface = Math.Max(Theta(surface), surface + 1 < ny ? Theta(surface + 1) - 0.2 : 0);
            double top = ground;
            for (int j = surface + 1; j < ny; j++)
            {
                if (Theta(j) > thetaSurface + 0.25)
                {
                    top = Math.Max(ground, grid.LevelHeight(j) - grid.LayerHeight / 2);
                    break;
                }
                top = grid.LevelHeight(j);
            }
            field.BoundaryLayerTop[column] = (float)top;
            double pressure = Settings.Sounding.PressureAt(grid.LevelHeight(surface));
            double temperature = thetaSurface * Thermodynamics.Exner(pressure);
            double q = vapor[grid.Cell(i, surface, k)];
            double dewPoint = q > 0 ? Thermodynamics.DewPoint(q, pressure) : temperature - 30;
            field.CloudBase[column] = (float)(grid.LevelHeight(surface) + Thermodynamics.CondensationHeight(temperature, dewPoint));
            field.ThermalVelocity[column] = (float)BoundaryLayer.ConvectiveVelocity(field.SurfaceHeat[column], top - ground, thetaSurface,
                Settings.Sounding.DensityAt(ground));
        });
    }

    // Columns where the mean climb within the boundary layer peaks among their neighbors, with a warm core: air the wind
    // forces up a slope (ridge lift, waves) rises too, but cooler than its surroundings.
    private List<Thermal> FindThermals(AirflowField field, float[] waterHeat)
    {
        int nx = field.Nx, nz = field.Nz, ny = field.Ny;
        var climb = new float[nx * nz];
        var level = new int[nx * nz];
        var levelMean = new double[ny];
        for (int j = 0; j < ny; j++)
        {
            double sum = 0;
            int n = 0;
            for (int k = 0; k < nz; k++)
            {
                for (int i = 0; i < nx; i++)
                {
                    if (Grid.Air[Grid.Cell(i, j, k)] <= 0) continue;
                    sum += field.Temperature[field.Index(i, j, k)];
                    n++;
                }
            }
            levelMean[j] = n > 0 ? sum / n : 0;
        }
        for (int k = 0; k < nz; k++)
        {
            for (int i = 0; i < nx; i++)
            {
                int column = k * nx + i;
                double ground = field.GroundHeight[column], top = field.BoundaryLayerTop[column];
                if (top - ground < 300 || field.ThermalVelocity[column] < 0.5) continue;
                float best = 0;
                for (int j = 0; j < ny; j++)
                {
                    double y = field.Y0 + (j + 0.5) * field.LayerHeight;
                    if (y < ground + 100 || y > top - 50) continue;
                    int c = field.Index(i, j, k);
                    float w = field.WindY[c];
                    if (w > best && field.Temperature[c] - levelMean[j] >= 0.1)
                    {
                        best = w;
                        level[column] = j;
                    }
                }
                climb[column] = best;
            }
        }
        // From each column where the climb peaks, the core is followed down to where the thermal leaves the ground and up
        // to where it stops: thermals lean with the wind, so their strongest level can be far downwind of their source
        // (over a lake, from the town on its shore).
        var thermals = new List<Thermal>();
        var roots = new HashSet<int>();
        foreach (int column in Enumerable.Range(0, nx * nz).Where(c => climb[c] >= 0.5f).OrderByDescending(c => climb[c]))
        {
            int i = column % nx, k = column / nx;
            if (!Maps.IsPeak(climb, nx, nz, i, k, 2)) continue;
            var (core, root) = TraceCore(field, i, level[column], k);
            int rootColumn = root.K * nx + root.I;
            // One thermal per source; none rising from water the sun doesn't warm the air over (a lake breeze's
            // convergence lifts air there too, but it is cool).
            if (!roots.Add(rootColumn)) continue;
            if (ColumnWater[rootColumn] > 0.5f && waterHeat[rootColumn] <= 0) continue;
            double top = Math.Min(core[^1].Y + field.LayerHeight / 2, field.BoundaryLayerTop[rootColumn] + field.LayerHeight);
            double cloudBase = field.CloudBase[rootColumn] < top ? field.CloudBase[rootColumn] : double.NaN;
            thermals.Add(new Thermal(core[0].X, core[0].Z, field.GroundHeight[rootColumn], top, climb[column], cloudBase) { Core = core });
            if (thermals.Count == 300) break;
        }
        return thermals;
    }

    // The core of a thermal through the middle of cell (i, j, k): the path the rising air takes through the mean wind,
    // back down to where it leaves the ground (where the path comes within one and a half layers of it: further down the
    // warm air flows up along the slope, the upslope wind, and the path would follow it to the valley floor) or where it
    // hardly rises yet, and on up until it climbs at less than 1 m/s, in steps of a quarter of a layer (a thermal leans
    // by the wind over its climb).
    private (List<Vector3> Core, (int I, int K) Root) TraceCore(AirflowField field, int i, int j, int k)
    {
        float rise = (float)field.LayerHeight / 4, lift = (float)field.LayerHeight * 1.5f;
        double x1 = field.X0 + field.Nx * field.CellSize, z1 = field.Z0 + field.Nz * field.CellSize;
        var start = new Vector3((float)(field.X0 + (i + 0.5) * field.CellSize), (float)(field.Y0 + (j + 0.5) * field.LayerHeight),
            (float)(field.Z0 + (k + 0.5) * field.CellSize));
        bool Inside(Vector3 p) => p.X >= field.X0 && p.X <= x1 && p.Z >= field.Z0 && p.Z <= z1 && p.Y < field.Top;

        // One step along the mean wind that moves the air up (or down) by a quarter layer; null where it doesn't rise.
        Vector3? Step(Vector3 p, float direction, float least)
        {
            var wind = field.WindAt(p.X, p.Y, p.Z);
            if (wind.Y < least) return null;
            var middle = p + wind * (direction * rise / 2 / wind.Y);
            var mid = field.WindAt(middle.X, middle.Y, middle.Z);
            if (mid.Y < least) return null;
            return p + mid * (direction * rise / mid.Y);
        }

        var below = new List<Vector3>();
        var point = start;
        for (int n = 0; n < 400; n++)
        {
            if (point.Y < field.GroundAt(point.X, point.Z) + lift) break;
            if (Step(point, -1, 0.3f) is not { } next || !Inside(next) || next.Y < field.GroundAt(next.X, next.Z) + rise) break;
            point = next;
            below.Add(point);
        }
        below.Reverse();
        var core = new List<Vector3>(below) { start };
        point = start;
        for (int n = 0; n < 400; n++)
        {
            // Up to where it climbs at a meter per second, about a glider's sink: above, it carries none, and the path leans
            // out flat in the wind.
            if (Step(point, 1, 1f) is not { } next || !Inside(next)) break;
            point = next;
            core.Add(point);
        }
        var root = core[0];
        return (core, (Math.Clamp((int)((root.X - field.X0) / field.CellSize), 0, field.Nx - 1),
            Math.Clamp((int)((root.Z - field.Z0) / field.CellSize), 0, field.Nz - 1)));
    }

    // Hot spots among their surroundings, and knolls and ridges that stand out where the sun shines.
    private List<ThermalTrigger> FindTriggers()
    {
        var grid = Grid;
        int nx = grid.Nx, nz = grid.Nz;
        if (!Settings.Heating) return [];
        var heat = ColumnHeat;
        var around = Maps.Smooth(heat, nx, nz, Math.Max(1, (int)Math.Round(800 / grid.CellSize)));
        var relief = Maps.Smooth(grid.GroundHeight, nx, nz, Math.Max(1, (int)Math.Round(400 / grid.CellSize)));
        var score = new float[nx * nz];
        for (int c = 0; c < score.Length; c++)
        {
            if (heat[c] < 40) continue;
            double hot = (heat[c] - around[c]) / 60;
            double standsOut = Math.Max(0, grid.GroundHeight[c] - relief[c]) / 40;
            score[c] = (float)(hot + standsOut * Math.Min(1, heat[c] / 150));
        }
        int radius = Math.Max(2, (int)Math.Round(500 / grid.CellSize));
        var triggers = new List<ThermalTrigger>();
        for (int k = 0; k < nz; k++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = k * nx + i;
                if (score[c] < 0.5f || !Maps.IsPeak(score, nx, nz, i, k, radius)) continue;
                triggers.Add(new ThermalTrigger(grid.X0 + (i + 0.5) * grid.CellSize, grid.Z0 + (k + 0.5) * grid.CellSize, grid.GroundHeight[c], score[c],
                    heat[c], (LandCover)ColumnCover[c]));
            }
        }
        return triggers.OrderByDescending(t => t.Score).Take(200).ToList();
    }
}
