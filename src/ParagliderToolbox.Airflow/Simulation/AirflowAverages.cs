namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>
/// The time averages of a running <see cref="AirflowSolver"/>: the mean wind and its variance (the resolved gusts), the
/// sub-grid turbulence, how often and how strongly the air rises, the cloud time, the warming and the vapor.
/// </summary>
internal sealed class AirflowAverages
{
    /// <summary>The climb that counts as lift (m/s).</summary>
    public const float LiftThreshold = 1;

    private readonly float[] _x, _y, _z, _xx, _yy, _zz, _subgrid, _liftTime, _liftSum, _cloudTime, _theta, _vapor;
    private readonly float _subgridScale;

    public AirflowAverages(AirflowGrid grid)
    {
        int n = grid.CellCount;
        _x = new float[n];
        _y = new float[n];
        _z = new float[n];
        _xx = new float[n];
        _yy = new float[n];
        _zz = new float[n];
        _subgrid = new float[n];
        _liftTime = new float[n];
        _liftSum = new float[n];
        _cloudTime = new float[n];
        _theta = new float[n];
        _vapor = new float[n];
        // The sub-grid kinetic energy is (ν / (C·Δ))²; its velocity variance per axis two thirds of it.
        double delta = Math.Cbrt(grid.CellSize * grid.CellSize * grid.LayerHeight);
        _subgridScale = (float)(2.0 / 3 / Math.Pow(0.1 * delta, 2));
        Grid = grid;
    }

    public AirflowGrid Grid { get; }

    /// <summary>Gets the time averaged (s).</summary>
    public double Time { get; private set; }

    public bool IsEmpty => Time <= 0;

    /// <summary>Adds the solver's current state for <paramref name="dt"/> seconds.</summary>
    public void Add(AirflowSolver solver, double dt)
    {
        if (dt <= 0) return;
        var (cx, cy, cz) = solver.CenterVelocity;
        var nu = solver.Viscosity;
        var cloud = solver.Cloud;
        var theta = solver.Temperature;
        var vapor = solver.Vapor;
        float w = (float)dt, threshold = AirflowSolver.CloudWater;
        int ny = Grid.Ny;
        Parallel.For(0, Grid.Nx * Grid.Nz, column =>
        {
            for (int c = column * ny, end = c + ny; c < end; c++)
            {
                if (!solver.IsAir(c)) continue;
                float x = cx[c], y = cy[c], z = cz[c];
                _x[c] += w * x;
                _y[c] += w * y;
                _z[c] += w * z;
                _xx[c] += w * x * x;
                _yy[c] += w * y * y;
                _zz[c] += w * z * z;
                _subgrid[c] += w * nu[c] * nu[c] * _subgridScale;
                if (y >= LiftThreshold)
                {
                    _liftTime[c] += w;
                    _liftSum[c] += w * y;
                }
                if (cloud[c] > threshold) _cloudTime[c] += w;
                _theta[c] += w * theta[c];
                _vapor[c] += w * vapor[c];
            }
        });
        Time += dt;
    }

    /// <summary>Puts the averages into <paramref name="cells"/>.</summary>
    public void Fill(AirflowCells cells)
    {
        if (IsEmpty) return;
        float t = (float)Time;
        Parallel.For(0, Grid.Nx * Grid.Nz, column =>
        {
            for (int c = column * Grid.Ny, end = c + Grid.Ny; c < end; c++)
            {
                float x = _x[c] / t, y = _y[c] / t, z = _z[c] / t;
                float variance = Math.Max(0, (_xx[c] / t - x * x + _yy[c] / t - y * y + _zz[c] / t - z * z) / 3);
                float liftTime = _liftTime[c];
                cells.X[c] = x;
                cells.Y[c] = y;
                cells.Z[c] = z;
                cells.Turbulence[c] = MathF.Sqrt(variance + _subgrid[c] / t);
                cells.LiftChance[c] = liftTime / t;
                cells.LiftStrength[c] = liftTime > 0 ? _liftSum[c] / liftTime : 0;
                cells.Cloud[c] = _cloudTime[c] / t;
                cells.Temperature[c] = _theta[c] / t;
                cells.Vapor[c] = _vapor[c] / t;
            }
        });
    }
}
