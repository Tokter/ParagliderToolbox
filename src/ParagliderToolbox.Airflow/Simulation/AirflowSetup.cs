using ParagliderToolbox.Airflow.Atmosphere;

namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>
/// What a solver works out of its grid and conditions before the first step, the same for the CPU's and a graphics
/// card's: the sounding per level, the wind's direction and the upwind sides, the inflow's wind on every face, the
/// ground's heat, vapor and friction per column, where the air starts above the terrain on every face column, which
/// cells hold air, the pressure solver's hierarchy and the starting air. The constants of the physics are here too.
/// </summary>
internal sealed class AirflowSetup
{
    public const double Karman = 0.4;
    public const double Smagorinsky = 0.18;
    public const double CourantNumber = 1.2;
    public const int RelaxationCells = 5;
    public const double RelaxationTime = 90;
    public const double OutflowRelaxationTime = 400;
    public const double SpongeShare = 0.25;
    public const double SpongeTime = 120;
    public const float CloudThreshold = 1e-5f;

    // The optical depth of a cloud per kg/m² of cloud water along the sun's ray (1.5 / (ρ water · 10 µm droplets)).
    public const float CloudExtinction = 150;

    // How fast the ground's heat follows the sunlight that reaches it (s): a sunlit surface cools within minutes in a
    // cloud's shadow.
    public const float GroundResponseTime = 180;

    // The inflow noise: blocks of this many cells a side, this many blocks deep inside the relaxation band.
    public const int NoiseBlock = 8;
    public const int NoiseBlocks = 2;

