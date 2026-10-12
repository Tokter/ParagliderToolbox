namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>
/// What a field is made of, per cell in the solver's layout (<see cref="AirflowGrid.Cell"/>) and per column: the wind
/// at the cells' centers, its variability, the lift statistics, the clouds, the warming and the vapor, and the
/// ground's heat. <see cref="IAirflowSolver.Averages"/> fills it with the running averages,
/// <see cref="IAirflowSolver.Snapshot()"/> with the state of one moment.
/// </summary>
public sealed class AirflowCells
{
    /// <summary>Initializes the arrays for <paramref name="grid"/>.</summary>
    public AirflowCells(AirflowGrid grid)
    {
        int cells = grid.CellCount, columns = grid.Nx * grid.Nz;
        X = new float[cells];
        Y = new float[cells];
        Z = new float[cells];
        Turbulence = new float[cells];
        LiftChance = new float[cells];
        LiftStrength = new float[cells];
        Cloud = new float[cells];
        Temperature = new float[cells];
        Vapor = new float[cells];
        CloudWater = new float[cells];
        SurfaceHeat = new float[columns];
        WaterHeat = new float[columns];
    }

    /// <summary>Gets the wind along X (m/s).</summary>
    public float[] X { get; }

    /// <summary>Gets the vertical wind (m/s).</summary>
    public float[] Y { get; }

    /// <summary>Gets the wind along Z (m/s).</summary>
    public float[] Z { get; }

    /// <summary>Gets the standard deviation of the wind (m/s; a snapshot: the sub-grid part only).</summary>
    public float[] Turbulence { get; }

    /// <summary>Gets the share of the time the air rises at 1 m/s or more (a snapshot: 0 or 1).</summary>
    public float[] LiftChance { get; }

    /// <summary>Gets the mean climb while the air rises at 1 m/s or more (m/s).</summary>
    public float[] LiftStrength { get; }

    /// <summary>Gets the share of the time there is cloud (a snapshot: 0 or 1).</summary>
    public float[] Cloud { get; }

    /// <summary>Gets the potential temperature above the sounding's (K).</summary>
    public float[] Temperature { get; }

    /// <summary>Gets the water vapor (kg/kg).</summary>
    public float[] Vapor { get; }

    /// <summary>Gets the cloud water (kg/kg; a snapshot's: the averages leave it 0).</summary>
    public float[] CloudWater { get; }

    /// <summary>Gets the sensible heat flux of every column at the last step (W/m²).</summary>
    public float[] SurfaceHeat { get; }

    /// <summary>Gets the sensible heat flux of every column's water at the last step (W/m² of water).</summary>
    public float[] WaterHeat { get; }
}

/// <summary>
/// A fluid solver of the airflow: the CPU's <see cref="AirflowSolver"/>, or one on a graphics card (the
/// ParagliderToolbox.Airflow.Gpu library). It steps the flow, keeps running averages of it, and gives them (or the state
/// of the moment) as <see cref="AirflowCells"/>.
/// </summary>
public interface IAirflowSolver : IDisposable
{
    /// <summary>Gets the grid.</summary>
    AirflowGrid Grid { get; }

    /// <summary>Gets the conditions.</summary>
    AirflowConditions Conditions { get; }

    /// <summary>Gets what it runs on, for people ("CPU, 32 threads", the graphics card's name).</summary>
    string Device { get; }

    /// <summary>Gets the simulated time (s).</summary>
    double Time { get; }

    /// <summary>Gets the steps made.</summary>
    int Steps { get; }

    /// <summary>Gets the last time step (s).</summary>
    double LastTimeStep { get; }

    /// <summary>Gets the pressure solver's iterations in the last step.</summary>
    int PressureIterations { get; }

    /// <summary>Gets the multigrid levels of the pressure solver.</summary>
    int PressureLevels { get; }

    /// <summary>Gets the time averaged so far (s).</summary>
    double AveragedTime { get; }

    /// <summary>Gets or sets the time the steps spent per phase, measured while it isn't <c>null</c> (for profiling).</summary>
    IDictionary<string, TimeSpan>? PhaseTimes { get; set; }

    /// <summary>Gets the potential temperature of the sounding at level <paramref name="j"/> (K).</summary>
    float BackgroundTemperature(int j);

    /// <summary>Gets whether a cell holds air.</summary>
    bool IsAir(int cell);

    /// <summary>Advances the flow by one time step of at most <paramref name="maxTimeStep"/> seconds (less where the flow is fast); returns it.</summary>
    double Step(double maxTimeStep);

    /// <summary>Adds the current state to the running averages for <paramref name="dt"/> seconds.</summary>
    void Accumulate(double dt);

    /// <summary>Gets the running averages.</summary>
    AirflowCells Averages();

    /// <summary>Gets the state of this moment.</summary>
    AirflowCells Snapshot();

    /// <summary>Puts the state of this moment into <paramref name="cells"/> (of this grid).</summary>
    void Snapshot(AirflowCells cells);
}
