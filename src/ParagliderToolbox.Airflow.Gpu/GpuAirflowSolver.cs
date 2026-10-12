using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ParagliderToolbox.Airflow.Simulation;

namespace ParagliderToolbox.Airflow.Gpu;

/// <summary>
/// The airflow solver on a graphics card: the same steps as <see cref="AirflowSolver"/> (see it for the physics), every
/// one a kernel over the faces, cells or columns, the whole state and the running averages in the card's memory; only the
/// time step's and the pressure solver's few numbers come back each step, the fields when asked.
/// </summary>
internal sealed class GpuAirflowSolver : IAirflowSolver
{
    private readonly GpuDevice _device;
    private readonly AirflowSetup _setup;
    private readonly Accelerator _accelerator;
    private readonly List<MemoryBuffer> _buffers = [];
    private readonly GridViews _grid;
    private readonly BoundaryParams _boundary;
    private readonly int _faceCountX, _faceCountY, _faceCountZ, _cellCount, _columnCount;
    private readonly float _subgridScale, _tolerance;

    private MemoryBuffer1D<float, Stride1D.Dense> _ux, _uy, _uz, _nextX, _nextY, _nextZ;
    private MemoryBuffer1D<float, Stride1D.Dense> _theta, _vapor, _cloud, _nextTheta, _nextVapor, _nextCloud;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _targetX, _targetY, _targetZ, _initialTheta, _initialVapor;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _centerX, _centerY, _centerZ, _viscosity, _rate, _rhs;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _hat0, _hat1, _hat2, _low0, _low1, _low2, _high0, _high1, _high2;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _sums, _means, _dots;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _maxes;
    private readonly SurfaceViews _surface;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _surfaceHeat, _waterHeat;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _pressure, _r, _z, _p, _q;
    private readonly Level[] _levels;
    private MemoryBuffer1D<float, Stride1D.Dense>[]? _averages;
    private float[]? _hostViscosity;
    private int _noiseEpoch = -1;