    public AirflowSetup(AirflowGrid grid, AirflowConditions conditions)
    {
        Grid = grid;
        Conditions = conditions;
        var sounding = conditions.Sounding;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;

        Theta0 = new float[ny];
        Vapor0 = new float[ny];
        Density0 = new float[ny];
        Exner0 = new float[ny];
        Pressure0 = new float[ny];
        for (int j = 0; j < ny; j++)
        {
            double y = grid.LevelHeight(j);
            Theta0[j] = (float)sounding.PotentialTemperatureAt(y);
            Vapor0[j] = (float)sounding.MixingRatioAt(y);
            Density0[j] = (float)sounding.DensityAt(y);
            Pressure0[j] = (float)sounding.PressureAt(y);
            Exner0[j] = (float)Thermodynamics.Exner(Pressure0[j]);
        }
        ThetaFace = new float[ny + 1];
        for (int j = 0; j <= ny; j++) ThetaFace[j] = (float)sounding.PotentialTemperatureAt(grid.Y0 + j * grid.LayerHeight);

        // The direction the wind blows towards, in the frame.
        double towards = (conditions.WindDirection + 180) * Math.PI / 180;
        WindX = Math.Sin(towards);
        WindZ = -Math.Cos(towards);

        // The inflow's wind on every face: the wind's profile over the ground, level.
        bool windy = conditions.WindSpeed > 0.3;
        Inflow = (windy && WindX > 0.2, windy && WindX < -0.2, windy && WindZ > 0.2, windy && WindZ < -0.2);
        TargetX = new float[grid.OpenX.Length];
        TargetY = new float[grid.OpenY.Length];
        TargetZ = new float[grid.OpenZ.Length];
        float[] targetX = TargetX, targetZ = TargetZ;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i <= nx; i++)
            {
                double ground = (grid.GroundHeight[k * nx + Math.Max(i - 1, 0)] + grid.GroundHeight[k * nx + Math.Min(i, nx - 1)]) / 2;
                for (int j = 0; j < ny; j++) targetX[grid.FaceX(i, j, k)] = (float)(conditions.WindSpeed * WindX * Profile(grid.LevelHeight(j) - ground));
            }
        });
        Parallel.For(0, nz + 1, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                double ground = (grid.GroundHeight[Math.Max(k - 1, 0) * nx + i] + grid.GroundHeight[Math.Min(k, nz - 1) * nx + i]) / 2;
                for (int j = 0; j < ny; j++) targetZ[grid.FaceZ(i, j, k)] = (float)(conditions.WindSpeed * WindZ * Profile(grid.LevelHeight(j) - ground));
            }
        });
        Pressure = new PressureSolver(grid, (!Inflow.West, !Inflow.East, !Inflow.North, !Inflow.South));

        int cells = grid.CellCount;
        IsAir = new bool[cells];
        for (int c = 0; c < cells; c++) IsAir[c] = grid.Air[c] > 0 || Pressure.IsActive(c);

        // The ground's heat, vapor and friction per column.
        int columns = nx * nz;
        Heat = conditions.SensibleHeat ?? new float[columns];
        Evaporation = conditions.Evaporation ?? new float[columns];
        Water = conditions.WaterShare ?? new float[columns];
        WaterTemperature = conditions.WaterTemperature ?? new float[columns];
        Drag = new float[columns];
        double reference = grid.LayerHeight / 2;
        for (int column = 0; column < columns; column++)
        {
            double z0 = Math.Clamp(conditions.Roughness?[column] ?? 0.1, 1e-4, reference / 4);
            double ln = Math.Log(reference / z0);
            Drag[column] = (float)(Karman * Karman / (ln * ln));
        }

        // The clouds' shadows: the ground in the shade, and the sun's ray through the levels (a level up per step).
        if (conditions.ShadedHeat is { } shadedHeat && conditions.SunDirection.Y > 0.05f)
        {
            ShadedHeat = shadedHeat;
            ShadedEvaporation = conditions.ShadedEvaporation ?? Evaporation;
            var sun = conditions.SunDirection;
            RayStepX = (float)(sun.X / sun.Y * grid.LayerHeight / grid.CellSize);
            RayStepZ = (float)(sun.Z / sun.Y * grid.LayerHeight / grid.CellSize);
            RayLength = (float)(grid.LayerHeight / sun.Y);
        }


        // The inflow noise, renewed as the wind crosses a block.
        if (conditions.InflowNoise > 0 && (Inflow.West || Inflow.East || Inflow.North || Inflow.South))
        {
            NoiseAmplitude = (float)conditions.InflowNoise;
            NoisePeriod = NoiseBlock * grid.CellSize / Math.Max(conditions.WindSpeed, 1);
            NoiseLevels = (int)Math.Ceiling(conditions.InflowNoiseDepth / grid.LayerHeight);
        }
        FirstX = new int[(nx + 1) * nz];
        FirstZ = new int[nx * (nz + 1)];
        for (int k = 0; k < nz; k++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int j = 0;
                while (j < ny && grid.OpenX[grid.FaceX(i, j, k)] <= 0) j++;
                FirstX[k * (nx + 1) + i] = j;
            }
        }
        for (int k = 0; k <= nz; k++)
        {
            for (int i = 0; i < nx; i++)
            {
                int j = 0;
                while (j < ny && grid.OpenZ[grid.FaceZ(i, j, k)] <= 0) j++;
                FirstZ[k * nx + i] = j;
            }
        }

        InitialTheta = conditions.InitialTemperature ?? new float[cells];
        if (conditions.InitialVapor is { } vapor)
        {
            InitialVapor = vapor;
        }
        else
        {
            InitialVapor = new float[cells];
            for (int c = 0; c < cells; c++) InitialVapor[c] = Vapor0[c % ny];
        }
    }

    public AirflowGrid Grid { get; }
    public AirflowConditions Conditions { get; }

    /// <summary>Gets the sounding's potential temperature, vapor, density, Exner function and pressure per level.</summary>
    public float[] Theta0 { get; }
    public float[] Vapor0 { get; }
    public float[] Density0 { get; }
    public float[] Exner0 { get; }
    public float[] Pressure0 { get; }

    /// <summary>Gets the sounding's potential temperature at the levels' boundaries.</summary>
    public float[] ThetaFace { get; }

    /// <summary>Gets the unit vector the wind blows towards (X east, Z south).</summary>
    public double WindX { get; }
    public double WindZ { get; }

    /// <summary>Gets which sides the wind enters on.</summary>
    public (bool West, bool East, bool North, bool South) Inflow { get; }

    /// <summary>Gets the pressure solver (its hierarchy of face weights; the CPU solver also solves with it).</summary>
    public PressureSolver Pressure { get; }

    /// <summary>Gets whether every cell holds air (has some, or an open face).</summary>
    public bool[] IsAir { get; }

    /// <summary>Gets the ground's sensible heat (W/m²), evaporation (kg/(m² s)), water share and temperature (K), and drag coefficient per column.</summary>
    public float[] Heat { get; }
    public float[] Evaporation { get; }
    public float[] Water { get; }
    public float[] WaterTemperature { get; }
    public float[] Drag { get; }

    /// <summary>Gets the inflow's wind on every face normal to X, Y and Z.</summary>
    public float[] TargetX { get; }
    public float[] TargetY { get; }
    public float[] TargetZ { get; }

    /// <summary>Gets the ground's heat and evaporation per column in a cloud's shadow, or <c>null</c> without cloud shadows.</summary>
    public float[]? ShadedHeat { get; }
    public float[]? ShadedEvaporation { get; }

    /// <summary>Gets the sun's ray per level up: the cells it moves along X and Z, and its length (m).</summary>
    public float RayStepX { get; }
    public float RayStepZ { get; }
    public float RayLength { get; }

    /// <summary>Gets the inflow noise's amplitude (K; 0 for none), how often it is renewed (s) and how many levels above the ground it reaches.</summary>
    public float NoiseAmplitude { get; }
    public double NoisePeriod { get; } = double.PositiveInfinity;
    public int NoiseLevels { get; }

    /// <summary>Gets the cells column (i, k) lies from the nearest upwind side, or <see cref="int.MaxValue"/> without one.</summary>
    public int UpwindDistance(int i, int k)
    {
        int d = int.MaxValue;
        if (Inflow.West) d = Math.Min(d, i);
        if (Inflow.East) d = Math.Min(d, Grid.Nx - 1 - i);
        if (Inflow.North) d = Math.Min(d, k);
        if (Inflow.South) d = Math.Min(d, Grid.Nz - 1 - k);
        return d;
    }

    /// <summary>Gets the noise (−1 to 1) of the block of cell (i, j, k) in a period (the same on every device).</summary>
    public static float Noise(int i, int j, int k, int epoch)
    {
        uint h = (uint)(i / NoiseBlock) * 73856093u ^ (uint)(j / NoiseBlock) * 19349663u ^ (uint)(k / NoiseBlock) * 83492791u ^ (uint)epoch * 2654435761u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        h *= 3266489917u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 32767.5f - 1;
    }

    /// <summary>Gets the lowest open face of every column of faces normal to X (<c>k * (Nx + 1) + i</c>) and to Z (<c>k * Nx + i</c>).</summary>
    public int[] FirstX { get; }
    public int[] FirstZ { get; }

    /// <summary>Gets the starting potential temperature above the sounding's and vapor of every cell.</summary>
    public float[] InitialTheta { get; }
    public float[] InitialVapor { get; }

    // The wind speed by height above the ground: logarithmic up to the reference height.
    private double Profile(double above)
    {
        const double z0 = 0.1;
        if (above <= 0) return 0;
        return Math.Min(1, Math.Log(1 + above / z0) / Math.Log(1 + Conditions.WindReferenceHeight / z0));
    }
}
