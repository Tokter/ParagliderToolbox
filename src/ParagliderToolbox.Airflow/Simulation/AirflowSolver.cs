using System.Diagnostics;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>What drives an <see cref="AirflowSolver"/> beyond its grid.</summary>
public sealed record AirflowConditions
{
    /// <summary>Gets the morning sounding: the stratification and moisture of the air.</summary>
    public required Sounding Sounding { get; init; }

    /// <summary>Gets the direction the wind blows from (degrees clockwise from north).</summary>
    public double WindDirection { get; init; } = 270;

    /// <summary>Gets the wind speed above the ground's friction (m/s).</summary>
    public double WindSpeed { get; init; }

    /// <summary>Gets the height above the ground where the wind reaches <see cref="WindSpeed"/> (m); below it the speed falls off logarithmically.</summary>
    public double WindReferenceHeight { get; init; } = 800;

    /// <summary>Gets the sensible heat flux of every column (W/m², <c>k * Nx + i</c>), or <c>null</c> for none.</summary>
    public float[]? SensibleHeat { get; init; }

    /// <summary>Gets the evaporation of every column (kg/(m² s)), or <c>null</c> for none.</summary>
    public float[]? Evaporation { get; init; }

    /// <summary>Gets the roughness length of every column (m), or <c>null</c> for 0.1 m everywhere.</summary>
    public float[]? Roughness { get; init; }

    /// <summary>
    /// Gets the share of every column that is water, or <c>null</c> for none. Water's heat and vapor aren't given: they
    /// follow every step from how much warmer the water (<see cref="WaterTemperature"/>) is than the air above it.
    /// </summary>
    public float[]? WaterShare { get; init; }

    /// <summary>Gets the water's surface temperature of every column (K), where <see cref="WaterShare"/> has water.</summary>
    public float[]? WaterTemperature { get; init; }

    /// <summary>Gets the starting potential temperature of every cell above the sounding's (K), or <c>null</c> for the sounding's.</summary>
    public float[]? InitialTemperature { get; init; }

    /// <summary>Gets the starting water vapor of every cell (kg/kg), or <c>null</c> for the sounding's.</summary>
    public float[]? InitialVapor { get; init; }

    /// <summary>Gets whether water vapor condenses into clouds (releasing its heat).</summary>
    public bool Moisture { get; init; } = true;

    /// <summary>
    /// Gets the sensible heat of every column in the shade (W/m²: with only the diffuse light), or <c>null</c> for no
    /// cloud shadows. With it, the clouds shade the ground along <see cref="SunDirection"/>: the ground's heat follows
    /// the sunlight that gets through over a few minutes, so a cumulus cuts off the thermal under it.
    /// </summary>
    public float[]? ShadedHeat { get; init; }

    /// <summary>Gets the evaporation of every column in the shade (kg/(m² s)), with <see cref="ShadedHeat"/>.</summary>
    public float[]? ShadedEvaporation { get; init; }

    /// <summary>Gets the unit vector towards the sun (+X east, +Y up, +Z south), for the clouds' shadows.</summary>
    public System.Numerics.Vector3 SunDirection { get; init; } = System.Numerics.Vector3.UnitY;

    /// <summary>
    /// Gets the amplitude of the warmth noise just inside the upwind sides (K), or 0 for none. Air from the sounding
    /// enters without turbulence and takes kilometers of heating before it convects; blocks of random warmth,
    /// renewed as the wind crosses them, set it overturning at once (the cell perturbation method).
    /// </summary>
    public double InflowNoise { get; init; }

    /// <summary>Gets how high above the ground the inflow noise reaches (m).</summary>
    public double InflowNoiseDepth { get; init; } = 1000;
}

/// <summary>
/// The fluid solver: the incompressible, Boussinesq Navier–Stokes equations on an <see cref="AirflowGrid"/> over the
/// terrain, with buoyancy from temperature and moisture, sub-grid turbulence, ground friction and heating, and clouds.
/// The wind enters on the upwind sides, leaves on the others; mass conservation makes it speed up through passes
/// (venturi), flow over and around the relief and separate behind sharp edges (lee turbulence); the heated ground makes
/// thermals, which the sounding's stability stops.
/// </summary>
/// <remarks>
/// <para>
/// A step (<see cref="Step"/>): MacCormack advection (semi-Lagrangian, error-corrected and limited) of the velocity,
/// the potential temperature and the moisture; buoyancy against each level's horizontal mean (a uniform warming only
/// changes the hydrostatic pressure); Smagorinsky viscosity, damped in stable and raised in unstable air; logarithmic
/// friction by the ground's roughness and the surface's heat and vapor in the cells the ground passes through;
/// condensation with its latent heat; relaxation towards the inflow near the upwind sides and a sponge under the lid
/// (which keeps gravity waves from reflecting); then the pressure projection (see <see cref="PressureSolver"/>).
/// </para>
/// <para>Velocities are on the faces (<see cref="VelocityX"/> … in the grid's face layouts), scalars in the cells.</para>
/// </remarks>
public sealed partial class AirflowSolver : IAirflowSolver
{
    private const double Smagorinsky = AirflowSetup.Smagorinsky;
    private const double CourantNumber = AirflowSetup.CourantNumber;
    private const int RelaxationCells = AirflowSetup.RelaxationCells;
    private const double RelaxationTime = AirflowSetup.RelaxationTime;
    private const double OutflowRelaxationTime = AirflowSetup.OutflowRelaxationTime;
    private const double SpongeShare = AirflowSetup.SpongeShare;
    private const double SpongeTime = AirflowSetup.SpongeTime;
    private const float CloudThreshold = AirflowSetup.CloudThreshold;