    /// <summary>Initializes the solver on <paramref name="device"/>: uploads the grid and the starting state and makes it mass conserving.</summary>
    public GpuAirflowSolver(GpuDevice device, AirflowGrid grid, AirflowConditions conditions)
    {
        _device = device;
        _accelerator = device.Accelerator;
        Grid = grid;
        Conditions = conditions;
        lock (device.Gate)
        {
            _accelerator.Bind();
            try
            {
                var setup = _setup = new AirflowSetup(grid, conditions);
                int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
                _faceCountX = grid.OpenX.Length;
                _faceCountY = grid.OpenY.Length;
                _faceCountZ = grid.OpenZ.Length;
                _cellCount = grid.CellCount;
                _columnCount = nx * nz;
                _subgridScale = AirflowSolver.SubgridScale(grid);
                _tolerance = (float)(2e-4 * Math.Max(conditions.WindSpeed, 1) * grid.CellSize * grid.LayerHeight);

                _grid = new GridViews
                {
                    Nx = nx, Ny = ny, Nz = nz, H = (float)grid.CellSize, Dy = (float)grid.LayerHeight,
                    OpenX = Upload(grid.OpenX).View, OpenY = Upload(grid.OpenY).View, OpenZ = Upload(grid.OpenZ).View,
                    Air = Upload(grid.Air).View, Ground = Upload(grid.Ground).View,
                    FirstLevel = Upload(grid.FirstLevel).View, FirstX = Upload(setup.FirstX).View, FirstZ = Upload(setup.FirstZ).View,
                    IsAir = Upload(setup.IsAir.Select(a => a ? 1 : 0).ToArray()).View,
                    Theta0 = Upload(setup.Theta0).View, Vapor0 = Upload(setup.Vapor0).View, Density0 = Upload(setup.Density0).View,
                    Exner0 = Upload(setup.Exner0).View, Pressure0 = Upload(setup.Pressure0).View, ThetaFace = Upload(setup.ThetaFace).View,
                };
                _boundary = new BoundaryParams
                {
                    West = setup.Inflow.West ? 1 : 0, East = setup.Inflow.East ? 1 : 0, North = setup.Inflow.North ? 1 : 0, South = setup.Inflow.South ? 1 : 0,
                    SpongeStart = (float)(ny * (1 - AirflowSetup.SpongeShare)),
                };

                // The starting state: the inflow's wind on the open faces, the given air.
                var ux = new float[_faceCountX];
                var uz = new float[_faceCountZ];
                for (int f = 0; f < ux.Length; f++) ux[f] = grid.OpenX[f] > 0 ? setup.TargetX[f] : 0;
                for (int f = 0; f < uz.Length; f++) uz[f] = grid.OpenZ[f] > 0 ? setup.TargetZ[f] : 0;
                _ux = Upload(ux);
                var uy = new float[_faceCountY];
                for (int f = 0; f < uy.Length; f++) uy[f] = grid.OpenY[f] > 0 ? setup.TargetY[f] : 0;
                _uy = Upload(uy);
                _uz = Upload(uz);
                _nextX = Allocate(_faceCountX);
                _nextY = Allocate(_faceCountY);
                _nextZ = Allocate(_faceCountZ);
                _theta = Upload(setup.InitialTheta);
                _vapor = Upload(setup.InitialVapor);
                _cloud = Allocate(_cellCount);
                _nextTheta = Allocate(_cellCount);
                _nextVapor = Allocate(_cellCount);
                _nextCloud = Allocate(_cellCount);
                _targetX = Upload(setup.TargetX);
                _targetY = Upload(setup.TargetY);
                _targetZ = Upload(setup.TargetZ);
                _initialTheta = Upload(setup.InitialTheta);
                _initialVapor = Upload(setup.InitialVapor);
                _centerX = Allocate(_cellCount);
                _centerY = Allocate(_cellCount);
                _centerZ = Allocate(_cellCount);
                _viscosity = Allocate(_cellCount);
                _rate = Allocate(_cellCount);
                _rhs = Allocate(_cellCount);
                int largest = Math.Max(Math.Max(_faceCountX, _faceCountY), Math.Max(_faceCountZ, _cellCount));
                _hat0 = Allocate(largest);
                _low0 = Allocate(largest);
                _high0 = Allocate(largest);
                _hat1 = Allocate(_cellCount);
                _low1 = Allocate(_cellCount);
                _high1 = Allocate(_cellCount);
                _hat2 = Allocate(_cellCount);
                _low2 = Allocate(_cellCount);
                _high2 = Allocate(_cellCount);
                _sums = Allocate(ny * 4);
                _means = Allocate(ny * 3);
                _dots = Allocate(4);
                _maxes = _accelerator.Allocate1D<int>(4);
                _buffers.Add(_maxes);
                _surfaceHeat = Upload(setup.Heat);
                _waterHeat = Allocate(_columnCount);
                _surface = new SurfaceViews
                {
                    Heat = Upload(setup.Heat).View, Evaporation = Upload(setup.Evaporation).View, Water = Upload(setup.Water).View,
                    WaterTemperature = Upload(setup.WaterTemperature).View, Drag = Upload(setup.Drag).View,
                    FullHeat = Upload(setup.Heat).View, FullEvaporation = Upload(setup.Evaporation).View,
                    ShadedHeat = Upload(setup.ShadedHeat ?? setup.Heat).View, ShadedEvaporation = Upload(setup.ShadedEvaporation ?? setup.Evaporation).View,
                    SurfaceHeat = _surfaceHeat.View, WaterHeat = _waterHeat.View,
                };

                _pressure = Allocate(_cellCount);
                _r = Allocate(_cellCount);
                _z = Allocate(_cellCount);
                _p = Allocate(_cellCount);
                _q = Allocate(_cellCount);
                _levels = setup.Pressure.Hierarchy.Select(level => new Level(this, level)).ToArray();

                if (conditions.Moisture) _device.Condense(_cellCount, _grid, Scalars(), 0);
                Project();
                _device.Centers(_cellCount, _grid, Faces(), CenterViews());
                _accelerator.Synchronize();
            }
            catch
            {
                // Too large for the card, say: free what was allocated.
                foreach (var buffer in _buffers) buffer.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public AirflowGrid Grid { get; }

    /// <inheritdoc/>
    public AirflowConditions Conditions { get; }

    /// <inheritdoc/>
    public string Device => _device.Name;

    /// <inheritdoc/>
    public double Time { get; private set; }

    /// <inheritdoc/>
    public int Steps { get; private set; }

    /// <inheritdoc/>
    public double LastTimeStep { get; private set; }

    /// <inheritdoc/>
    public int PressureIterations { get; private set; }

    /// <inheritdoc/>
    public int PressureLevels => _levels.Length;

    /// <inheritdoc/>
    public double AveragedTime { get; private set; }

    /// <inheritdoc/>
    public IDictionary<string, TimeSpan>? PhaseTimes { get; set; }

    /// <inheritdoc/>
    public float BackgroundTemperature(int j) => _setup.Theta0[j];

    /// <inheritdoc/>
    public bool IsAir(int cell) => _setup.IsAir[cell];

    /// <inheritdoc/>
    public double Step(double maxTimeStep)
    {
        lock (_device.Gate)
        {
            _accelerator.Bind();
            long start = Stopwatch.GetTimestamp();
            double dt = TimeStep(maxTimeStep);
            float fdt = (float)dt;
            Measure("Time step", ref start);
            _device.ExtrapolateX((Grid.Nx + 1) * Grid.Nz, _grid, _ux.View);
            _device.ExtrapolateZ(Grid.Nx * (Grid.Nz + 1), _grid, _uz.View);
            _device.ExtrapolateCells(_columnCount, _grid, Scalars());
            Measure("Extrapolation", ref start);
            Advect(fdt);
            Measure("Advection", ref start);
            _sums.MemSetToZero();
            _device.LevelSums(_cellCount, _grid, Scalars(), _sums.View);
            _device.LevelMeans(Grid.Ny, _sums.View, _means.View);
            _device.ApplyBuoyancy(_faceCountY, _grid, _uy.View, Scalars(), _means.View, fdt);
            Measure("Buoyancy", ref start);
            double h = Grid.CellSize, dy = Grid.LayerHeight;
            double delta = Math.Cbrt(h * h * dy);
            float cs2 = (float)(AirflowSetup.Smagorinsky * AirflowSetup.Smagorinsky * delta * delta);
            float limit = (float)(0.12 / (dt * (4 / (h * h) + 2 / (dy * dy))));
            _device.Viscosity(_cellCount, _grid, Faces(), CenterViews(), _theta.View, _viscosity.View, limit, cs2);
            Measure("Viscosity", ref start);
            Diffuse(fdt);
            Measure("Diffusion", ref start);
            if (_setup.ShadedHeat != null)
            {
                _device.Shade(_columnCount, _grid, _surface, _cloud.View, _setup.RayStepX, _setup.RayStepZ, _setup.RayLength,
                    1 - MathF.Exp(-fdt / AirflowSetup.GroundResponseTime));
            }
            _device.Surface(_columnCount, _grid, _surface, CenterViews(), Scalars(), _rate.View, fdt);
            _device.DragX(_faceCountX, _grid, _ux.View, _rate.View, fdt);
            _device.DragY(_faceCountY, _grid, _uy.View, _rate.View, fdt);
            _device.DragZ(_faceCountZ, _grid, _uz.View, _rate.View, fdt);
            Measure("Surface", ref start);
            if (Conditions.Moisture) _device.Condense(_cellCount, _grid, Scalars(), fdt);
            Measure("Condensation", ref start);
            _device.RelaxX(_faceCountX, _grid, _ux.View, _targetX.View, _boundary, fdt);
            _device.RelaxY(_faceCountY, _grid, _uy.View, _targetY.View, _boundary, fdt);
            _device.RelaxZ(_faceCountZ, _grid, _uz.View, _targetZ.View, _boundary, fdt);
            _device.RelaxCells(_cellCount, _grid, Scalars(), _initialTheta.View, _initialVapor.View, _boundary, fdt);
            if (_setup.NoiseAmplitude > 0 && (int)Math.Floor(Time / _setup.NoisePeriod) is var epoch && epoch != _noiseEpoch)
            {
                _noiseEpoch = epoch;
                _device.PerturbInflow(_columnCount, _grid, _theta.View, _boundary, epoch, _setup.NoiseAmplitude, _setup.NoiseLevels);
            }
            Measure("Relaxation", ref start);
            Project();
            Measure("Pressure", ref start);
            _device.Centers(_cellCount, _grid, Faces(), CenterViews());
            Measure("Centers", ref start);
            Time += dt;
            Steps++;
            LastTimeStep = dt;
            return dt;
        }
    }

    /// <inheritdoc/>
    public void Accumulate(double dt)
    {
        if (dt <= 0) return;
        lock (_device.Gate)
        {
            _accelerator.Bind();
            _averages ??= Enumerable.Range(0, 12).Select(_ => Allocate(_cellCount)).ToArray();
            var a = _averages;
            var views = new AverageViews
            {
                X = a[0].View, Y = a[1].View, Z = a[2].View, XX = a[3].View, YY = a[4].View, ZZ = a[5].View, Subgrid = a[6].View, LiftTime = a[7].View,
                LiftSum = a[8].View, CloudTime = a[9].View, Theta = a[10].View, Vapor = a[11].View,
            };
            _device.Accumulate(_cellCount, _grid, views, CenterViews(), _viscosity.View, Scalars(), (float)dt, _subgridScale * _subgridScale);
            AveragedTime += dt;
        }
    }

    /// <inheritdoc/>
    public AirflowCells Averages()
    {
        var cells = new AirflowCells(Grid);
        lock (_device.Gate)
        {
            _accelerator.Bind();
            ReadColumns(cells);
            if (_averages is null || AveragedTime <= 0) return cells;
            var sums = _averages.Select(b => b.GetAsArray1D()).ToArray();
            float t = (float)AveragedTime;
            Parallel.For(0, _cellCount, c =>
            {
                float x = sums[0][c] / t, y = sums[1][c] / t, z = sums[2][c] / t;
                float variance = Math.Max(0, (sums[3][c] / t - x * x + sums[4][c] / t - y * y + sums[5][c] / t - z * z) / 3);
                float liftTime = sums[7][c];
                cells.X[c] = x;
                cells.Y[c] = y;
                cells.Z[c] = z;
                cells.Turbulence[c] = MathF.Sqrt(variance + sums[6][c] / t);
                cells.LiftChance[c] = liftTime / t;
                cells.LiftStrength[c] = liftTime > 0 ? sums[8][c] / liftTime : 0;
                cells.Cloud[c] = sums[9][c] / t;
                cells.Temperature[c] = sums[10][c] / t;
                cells.Vapor[c] = sums[11][c] / t;
            });
        }
        return cells;
    }

    /// <inheritdoc/>
    public AirflowCells Snapshot()
    {
        var cells = new AirflowCells(Grid);
        Snapshot(cells);
        return cells;
    }

    /// <inheritdoc/>
    public void Snapshot(AirflowCells cells)
    {
        lock (_device.Gate)
        {
            _accelerator.Bind();
            ReadColumns(cells);
            var nu = _hostViscosity ??= new float[_cellCount];
            _centerX.CopyToCPU(cells.X);
            _centerY.CopyToCPU(cells.Y);
            _centerZ.CopyToCPU(cells.Z);
            _viscosity.CopyToCPU(nu);
            _theta.CopyToCPU(cells.Temperature);
            _vapor.CopyToCPU(cells.Vapor);
            _cloud.CopyToCPU(cells.CloudWater);
            Parallel.For(0, _cellCount, c =>
            {
                if (!_setup.IsAir[c])
                {
                    cells.Turbulence[c] = cells.LiftChance[c] = cells.LiftStrength[c] = cells.Cloud[c] = 0;
                    cells.Temperature[c] = cells.Vapor[c] = cells.CloudWater[c] = 0;
                    return;
                }
                float y = cells.Y[c];
                bool lift = y >= 1;
                cells.Turbulence[c] = nu[c] * _subgridScale;
                cells.LiftChance[c] = lift ? 1 : 0;
                cells.LiftStrength[c] = lift ? y : 0;
                cells.Cloud[c] = cells.CloudWater[c] > AirflowSetup.CloudThreshold ? 1 : 0;
            });
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_device.Gate)
        {
            _accelerator.Bind();
            foreach (var buffer in _buffers) buffer.Dispose();
            if (_averages != null) foreach (var buffer in _averages) buffer.Dispose();
            _buffers.Clear();
        }
    }

    private void ReadColumns(AirflowCells cells)
    {
        _surfaceHeat.CopyToCPU(cells.SurfaceHeat);
        _waterHeat.CopyToCPU(cells.WaterHeat);
    }

    private MemoryBuffer1D<float, Stride1D.Dense> Allocate(int length)
    {
        var buffer = _accelerator.Allocate1D<float>(Math.Max(1, length));
        buffer.MemSetToZero();
        _buffers.Add(buffer);
        return buffer;
    }

    private MemoryBuffer1D<float, Stride1D.Dense> Upload(float[] values)
    {
        var buffer = _accelerator.Allocate1D(values.Length == 0 ? new float[1] : values);
        _buffers.Add(buffer);
        return buffer;
    }

    private MemoryBuffer1D<int, Stride1D.Dense> Upload(int[] values)
    {
        var buffer = _accelerator.Allocate1D(values.Length == 0 ? new int[1] : values);
        _buffers.Add(buffer);
        return buffer;
    }

    private VelocityViews Faces() => new() { X = _ux.View, Y = _uy.View, Z = _uz.View };

    private VelocityViews CenterViews() => new() { X = _centerX.View, Y = _centerY.View, Z = _centerZ.View };

    private ScalarViews Scalars() => new() { Theta = _theta.View, Vapor = _vapor.View, Cloud = _cloud.View };

    private void Measure(string phase, ref long start)
    {
        if (PhaseTimes is not { } times) return;
        _accelerator.Synchronize();
        long now = Stopwatch.GetTimestamp();
        times[phase] = (times.TryGetValue(phase, out var total) ? total : TimeSpan.Zero) + Stopwatch.GetElapsedTime(start, now);
        start = now;
    }

    private double TimeStep(double maxTimeStep)
    {
        _maxes.MemSetToZero();
        _device.MaxAbs(_device.ReductionConfig, _ux.View, _maxes.View, 0);
        _device.MaxAbs(_device.ReductionConfig, _uy.View, _maxes.View, 1);
        _device.MaxAbs(_device.ReductionConfig, _uz.View, _maxes.View, 2);
        var bits = _maxes.GetAsArray1D();
        float mx = BitConverter.Int32BitsToSingle(bits[0]), my = BitConverter.Int32BitsToSingle(bits[1]), mz = BitConverter.Int32BitsToSingle(bits[2]);
        double h = Grid.CellSize, dy = Grid.LayerHeight;
        double limit = Math.Min(h / Math.Max(mx, 0.1f), Math.Min(dy / Math.Max(my, 0.1f), h / Math.Max(mz, 0.1f)));
        return Math.Min(maxTimeStep, AirflowSetup.CourantNumber * limit);
    }

    private void Advect(float dt)
    {
        int nx = Grid.Nx, ny = Grid.Ny, nz = Grid.Nz;
        var velocity = Faces();
        // Each velocity at its own positions, all traced through the old velocity.
        AdvectOne(velocity, new Staggered { Data = _ux.View, Ni = nx + 1, Nj = ny, Nk = nz, Ox = 0, Oy = 0.5f, Oz = 0.5f }, _faceCountX, _grid.OpenX, _nextX, dt);
        AdvectOne(velocity, new Staggered { Data = _uy.View, Ni = nx, Nj = ny + 1, Nk = nz, Ox = 0.5f, Oy = 0, Oz = 0.5f }, _faceCountY, _grid.OpenY, _nextY, dt);
        AdvectOne(velocity, new Staggered { Data = _uz.View, Ni = nx, Nj = ny, Nk = nz + 1, Ox = 0.5f, Oy = 0.5f, Oz = 0 }, _faceCountZ, _grid.OpenZ, _nextZ, dt);
        var hat = new ScalarViews { Theta = _hat0.View, Vapor = _hat1.View, Cloud = _hat2.View };
        var low = new ScalarViews { Theta = _low0.View, Vapor = _low1.View, Cloud = _low2.View };
        var high = new ScalarViews { Theta = _high0.View, Vapor = _high1.View, Cloud = _high2.View };
        var next = new ScalarViews { Theta = _nextTheta.View, Vapor = _nextVapor.View, Cloud = _nextCloud.View };
        _device.AdvectCells(_cellCount, _grid, velocity, Scalars(), hat, low, high, dt);
        _device.CorrectCells(_cellCount, _grid, velocity, Scalars(), hat, low, high, next, dt);
        (_ux, _nextX) = (_nextX, _ux);
        (_uy, _nextY) = (_nextY, _uy);
        (_uz, _nextZ) = (_nextZ, _uz);
        (_theta, _nextTheta) = (_nextTheta, _theta);
        (_vapor, _nextVapor) = (_nextVapor, _vapor);
        (_cloud, _nextCloud) = (_nextCloud, _cloud);
    }

    private void AdvectOne(VelocityViews velocity, Staggered field, int count, ArrayView<float> open, MemoryBuffer1D<float, Stride1D.Dense> result, float dt)
    {
        _device.AdvectFaces(count, _grid, velocity, field, open, _hat0.View, _low0.View, _high0.View, dt);
        var hat = field with { Data = _hat0.View };
        _device.CorrectFaces(count, _grid, velocity, field, hat, open, _low0.View, _high0.View, result.View, dt);
    }

    private void Diffuse(float dt)
    {
        int nx = Grid.Nx, ny = Grid.Ny, nz = Grid.Nz;
        float ih2 = (float)(1 / (Grid.CellSize * Grid.CellSize)), idy2 = (float)(1 / (Grid.LayerHeight * Grid.LayerHeight));
        _device.DiffuseFaces(_faceCountX, _grid, _ux.View, _nextX.View, _grid.OpenX, nx + 1, ny, nz, 0, _viscosity.View, dt, ih2, idy2);
        _device.DiffuseFaces(_faceCountY, _grid, _uy.View, _nextY.View, _grid.OpenY, nx, ny + 1, nz, 1, _viscosity.View, dt, ih2, idy2);
        _device.DiffuseFaces(_faceCountZ, _grid, _uz.View, _nextZ.View, _grid.OpenZ, nx, ny, nz + 1, 2, _viscosity.View, dt, ih2, idy2);
        (_ux, _nextX) = (_nextX, _ux);
        (_uy, _nextY) = (_nextY, _uy);
        (_uz, _nextZ) = (_nextZ, _uz);
        _device.DiffuseCells(_cellCount, _grid, _theta.View, _nextTheta.View, _viscosity.View, 1, dt, ih2, idy2);
        (_theta, _nextTheta) = (_nextTheta, _theta);
        _device.DiffuseCells(_cellCount, _grid, _vapor.View, _nextVapor.View, _viscosity.View, 0, dt, ih2, idy2);
        (_vapor, _nextVapor) = (_nextVapor, _vapor);
    }

    // The boundary faces, then the pressure solve that removes the divergence.
    private void Project()
    {
        int nx = Grid.Nx, ny = Grid.Ny, nz = Grid.Nz;
        _device.BoundaryX(ny * nz, _grid, _ux.View, _targetX.View, _boundary);
        _device.BoundaryZ(nx * ny, _grid, _uz.View, _targetZ.View, _boundary);
        _device.CloseFaces(_faceCountX, _grid.OpenX, _ux.View);
        _device.CloseFaces(_faceCountY, _grid.OpenY, _uy.View);
        _device.CloseFaces(_faceCountZ, _grid.OpenZ, _uz.View);
        _device.Divergence(_cellCount, _grid, Faces(), _levels[0].Views.Diag, _rhs.View);
        SolvePressure(80);
        _device.ProjectX(_faceCountX, _grid, _ux.View, _pressure.View, _boundary);
        _device.ProjectY(_faceCountY, _grid, _uy.View, _pressure.View);
        _device.ProjectZ(_faceCountZ, _grid, _uz.View, _pressure.View, _boundary);
    }

    // Conjugate gradients preconditioned with a multigrid V-cycle, from the last step's pressure (see PressureSolver).
    private void SolvePressure(int maxIterations)
    {
        var fine = _levels[0];
        int n = _cellCount;
        _device.Residual(n, fine.Views, _pressure.View, _rhs.View, _r.View);
        float residual = MaxAbs(_r);
        int iteration = 0;
        if (residual > _tolerance)
        {
            Precondition();
            _z.View.CopyTo(_p.View);
            float rz = Dot(_r, _z);
            for (iteration = 1; iteration <= maxIterations; iteration++)
            {
                _device.Multiply(n, fine.Views, _p.View, _q.View);
                float pq = Dot(_p, _q);
                if (pq <= 0) break;
                _device.UpdateSolution(n, _pressure.View, _r.View, _p.View, _q.View, rz / pq);
                residual = MaxAbs(_r);
                if (residual <= _tolerance) break;
                Precondition();
                float next = Dot(_r, _z);
                float beta = next / rz;
                rz = next;
                _device.UpdateDirection(n, _p.View, _z.View, beta);
            }
        }
        PressureIterations = iteration;
    }

    // z = V-cycle(r).
    private void Precondition()
    {
        var fine = _levels[0];
        _r.View.CopyTo(fine.B.View);
        VCycle(0);
        fine.X.View.CopyTo(_z.View);
    }

    private void VCycle(int index)
    {
        var level = _levels[index];
        if (index == _levels.Length - 1)
        {
            int threads = Math.Min(_accelerator.MaxNumThreadsPerGroup, Math.Max(32, (level.Count + 31) / 32 * 32));
            _device.SolveCoarsest(new KernelConfig(1, threads), level.Views, level.X.View, level.B.View, PressureSolver.CoarsestSweeps);
            return;
        }
        level.X.MemSetToZero();
        for (int sweep = 0; sweep < PressureSolver.Smoothing; sweep++)
        {
            _device.Smooth(level.Count, level.Views, level.X.View, level.B.View, 0);
            _device.Smooth(level.Count, level.Views, level.X.View, level.B.View, 1);
        }
        _device.Residual(level.Count, level.Views, level.X.View, level.B.View, level.R.View);
        var coarse = _levels[index + 1];
        _device.Restrict(coarse.Count, level.Views, coarse.Views, level.R.View, coarse.B.View);
        VCycle(index + 1);
        _device.Prolong(level.Count, level.Views, coarse.Views, coarse.X.View, level.X.View);
        for (int sweep = 0; sweep < PressureSolver.Smoothing; sweep++)
        {
            _device.Smooth(level.Count, level.Views, level.X.View, level.B.View, 1);
            _device.Smooth(level.Count, level.Views, level.X.View, level.B.View, 0);
        }
    }

    private float Dot(MemoryBuffer1D<float, Stride1D.Dense> a, MemoryBuffer1D<float, Stride1D.Dense> b)
    {
        _dots.MemSetToZero();
        _device.Dot(_device.ReductionConfig, a.View, b.View, _dots.View, 0);
        return _dots.GetAsArray1D()[0];
    }

    private float MaxAbs(MemoryBuffer1D<float, Stride1D.Dense> a)
    {
        _maxes.MemSetToZero();
        _device.MaxAbs(_device.ReductionConfig, a.View, _maxes.View, 0);
        return BitConverter.Int32BitsToSingle(_maxes.GetAsArray1D()[0]);
    }

    /// <summary>A level of the pressure hierarchy on the device, with its work vectors.</summary>
    private sealed class Level
    {
        public Level(GpuAirflowSolver solver, PressureSolver.LevelData data)
        {
            Count = data.Nx * data.Ny * data.Nz;
            X = solver.Allocate(Count);
            B = solver.Allocate(Count);
            R = solver.Allocate(Count);
            Views = new LevelViews
            {
                Nx = data.Nx, Ny = data.Ny, Nz = data.Nz,
                Wx = solver.Upload(data.Wx).View, Wy = solver.Upload(data.Wy).View, Wz = solver.Upload(data.Wz).View,
                Diag = solver.Upload(data.Diag).View, InvDiag = solver.Upload(data.InvDiag).View, First = solver.Upload(data.First).View,
            };
        }

        public int Count { get; }
        public LevelViews Views { get; }
        public MemoryBuffer1D<float, Stride1D.Dense> X { get; }
        public MemoryBuffer1D<float, Stride1D.Dense> B { get; }
        public MemoryBuffer1D<float, Stride1D.Dense> R { get; }
    }
}