    private readonly PressureSolver _pressure;
    private readonly (bool West, bool East, bool North, bool South) _inflow;
    private readonly float[] _theta0, _vapor0, _density0, _exner0, _pressure0, _thetaFace;
    private readonly float[] _targetX, _targetY, _targetZ;
    private readonly float[] _initialTheta, _initialVapor;
    private readonly float[] _heat, _evaporation, _drag, _water, _waterTemperature;
    private readonly float[] _groundHeat, _groundEvaporation;
    private readonly AirflowSetup _setup;
    private readonly int[] _firstX, _firstZ;
    private readonly bool[] _isAir;
    private readonly float[] _centerX, _centerY, _centerZ, _viscosity;
    private float[] _ux, _uy, _uz, _theta, _vapor, _cloud;
    private float[] _nextX, _nextY, _nextZ, _nextTheta, _nextVapor, _nextCloud;
    private readonly float[] _rhs;

    /// <summary>Initializes the solver on <paramref name="grid"/> with the wind and the air of <paramref name="conditions"/>, and makes the starting flow mass conserving.</summary>
    public AirflowSolver(AirflowGrid grid, AirflowConditions conditions)
    {
        Grid = grid;
        Conditions = conditions;
        var setup = new AirflowSetup(grid, conditions);
        _theta0 = setup.Theta0;
        _vapor0 = setup.Vapor0;
        _density0 = setup.Density0;
        _exner0 = setup.Exner0;
        _pressure0 = setup.Pressure0;
        _thetaFace = setup.ThetaFace;
        _inflow = setup.Inflow;
        _pressure = setup.Pressure;
        _isAir = setup.IsAir;
        _heat = setup.Heat;
        _evaporation = setup.Evaporation;
        _water = setup.Water;
        _waterTemperature = setup.WaterTemperature;
        _drag = setup.Drag;
        _targetX = setup.TargetX;
        _targetY = setup.TargetY;
        _targetZ = setup.TargetZ;
        _setup = setup;
        _firstX = setup.FirstX;
        _firstZ = setup.FirstZ;
        _initialTheta = setup.InitialTheta;
        _initialVapor = setup.InitialVapor;
        int nx = grid.Nx, nz = grid.Nz;

        _ux = new float[grid.OpenX.Length];
        _uy = new float[grid.OpenY.Length];
        _uz = new float[grid.OpenZ.Length];
        _nextX = new float[_ux.Length];
        _nextY = new float[_uy.Length];
        _nextZ = new float[_uz.Length];
        int cells = grid.CellCount;
        _theta = new float[cells];
        _vapor = new float[cells];
        _cloud = new float[cells];
        _nextTheta = new float[cells];
        _nextVapor = new float[cells];
        _nextCloud = new float[cells];
        _centerX = new float[cells];
        _centerY = new float[cells];
        _centerZ = new float[cells];
        _viscosity = new float[cells];
        _rhs = new float[cells];
        SurfaceHeat = (float[])_heat.Clone();
        _groundHeat = (float[])_heat.Clone();
        _groundEvaporation = (float[])_evaporation.Clone();
        WaterHeat = new float[nx * nz];

        // The starting state: the inflow's wind everywhere, the given (or the sounding's) air.
        for (int f = 0; f < _ux.Length; f++) _ux[f] = grid.OpenX[f] > 0 ? _targetX[f] : 0;
        for (int f = 0; f < _uz.Length; f++) _uz[f] = grid.OpenZ[f] > 0 ? _targetZ[f] : 0;
        for (int f = 0; f < _uy.Length; f++) _uy[f] = grid.OpenY[f] > 0 ? _targetY[f] : 0;
        Array.Copy(_initialTheta, _theta, cells);
        Array.Copy(_initialVapor, _vapor, cells);
        if (conditions.Moisture) Condense();
        Project();
        UpdateCenters();
    }

    /// <summary>Gets the grid.</summary>
    public AirflowGrid Grid { get; }

    /// <summary>Gets the conditions.</summary>
    public AirflowConditions Conditions { get; }

    /// <summary>Gets the simulated time (s).</summary>
    public double Time { get; private set; }

    /// <summary>Gets the steps made.</summary>
    public int Steps { get; private set; }

    /// <summary>Gets the last time step (s).</summary>
    public double LastTimeStep { get; private set; }

    /// <summary>Gets the pressure solver's iterations in the last step.</summary>
    public int PressureIterations => _pressure.Iterations;

    /// <summary>Gets the multigrid levels of the pressure solver.</summary>
    public int PressureLevels => _pressure.LevelCount;

    /// <summary>Gets the velocity along X on the faces normal to X (m/s).</summary>
    public float[] VelocityX => _ux;

    /// <summary>Gets the vertical velocity on the faces normal to Y (m/s).</summary>
    public float[] VelocityY => _uy;

    /// <summary>Gets the velocity along Z on the faces normal to Z (m/s).</summary>
    public float[] VelocityZ => _uz;

    /// <summary>Gets the potential temperature above the sounding's of every cell (K).</summary>
    public float[] Temperature => _theta;

    /// <summary>Gets the water vapor of every cell (kg/kg).</summary>
    public float[] Vapor => _vapor;

    /// <summary>Gets the cloud water of every cell (kg/kg).</summary>
    public float[] Cloud => _cloud;

    /// <summary>Gets the sensible heat flux of every column in the last step (W/m²): the land's as given, the water's by its temperature.</summary>
    public float[] SurfaceHeat { get; }

    /// <summary>Gets the sensible heat flux of every column's water in the last step (W/m² of water; negative where it cools the air).</summary>
    public float[] WaterHeat { get; }

    /// <summary>Gets the sub-grid viscosity of every cell (m²/s), from the last step.</summary>
    public float[] Viscosity => _viscosity;

    /// <summary>Gets the velocity at every cell's center (m/s): X, Y and Z.</summary>
    public (float[] X, float[] Y, float[] Z) CenterVelocity => (_centerX, _centerY, _centerZ);

    /// <summary>Gets whether a cell holds air.</summary>
    public bool IsAir(int cell) => _isAir[cell];

    /// <summary>Gets the potential temperature of the sounding at level <paramref name="j"/> (K).</summary>
    public float BackgroundTemperature(int j) => _theta0[j];

    /// <summary>Gets the water vapor of the sounding at level <paramref name="j"/> (kg/kg).</summary>
    public float BackgroundVapor(int j) => _vapor0[j];

    /// <summary>Gets the threshold of cloud water that counts as cloud (kg/kg).</summary>
    public static float CloudWater => CloudThreshold;

    /// <summary>Gets the largest net flux out of a cell after the last projection, relative to the wind (should be tiny).</summary>
    public double Divergence()
    {
        var grid = Grid;
        double h = grid.CellSize, dy = grid.LayerHeight, worst = 0;
        for (int k = 0; k < grid.Nz; k++)
        {
            for (int i = 0; i < grid.Nx; i++)
            {
                for (int j = 0; j < grid.Ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_pressure.IsActive(c)) continue;
                    worst = Math.Max(worst, Math.Abs(Outflux(i, j, k)) / (h * h * dy));
                }
            }
        }
        return worst;
    }

    /// <summary>Advances the flow by one time step of at most <paramref name="maxTimeStep"/> seconds (less where the flow is fast); returns it.</summary>
    public double Step(double maxTimeStep)
    {
        long start = Stopwatch.GetTimestamp();
        double dt = TimeStep(maxTimeStep);
        Measure("Time step", ref start);
        Extrapolate();
        Measure("Extrapolation", ref start);
        Advect(dt);
        Measure("Advection", ref start);
        ApplyBuoyancy(dt);
        Measure("Buoyancy", ref start);
        ComputeViscosity(dt);
        Measure("Viscosity", ref start);
        Diffuse(dt);
        Measure("Diffusion", ref start);
        ApplySurface(dt);
        Measure("Surface", ref start);
        if (Conditions.Moisture) Condense(dt);
        Measure("Condensation", ref start);
        Relax(dt);
        PerturbInflow();
        Measure("Relaxation", ref start);
        Project();
        Measure("Pressure", ref start);
        UpdateCenters();
        Measure("Centers", ref start);
        Time += dt;
        Steps++;
        LastTimeStep = dt;
        return dt;
    }

    /// <summary>Gets or sets the time the steps spent per phase, measured while it isn't <c>null</c> (for profiling).</summary>
    public IDictionary<string, TimeSpan>? PhaseTimes { get; set; }

    /// <inheritdoc/>
    public string Device => $"CPU, {Environment.ProcessorCount} threads";

    /// <inheritdoc/>
    public double AveragedTime => _averages?.Time ?? 0;

    private AirflowAverages? _averages;

    /// <inheritdoc/>
    public void Accumulate(double dt) => (_averages ??= new AirflowAverages(Grid)).Add(this, dt);

    /// <inheritdoc/>
    public AirflowCells Averages()
    {
        var cells = new AirflowCells(Grid);
        _averages?.Fill(cells);
        Array.Copy(SurfaceHeat, cells.SurfaceHeat, SurfaceHeat.Length);
        Array.Copy(WaterHeat, cells.WaterHeat, WaterHeat.Length);
        return cells;
    }

    /// <inheritdoc/>
    /// <remarks>The turbulence is the sub-grid part, the lift and the cloud are 0 or 1.</remarks>
    public AirflowCells Snapshot()
    {
        var cells = new AirflowCells(Grid);
        Snapshot(cells);
        return cells;
    }

    /// <inheritdoc/>
    public void Snapshot(AirflowCells cells)
    {
        float scale = SubgridScale(Grid);
        Parallel.For(0, Grid.Nx * Grid.Nz, column =>
        {
            for (int c = column * Grid.Ny, end = c + Grid.Ny; c < end; c++)
            {
                bool air = _isAir[c];
                float y = air ? _centerY[c] : 0;
                bool lift = y >= AirflowAverages.LiftThreshold;
                cells.X[c] = air ? _centerX[c] : 0;
                cells.Y[c] = y;
                cells.Z[c] = air ? _centerZ[c] : 0;
                cells.Turbulence[c] = air ? _viscosity[c] * scale : 0;
                cells.LiftChance[c] = lift ? 1 : 0;
                cells.LiftStrength[c] = lift ? y : 0;
                cells.CloudWater[c] = air ? _cloud[c] : 0;
                cells.Cloud[c] = air && _cloud[c] > CloudThreshold ? 1 : 0;
                cells.Temperature[c] = air ? _theta[c] : 0;
                cells.Vapor[c] = air ? _vapor[c] : 0;
            }
        });
        Array.Copy(SurfaceHeat, cells.SurfaceHeat, SurfaceHeat.Length);
        Array.Copy(WaterHeat, cells.WaterHeat, WaterHeat.Length);
    }

    /// <summary>
    /// Gets the factor from a cell's sub-grid viscosity to the standard deviation of its unresolved gusts: the sub-grid
    /// kinetic energy is (ν / (0.1·Δ))², two thirds of it per axis.
    /// </summary>
    internal static float SubgridScale(AirflowGrid grid) =>
        (float)(Math.Sqrt(2.0 / 3) / (0.1 * Math.Cbrt(grid.CellSize * grid.CellSize * grid.LayerHeight)));

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private void Measure(string phase, ref long start)
    {
        if (PhaseTimes is not { } times) return;
        long now = Stopwatch.GetTimestamp();
        times[phase] = (times.TryGetValue(phase, out var total) ? total : TimeSpan.Zero) + Stopwatch.GetElapsedTime(start, now);
        start = now;
    }

    private double TimeStep(double maxTimeStep)
    {
        float mx = MaxAbs(_ux), my = MaxAbs(_uy), mz = MaxAbs(_uz);
        double h = Grid.CellSize, dy = Grid.LayerHeight;
        double limit = Math.Min(h / Math.Max(mx, 0.1f), Math.Min(dy / Math.Max(my, 0.1f), h / Math.Max(mz, 0.1f)));
        return Math.Min(maxTimeStep, CourantNumber * limit);
    }

    private static float MaxAbs(float[] values)
    {
        float max = 0;
        foreach (float v in values) max = Math.Max(max, Math.Abs(v));
        return max;
    }

    #region Forces

    // Buoyancy on the vertical faces, against each level's horizontal mean.
    private void ApplyBuoyancy(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        var sums = new double[nz][];
        Parallel.For(0, nz, k =>
        {
            var local = sums[k] = new double[ny * 4];
            for (int i = 0; i < nx; i++)
            {
                for (int j = grid.FirstLevel[k * nx + i]; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c]) continue;
                    local[j * 4] += _theta[c];
                    local[j * 4 + 1] += _vapor[c] - _vapor0[j];
                    local[j * 4 + 2] += _cloud[c];
                    local[j * 4 + 3] += 1;
                }
            }
        });
        var mean = new float[ny * 3];
        for (int j = 0; j < ny; j++)
        {
            double t = 0, v = 0, q = 0, n = 0;
            foreach (var local in sums)
            {
                t += local[j * 4];
                v += local[j * 4 + 1];
                q += local[j * 4 + 2];
                n += local[j * 4 + 3];
            }
            if (n <= 0) continue;
            mean[j * 3] = (float)(t / n);
            mean[j * 3 + 1] = (float)(v / n);
            mean[j * 3 + 2] = (float)(q / n);
        }

        float g = (float)Thermodynamics.Gravity, fdt = (float)dt;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = Math.Max(1, grid.FirstLevel[k * nx + i]); j < ny; j++)
                {
                    int f = grid.FaceY(i, j, k);
                    if (grid.OpenY[f] <= 0) continue;
                    int below = grid.Cell(i, j - 1, k), above = below + 1;
                    float Buoyancy(int c, int level) =>
                        (_theta[c] - mean[level * 3]) / _thetaFace[j] + 0.608f * (_vapor[c] - _vapor0[level] - mean[level * 3 + 1]) - (_cloud[c] - mean[level * 3 + 2]);
                    float b = _isAir[below] ? (Buoyancy(below, j - 1) + Buoyancy(above, j)) / 2 : Buoyancy(above, j);
                    _uy[f] += fdt * g * b;
                }
            }
        });
    }

    // Smagorinsky viscosity per cell, scaled by the local stability (gradient Richardson number).
    private void ComputeViscosity(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        double h = grid.CellSize, dy = grid.LayerHeight;
        double delta = Math.Cbrt(h * h * dy);
        double cs2 = Smagorinsky * Smagorinsky * delta * delta;
        double limit = 0.12 / (dt * (4 / (h * h) + 2 / (dy * dy)));
        float g = (float)Thermodynamics.Gravity;
        int slab = nx * ny;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                int first = grid.FirstLevel[k * nx + i];
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (j < first || !_isAir[c])
                    {
                        _viscosity[c] = 0;
                        continue;
                    }
                    double sxx = (_ux[grid.FaceX(i + 1, j, k)] - _ux[grid.FaceX(i, j, k)]) / h;
                    double syy = (_uy[grid.FaceY(i, j + 1, k)] - _uy[grid.FaceY(i, j, k)]) / dy;
                    double szz = (_uz[grid.FaceZ(i, j, k + 1)] - _uz[grid.FaceZ(i, j, k)]) / h;
                    int cxm = i > 0 && _isAir[c - ny] ? c - ny : c, cxp = i < nx - 1 && _isAir[c + ny] ? c + ny : c;
                    int cym = j > first && _isAir[c - 1] ? c - 1 : c, cyp = j < ny - 1 && _isAir[c + 1] ? c + 1 : c;
                    int czm = k > 0 && _isAir[c - slab] ? c - slab : c, czp = k < nz - 1 && _isAir[c + slab] ? c + slab : c;
                    double dX = Math.Max(1, (cxp - cxm) / ny) * h, dY = Math.Max(1, cyp - cym) * dy, dZ = Math.Max(1, (czp - czm) / slab) * h;
                    double duxdy = (_centerX[cyp] - _centerX[cym]) / dY, duydx = (_centerY[cxp] - _centerY[cxm]) / dX;
                    double duxdz = (_centerX[czp] - _centerX[czm]) / dZ, duzdx = (_centerZ[cxp] - _centerZ[cxm]) / dX;
                    double duydz = (_centerY[czp] - _centerY[czm]) / dZ, duzdy = (_centerZ[cyp] - _centerZ[cym]) / dY;
                    double sxy = 0.5 * (duxdy + duydx), sxz = 0.5 * (duxdz + duzdx), syz = 0.5 * (duydz + duzdy);
                    double s2 = 2 * (sxx * sxx + syy * syy + szz * szz) + 4 * (sxy * sxy + sxz * sxz + syz * syz);
                    // Stability: dθ/dy of the sounding plus the perturbation's.
                    double dtheta = (_theta0[Math.Min(j + 1, ny - 1)] - _theta0[Math.Max(j - 1, 0)]) / (Math.Min(j + 1, ny - 1) - Math.Max(j - 1, 0)) / dy
                                    + (_theta[cyp] - _theta[cym]) / dY;
                    double n2 = g / _theta0[j] * dtheta;
                    double ri = n2 / Math.Max(s2, 1e-10);
                    double factor = ri >= 0.25 ? 0 : ri >= 0 ? Math.Sqrt(1 - 4 * ri) : Math.Min(3, Math.Sqrt(1 - 16 * ri));
                    _viscosity[c] = (float)Math.Min(limit, 0.2 + cs2 * Math.Sqrt(s2) * factor);
                }
            }
        });
    }

    private void Diffuse(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        float fdt = (float)dt, ih2 = (float)(1 / (grid.CellSize * grid.CellSize)), idy2 = (float)(1 / (grid.LayerHeight * grid.LayerHeight));

        // The velocities: each face with its open neighbors of the same kind (closed ones don't exchange: free slip).
        DiffuseFaces(_ux, _nextX, grid.OpenX, nx + 1, ny, nz, (i, j, k) => Average(i - 1, i, j, k, 0), fdt, ih2, idy2);
        DiffuseFaces(_uy, _nextY, grid.OpenY, nx, ny + 1, nz, (i, j, k) => Average(j - 1, j, i, k, 1), fdt, ih2, idy2);
        DiffuseFaces(_uz, _nextZ, grid.OpenZ, nx, ny, nz + 1, (i, j, k) => Average(k - 1, k, i, j, 2), fdt, ih2, idy2);
        (_ux, _nextX) = (_nextX, _ux);
        (_uy, _nextY) = (_nextY, _uy);
        (_uz, _nextZ) = (_nextZ, _uz);

        // The scalars through the open faces, with a turbulent Prandtl number of 1/3.
        DiffuseCells(_theta, _nextTheta, fdt, ih2, idy2, _theta0);
        (_theta, _nextTheta) = (_nextTheta, _theta);
        DiffuseCells(_vapor, _nextVapor, fdt, ih2, idy2, null);
        (_vapor, _nextVapor) = (_nextVapor, _vapor);
    }

    // The viscosity at a face between cells a and b along an axis (0: x, 1: y, 2: z), the others' indices given.
    private float Average(int a, int b, int p, int q, int axis)
    {
        var grid = Grid;
        int Cell(int n) => axis switch
        {
            0 => grid.Cell(Math.Clamp(n, 0, grid.Nx - 1), p, q),
            1 => grid.Cell(p, Math.Clamp(n, 0, grid.Ny - 1), q),
            _ => grid.Cell(p, q, Math.Clamp(n, 0, grid.Nz - 1)),
        };
        return (_viscosity[Cell(a)] + _viscosity[Cell(b)]) / 2;
    }

    private static void DiffuseFaces(float[] field, float[] result, float[] open, int ni, int nj, int nk, Func<int, int, int, float> viscosity,
        float dt, float ih2, float idy2)
    {
        int slab = ni * nj;
        Parallel.For(0, nk, k =>
        {
            for (int i = 0; i < ni; i++)
            {
                for (int j = 0; j < nj; j++)
                {
                    int f = (k * ni + i) * nj + j;
                    float value = field[f];
                    if (open[f] <= 0)
                    {
                        result[f] = value;
                        continue;
                    }
                    float nu = viscosity(i, j, k);
                    float sum = 0;
                    if (i > 0 && open[f - nj] > 0) sum += (field[f - nj] - value) * ih2;
                    if (i < ni - 1 && open[f + nj] > 0) sum += (field[f + nj] - value) * ih2;
                    if (j > 0 && open[f - 1] > 0) sum += (field[f - 1] - value) * idy2;
                    if (j < nj - 1 && open[f + 1] > 0) sum += (field[f + 1] - value) * idy2;
                    if (k > 0 && open[f - slab] > 0) sum += (field[f - slab] - value) * ih2;
                    if (k < nk - 1 && open[f + slab] > 0) sum += (field[f + slab] - value) * ih2;
                    result[f] = value + dt * nu * sum;
                }
            }
        });
    }

    // The scalar through the open faces; the potential temperature mixes as a whole (perturbation plus the sounding's).
    private void DiffuseCells(float[] field, float[] result, float dt, float ih2, float idy2, float[]? background)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz, slab = nx * ny;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    float value = field[c];
                    if (!_isAir[c])
                    {
                        result[c] = value;
                        continue;
                    }
                    float nu = _viscosity[c], sum = 0;
                    float Flux(int other, float open, float inverse, float offset = 0) => open * 3 * (nu + _viscosity[other]) / 2 * (field[other] - value + offset) * inverse;
                    if (i > 0) sum += Flux(c - ny, grid.OpenX[grid.FaceX(i, j, k)], ih2);
                    if (i < nx - 1) sum += Flux(c + ny, grid.OpenX[grid.FaceX(i + 1, j, k)], ih2);
                    float below = background is null || j == 0 ? 0 : background[j - 1] - background[j];
                    float above = background is null || j == ny - 1 ? 0 : background[j + 1] - background[j];
                    if (j > 0) sum += Flux(c - 1, grid.OpenY[grid.FaceY(i, j, k)], idy2, below);
                    if (j < ny - 1) sum += Flux(c + 1, grid.OpenY[grid.FaceY(i, j + 1, k)], idy2, above);
                    if (k > 0) sum += Flux(c - slab, grid.OpenZ[grid.FaceZ(i, j, k)], ih2);
                    if (k < nz - 1) sum += Flux(c + slab, grid.OpenZ[grid.FaceZ(i, j, k + 1)], ih2);
                    result[c] = value + dt * sum / Math.Max(grid.Air[c], 0.25f);
                }
            }
        });
    }

    // Friction, heat and vapor where the ground passes through the cells.
    private void ApplySurface(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        float fdt = (float)dt, dy = (float)grid.LayerHeight, cp = (float)Thermodynamics.SpecificHeat;
        if (_setup.ShadedHeat != null) Shade(fdt);
        // The friction rate per cell: drag coefficient × surface share / air depth × speed (1/s).
        var rate = _nextCloud;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                int column = k * nx + i;
                var (heat, evaporation) = ColumnFluxes(i, k);
                SurfaceHeat[column] = heat;
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    float ground = grid.Ground[c];
                    if (ground <= 0 || !_isAir[c])
                    {
                        rate[c] = 0;
                        continue;
                    }
                    float depth = Math.Max(grid.Air[c], 0.25f) * dy;
                    float speed = MathF.Sqrt(_centerX[c] * _centerX[c] + _centerY[c] * _centerY[c] + _centerZ[c] * _centerZ[c]);
                    rate[c] = _drag[column] * ground / depth * speed;
                    // The heat and vapor warm the air the ground's surface is in; a cell with little air shares them
                    // with the one above, so a sliver doesn't overheat.
                    bool share = grid.Air[c] < 0.5f && j + 1 < ny && _isAir[c + 1];
                    float layer = share ? (grid.Air[c] + grid.Air[c + 1]) * dy : depth;
                    float warming = fdt * heat * ground / (_density0[j] * cp * _exner0[j] * layer);
                    float moistening = fdt * evaporation * ground / (_density0[j] * layer);
                    _theta[c] += warming;
                    _vapor[c] = Math.Max(0, _vapor[c] + moistening);
                    if (share)
                    {
                        _theta[c + 1] += warming;
                        _vapor[c + 1] = Math.Max(0, _vapor[c + 1] + moistening);
                    }
                }
            }
        });
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i <= nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int f = grid.FaceX(i, j, k);
                    float r = (rate[grid.Cell(Math.Max(i - 1, 0), j, k)] + rate[grid.Cell(Math.Min(i, nx - 1), j, k)]) / 2;
                    if (r > 0) _ux[f] /= 1 + fdt * r;
                }
            }
            for (int i = 0; i < nx; i++)
            {
                for (int j = 1; j < ny; j++)
                {
                    int f = grid.FaceY(i, j, k);
                    float r = (rate[grid.Cell(i, j - 1, k)] + rate[grid.Cell(i, j, k)]) / 2;
                    if (r > 0) _uy[f] /= 1 + fdt * r;
                }
            }
        });
        Parallel.For(0, nz + 1, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int f = grid.FaceZ(i, j, k);
                    float r = (rate[grid.Cell(i, j, Math.Max(k - 1, 0))] + rate[grid.Cell(i, j, Math.Min(k, nz - 1))]) / 2;
                    if (r > 0) _uz[f] /= 1 + fdt * r;
                }
            }
        });
    }

    // The clouds' shadows: the sunlight that gets through the cloud water along the sun's ray from every column's ground,
    // and the ground's heat following it over a few minutes.
    private void Shade(float dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny;
        var shadedHeat = _setup.ShadedHeat!;
        var shadedEvaporation = _setup.ShadedEvaporation!;
        float follow = 1 - MathF.Exp(-dt / AirflowSetup.GroundResponseTime);
        Parallel.For(0, nx * grid.Nz, column =>
        {
            float through = MathF.Exp(-AirflowSetup.CloudExtinction * CloudPath(column));
            float heat = shadedHeat[column] + through * (_heat[column] - shadedHeat[column]);
            float evaporation = shadedEvaporation[column] + through * (_evaporation[column] - shadedEvaporation[column]);
            _groundHeat[column] += (heat - _groundHeat[column]) * follow;
            _groundEvaporation[column] += (evaporation - _groundEvaporation[column]) * follow;
        });

        // The cloud water along the ray (kg/m²), a level per step, the nearest cell's (none beyond the sides).
        float CloudPath(int column)
        {
            float fi = column % nx + 0.5f, fk = column / nx + 0.5f, path = 0;
            for (int j = grid.FirstLevel[column]; j < ny; j++)
            {
                int i = (int)MathF.Floor(fi), k = (int)MathF.Floor(fk);
                if (i < 0 || i >= nx || k < 0 || k >= grid.Nz) break;
                path += _cloud[grid.Cell(i, j, k)] * _density0[j];
                fi += _setup.RayStepX;
                fk += _setup.RayStepZ;
            }
            return path * _setup.RayLength;
        }
    }

    // A column's sensible heat (W/m²) and evaporation (kg/(m² s)): its land's as given, plus its water's by how much
    // warmer the water is than the air just above it (a lake under a summer afternoon's warmer air cools it).
    private (float Heat, float Evaporation) ColumnFluxes(int i, int k)
    {
        var grid = Grid;
        int column = k * grid.Nx + i;
        float heat = _groundHeat[column], evaporation = _groundEvaporation[column], water = _water[column];
        int j = grid.FirstLevel[column];
        if (water <= 0 || j >= grid.Ny) return (heat, evaporation);
        int c = grid.Cell(i, j, k);
        if (grid.Air[c] < 0.5f && j + 1 < grid.Ny && _isAir[c + 1])
        {
            c++;
            j++;
        }
        double air = (_theta0[j] + _theta[c]) * _exner0[j];
        double speed = Math.Sqrt(_centerX[c] * _centerX[c] + _centerY[c] * _centerY[c] + _centerZ[c] * _centerZ[c]);
        var (sensible, vapor) = SurfaceHeating.WaterFlux(_waterTemperature[column], air, _vapor[c], speed, _pressure0[j], _density0[j]);
        WaterHeat[column] = (float)sensible;
        return (heat + water * (float)sensible, evaporation + water * (float)vapor);
    }

    private void Condense(double dt = 0)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        double lv = Thermodynamics.LatentHeat, cp = Thermodynamics.SpecificHeat, rv = Thermodynamics.VaporConstant;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = grid.FirstLevel[k * nx + i]; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c]) continue;
                    double temperature = (_theta0[j] + _theta[c]) * _exner0[j];
                    double saturation = Thermodynamics.SaturationMixingRatio(temperature, _pressure0[j]);
                    double factor = 1 + lv * lv * saturation / (cp * rv * temperature * temperature);
                    double change = (_vapor[c] - saturation) / factor;
                    // Condense the excess, or evaporate cloud water into subsaturated air.
                    if (change < 0) change = Math.Max(change, -_cloud[c]);
                    if (change == 0) continue;
                    _vapor[c] -= (float)change;
                    _cloud[c] += (float)change;
                    _theta[c] += (float)(lv * change / (cp * _exner0[j]));
                    // Heavy cloud water rains out (slowly), so the clouds don't load the air without end.
                    if (dt > 0 && _cloud[c] > 1.5e-3f) _cloud[c] -= (float)((_cloud[c] - 1.5e-3f) * Math.Min(1, dt / 600));
                }
            }
        });
    }


    // The inflow noise: random warmth in blocks just inside the upwind sides' relaxation band, renewed every period.
    private int _noiseEpoch = -1;

    private void PerturbInflow()
    {
        if (_setup.NoiseAmplitude <= 0) return;
        int epoch = (int)Math.Floor(Time / _setup.NoisePeriod);
        if (epoch == _noiseEpoch) return;
        _noiseEpoch = epoch;
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny;
        Parallel.For(0, nx * grid.Nz, column =>
        {
            int i = column % nx, k = column / nx;
            int d = _setup.UpwindDistance(i, k);
            if (d < RelaxationCells || d >= RelaxationCells + AirflowSetup.NoiseBlock * AirflowSetup.NoiseBlocks) return;
            int first = grid.FirstLevel[column];
            for (int j = first; j < Math.Min(ny, first + _setup.NoiseLevels); j++)
            {
                int c = column * ny + j;
                if (_isAir[c]) _theta[c] += _setup.NoiseAmplitude * AirflowSetup.Noise(i, j, k, epoch);
            }
        });
    }
    // Relaxation towards the inflow near the upwind sides, and the sponge under the lid.
    private void Relax(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        double spongeStart = ny * (1 - SpongeShare);
        // Upwind sides relax firmly; the others gently, which keeps what leaves there from disturbing the flow inside.
        float Rate(double fi, double fj, double fk)
        {
            double band = Math.Max(Side(fi, _inflow.West), Side(nx - fi, _inflow.East));
            band = Math.Max(band, Math.Max(Side(fk, _inflow.North), Side(nz - fk, _inflow.South)));
            double sponge = fj > spongeStart ? Math.Pow(Math.Sin(Math.PI / 2 * (fj - spongeStart) / (ny - spongeStart)), 2) : 0;
            double rate = Math.Max(band, sponge / SpongeTime);
            return (float)(1 - Math.Exp(-rate * dt));
        }
        static double Side(double d, bool inflow) => Band(d) / (inflow ? RelaxationTime : OutflowRelaxationTime);
        static double Band(double d) => d >= RelaxationCells ? 0 : Math.Pow((RelaxationCells - d) / RelaxationCells, 2);

        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i <= nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int f = grid.FaceX(i, j, k);
                    if (grid.OpenX[f] <= 0) continue;
                    float a = Rate(i, j + 0.5, k + 0.5);
                    if (a > 0) _ux[f] += (_targetX[f] - _ux[f]) * a;
                }
            }
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j <= ny; j++)
                {
                    int f = grid.FaceY(i, j, k);
                    if (grid.OpenY[f] <= 0) continue;
                    float a = Rate(i + 0.5, j, k + 0.5);
                    if (a > 0) _uy[f] += (_targetY[f] - _uy[f]) * a;
                }
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c]) continue;
                    float a = Rate(i + 0.5, j + 0.5, k + 0.5);
                    if (a <= 0) continue;
                    _theta[c] += (_initialTheta[c] - _theta[c]) * a;
                    _vapor[c] += (_initialVapor[c] - _vapor[c]) * a;
                    _cloud[c] -= _cloud[c] * a;
                }
            }
        });
        Parallel.For(0, nz + 1, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int f = grid.FaceZ(i, j, k);
                    if (grid.OpenZ[f] <= 0) continue;
                    float a = Rate(i + 0.5, j + 0.5, k);
                    if (a > 0) _uz[f] += (_targetZ[f] - _uz[f]) * a;
                }
            }
        });
    }

    #endregion

    #region Projection

    // The boundary faces, then the pressure solve that removes the divergence.
    private void Project()
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        ApplyBoundaries();

        double h = grid.CellSize, dy = grid.LayerHeight;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    _rhs[c] = _pressure.IsActive(c) ? (float)(-Outflux(i, j, k)) : 0;
                }
            }
        });
        double tolerance = 2e-4 * Math.Max(Conditions.WindSpeed, 1) * h * dy;
        _pressure.Solve(_rhs, tolerance, 80);

        var p = _pressure.Pressure;
        float ih = (float)(1 / h), idy = (float)(1 / dy);
        Parallel.For(0, nz, k =>
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    int f = grid.FaceX(i, j, k);
                    if (grid.OpenX[f] <= 0) continue;
                    bool boundary = i == 0 || i == nx;
                    if (boundary && (i == 0 ? _inflow.West : _inflow.East)) continue;
                    float west = i > 0 ? p[grid.Cell(i - 1, j, k)] : 0, east = i < nx ? p[grid.Cell(i, j, k)] : 0;
                    _ux[f] -= (east - west) * ih;
                }
            }
            for (int i = 0; i < nx; i++)
            {
                for (int j = 1; j < ny; j++)
                {
                    int f = grid.FaceY(i, j, k);
                    if (grid.OpenY[f] <= 0) continue;
                    _uy[f] -= (p[grid.Cell(i, j, k)] - p[grid.Cell(i, j - 1, k)]) * idy;
                }
            }
        });
        Parallel.For(0, nz + 1, k =>
        {
            bool boundary = k == 0 || k == nz;
            if (boundary && (k == 0 ? _inflow.North : _inflow.South)) return;
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int f = grid.FaceZ(i, j, k);
                    if (grid.OpenZ[f] <= 0) continue;
                    float north = k > 0 ? p[grid.Cell(i, j, k - 1)] : 0, south = k < nz ? p[grid.Cell(i, j, k)] : 0;
                    _uz[f] -= (south - north) * ih;
                }
            }
        });
    }

    // The net volume flux out of a cell (m³/s).
    private double Outflux(int i, int j, int k)
    {
        var grid = Grid;
        double h = grid.CellSize, dy = grid.LayerHeight;
        int x0 = grid.FaceX(i, j, k), x1 = grid.FaceX(i + 1, j, k);
        int y0 = grid.FaceY(i, j, k), y1 = y0 + 1;
        int z0 = grid.FaceZ(i, j, k), z1 = grid.FaceZ(i, j, k + 1);
        return h * dy * (grid.OpenX[x1] * _ux[x1] - grid.OpenX[x0] * _ux[x0] + grid.OpenZ[z1] * _uz[z1] - grid.OpenZ[z0] * _uz[z0])
               + h * h * (grid.OpenY[y1] * _uy[y1] - grid.OpenY[y0] * _uy[y0]);
    }

    // Inflow sides take the inflow, open sides let the flow through as it comes (zero gradient), closed faces are still.
    private void ApplyBoundaries()
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                int west = grid.FaceX(0, j, k), east = grid.FaceX(nx, j, k);
                _ux[west] = grid.OpenX[west] <= 0 ? 0 : _inflow.West ? _targetX[west] : Open(_ux[grid.FaceX(1, j, k)], -1, _targetX[west]);
                _ux[east] = grid.OpenX[east] <= 0 ? 0 : _inflow.East ? _targetX[east] : Open(_ux[grid.FaceX(nx - 1, j, k)], 1, _targetX[east]);
            }
        }
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                int north = grid.FaceZ(i, j, 0), south = grid.FaceZ(i, j, nz);
                _uz[north] = grid.OpenZ[north] <= 0 ? 0 : _inflow.North ? _targetZ[north] : Open(_uz[grid.FaceZ(i, j, 1)], -1, _targetZ[north]);
                _uz[south] = grid.OpenZ[south] <= 0 ? 0 : _inflow.South ? _targetZ[south] : Open(_uz[grid.FaceZ(i, j, nz - 1)], 1, _targetZ[south]);
            }
        }
        Parallel.For(0, _ux.Length, f =>
        {
            if (grid.OpenX[f] <= 0) _ux[f] = 0;
        });
        Parallel.For(0, _uy.Length, f =>
        {
            if (grid.OpenY[f] <= 0) _uy[f] = 0;
        });
        Parallel.For(0, _uz.Length, f =>
        {
            if (grid.OpenZ[f] <= 0) _uz[f] = 0;
        });
    }

    // An open side lets the flow out as it comes (zero gradient); air coming in takes the inflow's (copying an inward
    // flow from inside would feed itself).
    private static float Open(float inside, int outward, float target) => inside * outward >= 0 ? inside : target;

    // The velocity at the cells' centers: the open-weighted mean of their faces.
    private void UpdateCenters()
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c])
                    {
                        _centerX[c] = _centerY[c] = _centerZ[c] = 0;
                        continue;
                    }
                    _centerX[c] = Mean(grid.OpenX, _ux, grid.FaceX(i, j, k), grid.FaceX(i + 1, j, k));
                    _centerY[c] = Mean(grid.OpenY, _uy, grid.FaceY(i, j, k), grid.FaceY(i, j + 1, k));
                    _centerZ[c] = Mean(grid.OpenZ, _uz, grid.FaceZ(i, j, k), grid.FaceZ(i, j, k + 1));
                }
            }
        });
    }

    private static float Mean(float[] open, float[] velocity, int a, int b)
    {
        float wa = open[a], wb = open[b];
        return wa + wb > 0 ? (wa * velocity[a] + wb * velocity[b]) / (wa + wb) : 0;
    }

    #endregion
}
