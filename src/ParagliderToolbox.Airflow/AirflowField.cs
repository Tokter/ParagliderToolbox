using System.Numerics;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Airflow;

/// <summary>The airflow at a point.</summary>
/// <param name="Wind">The mean wind (m/s; +X east, +Y up, +Z south).</param>
/// <param name="Turbulence">The gusts' typical size: the standard deviation of the wind around its mean (m/s).</param>
/// <param name="LiftChance">The share of the time the air rises at 1 m/s or more (0–1).</param>
/// <param name="LiftStrength">How fast it rises then (m/s).</param>
/// <param name="Cloud">The share of the time there is cloud (0–1).</param>
/// <param name="Temperature">How much warmer the air is than the morning sounding (K, potential temperature).</param>
public readonly record struct AirflowSample(Vector3 Wind, float Turbulence, float LiftChance, float LiftStrength, float Cloud, float Temperature)
{
    /// <summary>Gets the horizontal wind speed (m/s).</summary>
    public float HorizontalSpeed => MathF.Sqrt(Wind.X * Wind.X + Wind.Z * Wind.Z);
}

/// <summary>A thermal found in the simulated airflow: where the air rises on average, from its source on the ground up.</summary>
/// <param name="X">X where it leaves the ground (m).</param>
/// <param name="Z">Z where it leaves the ground (m).</param>
/// <param name="Ground">The ground there (m above sea level).</param>
/// <param name="Top">How high it rises (m above sea level).</param>
/// <param name="Climb">Its mean climb in its core where it is strongest (m/s, before a glider's own sink).</param>
/// <param name="CloudBase">The base of its cumulus (m), or NaN when it stays blue.</param>
public sealed record Thermal(double X, double Z, double Ground, double Top, double Climb, double CloudBase)
{
    /// <summary>Gets its core from the ground up, a point per level: thermals lean with the wind.</summary>
    public IReadOnlyList<Vector3> Core { get; init; } = [];
}

/// <summary>A likely thermal trigger: a hot spot on the ground, or one where the terrain stands out.</summary>
/// <param name="X">X (m).</param>
/// <param name="Z">Z (m).</param>
/// <param name="Height">The ground there (m above sea level).</param>
/// <param name="Score">How likely it releases thermals (higher is likelier; above 1 is a strong one).</param>
/// <param name="Heat">The sensible heat there (W/m²).</param>
/// <param name="Cover">The land cover there.</param>
public sealed record ThermalTrigger(double X, double Z, double Height, double Score, double Heat, LandCover Cover);

/// <summary>What an airflow was simulated with.</summary>
public sealed record AirflowInfo
{
    /// <summary>Gets the latitude of the frame's origin.</summary>
    public double Latitude { get; init; }

    /// <summary>Gets the longitude of the frame's origin.</summary>
    public double Longitude { get; init; }

    /// <summary>Gets the direction the wind blew from (degrees clockwise from north).</summary>
    public double WindDirection { get; init; }

    /// <summary>Gets the wind speed above the ground's friction (m/s).</summary>
    public double WindSpeed { get; init; }

    /// <summary>Gets the local time it shows.</summary>
    public DateTime LocalTime { get; init; }

    /// <summary>Gets the local time's offset from UTC (hours).</summary>
    public double UtcOffset { get; init; }

    /// <summary>Gets the sounding's name.</summary>
    public string Sounding { get; init; } = "";

    /// <summary>Gets the sun's azimuth (degrees clockwise from north).</summary>
    public double SunAzimuth { get; init; }

    /// <summary>Gets the sun's elevation (degrees).</summary>
    public double SunElevation { get; init; }

    /// <summary>Gets the simulated time (s).</summary>
    public double SimulatedTime { get; init; }

    /// <summary>Gets the time at the end the fields average (s; 0 for a snapshot of one moment).</summary>
    public double AveragedTime { get; init; }

    /// <summary>Gets the time steps simulated.</summary>
    public int Steps { get; init; }

    /// <summary>Gets how long the simulation took (s).</summary>
    public double ComputeTime { get; init; }

    /// <summary>Gets what it was simulated on (the CPU or a graphics card).</summary>
    public string Device { get; init; } = "";

    /// <summary>Gets when it was simulated (UTC).</summary>
    public DateTime Created { get; init; }
}

/// <summary>
/// A simulated airflow over a terrain, ready for a game: the mean wind, the turbulence, where and how strongly the air
/// rises, the clouds and the warming on a grid of cells, the ground under it, maps of the surface heating and the
/// convective boundary layer, the thermals and their likely triggers. <see cref="Sample"/> reads it at any point;
/// <see cref="Save"/> and <see cref="Load"/> keep it in a file (see docs/AirflowFormat.md).
/// </summary>
/// <remarks>
/// The frame is the terrain's: +X east, +Y up (heights above sea level), +Z south, the origin at the terrain's center.
/// The cells are <see cref="CellSize"/> wide and <see cref="LayerHeight"/> high from (<see cref="X0"/>, <see cref="Y0"/>,
/// <see cref="Z0"/>); values are at their centers. 3D fields go level by level from the bottom, each level row by row
/// from north to south, each row from west to east: <c>(j * Nz + k) * Nx + i</c> (<see cref="Index"/>). Maps go row by
/// row: <c>k * Nx + i</c>.
/// </remarks>
public sealed class AirflowField
{
    /// <summary>Initializes an empty field of the given grid (the arrays filled by the caller).</summary>
    public AirflowField(int nx, int ny, int nz, double cellSize, double layerHeight, double x0, double y0, double z0)
    {
        Nx = nx;
        Ny = ny;
        Nz = nz;
        CellSize = cellSize;
        LayerHeight = layerHeight;
        X0 = x0;
        Y0 = y0;
        Z0 = z0;
        int cells = nx * ny * nz, columns = nx * nz;
        WindX = new float[cells];
        WindY = new float[cells];
        WindZ = new float[cells];
        Turbulence = new float[cells];
        LiftChance = new float[cells];
        LiftStrength = new float[cells];
        Cloud = new float[cells];
        Temperature = new float[cells];
        Vapor = new float[cells];
        GroundHeight = new float[columns];
        SurfaceHeat = new float[columns];
        LandCover = new byte[columns];
        BoundaryLayerTop = new float[columns];
        CloudBase = new float[columns];
        ThermalVelocity = new float[columns];
    }

    /// <summary>Gets the cells from west to east.</summary>
    public int Nx { get; }

    /// <summary>Gets the levels from the bottom up.</summary>
    public int Ny { get; }

    /// <summary>Gets the cells from north to south.</summary>
    public int Nz { get; }

    /// <summary>Gets the horizontal size of a cell (m).</summary>
    public double CellSize { get; }

    /// <summary>Gets the height of a cell (m).</summary>
    public double LayerHeight { get; }

    /// <summary>Gets the west edge (m).</summary>
    public double X0 { get; }

    /// <summary>Gets the bottom (m above sea level).</summary>
    public double Y0 { get; }

    /// <summary>Gets the north edge (m).</summary>
    public double Z0 { get; }

    /// <summary>Gets the mean wind along X (m/s).</summary>
    public float[] WindX { get; }

    /// <summary>Gets the mean vertical wind (m/s): positive rises.</summary>
    public float[] WindY { get; }

    /// <summary>Gets the mean wind along Z (m/s).</summary>
    public float[] WindZ { get; }

    /// <summary>Gets the turbulence: the standard deviation of the wind around its mean (m/s).</summary>
    public float[] Turbulence { get; }

    /// <summary>Gets the share of the time the air rises at 1 m/s or more.</summary>
    public float[] LiftChance { get; }

    /// <summary>Gets the mean climb of the air when it rises at 1 m/s or more (m/s).</summary>
    public float[] LiftStrength { get; }

    /// <summary>Gets the share of the time there is cloud (in a snapshot: how cloudy, by the cloud water: 1 from 0.5 g/kg).</summary>
    public float[] Cloud { get; }

    /// <summary>Gets how much warmer the air is than the morning sounding (K).</summary>
    public float[] Temperature { get; }

    /// <summary>Gets the water vapor (kg/kg; all 0 in files from before it was kept).</summary>
    public float[] Vapor { get; }

    /// <summary>Gets the mean ground height of every column (m above sea level).</summary>
    public float[] GroundHeight { get; }

    /// <summary>Gets the sensible heat flux of every column (W/m²).</summary>
    public float[] SurfaceHeat { get; }

    /// <summary>Gets the main land cover of every column (<see cref="Surface.LandCover"/> values).</summary>
    public byte[] LandCover { get; }

    /// <summary>Gets the top of the convective boundary layer of every column: how high thermals go (m above sea level).</summary>
    public float[] BoundaryLayerTop { get; }

    /// <summary>Gets where rising air condenses above every column (m above sea level); cumulus form where it is below <see cref="BoundaryLayerTop"/>.</summary>
    public float[] CloudBase { get; }

    /// <summary>Gets the convective velocity scale w* of every column: the typical climb of its thermals (m/s).</summary>
    public float[] ThermalVelocity { get; }

    /// <summary>Gets or sets the thermals found.</summary>
    public IReadOnlyList<Thermal> Thermals { get; set; } = [];

    /// <summary>Gets or sets the likely thermal triggers.</summary>
    public IReadOnlyList<ThermalTrigger> Triggers { get; set; } = [];

    /// <summary>Gets or sets what it was simulated with.</summary>
    public AirflowInfo Info { get; set; } = new();

    /// <summary>
    /// Gets or sets the base flow's wind along X (m/s; the 3D fields' layout), or <c>null</c>: the mean wind of the air
    /// between the thermals and clouds, from a run that followed them (<see cref="AirflowSettings.TrackConvection"/>). A
    /// game plays its own thermals over it, spawned by <see cref="Convection"/>.
    /// </summary>
    public float[]? BaseWindX { get; set; }

    /// <summary>Gets or sets the base flow's vertical wind (m/s), or <c>null</c>; see <see cref="BaseWindX"/>.</summary>
    public float[]? BaseWindY { get; set; }

    /// <summary>Gets or sets the base flow's wind along Z (m/s), or <c>null</c>; see <see cref="BaseWindX"/>.</summary>
    public float[]? BaseWindZ { get; set; }

    /// <summary>Gets whether the field has a base flow (<see cref="BaseWindX"/>).</summary>
    public bool HasBaseFlow => BaseWindX != null && BaseWindY != null && BaseWindZ != null;

    /// <summary>
    /// Gets or sets the thermals and cumulus followed over the averaged time (their lives and the sites they rise from),
    /// or <c>null</c> from a run that didn't follow them.
    /// </summary>
    public Convection.ConvectionClimate? Convection { get; set; }

    /// <summary>Gets the base flow's wind at a point (m/s; trilinear, as <see cref="Sample"/>), or the mean wind without a base flow.</summary>
    public Vector3 BaseWindAt(double x, double y, double z)
    {
        if (!HasBaseFlow) return WindAt(x, y, z);
        if (y < GroundAt(x, z) || y > Top) return default;
        var at = Locate(x, y, z);
        return new Vector3(at.Lerp(BaseWindX!), at.Lerp(BaseWindY!), at.Lerp(BaseWindZ!));
    }

    /// <summary>
    /// Gets a field like this one whose mean wind is the base flow (the other arrays are this one's, shared), or this
    /// field without a base flow.
    /// </summary>
    public AirflowField BaseFlow() => HasBaseFlow ? new AirflowField(this, BaseWindX!, BaseWindY!, BaseWindZ!) : this;

    // A field with another wind, sharing the rest.
    private AirflowField(AirflowField source, float[] windX, float[] windY, float[] windZ)
    {
        (Nx, Ny, Nz, CellSize, LayerHeight, X0, Y0, Z0) =
            (source.Nx, source.Ny, source.Nz, source.CellSize, source.LayerHeight, source.X0, source.Y0, source.Z0);
        (WindX, WindY, WindZ) = (windX, windY, windZ);
        (Turbulence, LiftChance, LiftStrength, Cloud, Temperature, Vapor) =
            (source.Turbulence, source.LiftChance, source.LiftStrength, source.Cloud, source.Temperature, source.Vapor);
        (GroundHeight, SurfaceHeat, LandCover, BoundaryLayerTop, CloudBase, ThermalVelocity) =
            (source.GroundHeight, source.SurfaceHeat, source.LandCover, source.BoundaryLayerTop, source.CloudBase, source.ThermalVelocity);
        (Thermals, Triggers, Info, Convection) = (source.Thermals, source.Triggers, source.Info, source.Convection);
    }

    /// <summary>Gets the top of the grid (m above sea level).</summary>
    public double Top => Y0 + Ny * LayerHeight;

    /// <summary>Gets the index of cell (i, j, k) in the 3D fields.</summary>
    public int Index(int i, int j, int k) => (j * Nz + k) * Nx + i;

    /// <summary>Gets the ground height at a point (m above sea level; bilinear between the columns' centers).</summary>
    public float GroundAt(double x, double z)
    {
        double fi = Math.Clamp((x - X0) / CellSize - 0.5, 0, Nx - 1), fk = Math.Clamp((z - Z0) / CellSize - 0.5, 0, Nz - 1);
        int i = Math.Min((int)fi, Nx - 2), k = Math.Min((int)fk, Nz - 2);
        float tx = (float)(fi - i), tz = (float)(fk - k);
        int c = k * Nx + i;
        float north = GroundHeight[c] + tx * (GroundHeight[c + 1] - GroundHeight[c]);
        float south = GroundHeight[c + Nx] + tx * (GroundHeight[c + Nx + 1] - GroundHeight[c + Nx]);
        return north + tz * (south - north);
    }

    /// <summary>Gets the airflow at a point (trilinear between the cells' centers; still air below the ground and outside the grid's height).</summary>
    public AirflowSample Sample(double x, double y, double z)
    {
        if (y < GroundAt(x, z) || y > Top) return default;
        var at = Locate(x, y, z);
        return new AirflowSample(new Vector3(at.Lerp(WindX), at.Lerp(WindY), at.Lerp(WindZ)), at.Lerp(Turbulence), at.Lerp(LiftChance),
            at.Lerp(LiftStrength), at.Lerp(Cloud), at.Lerp(Temperature));
    }

    // The cell corner below a point and the point's place between the cells' centers.
    private Interpolation Locate(double x, double y, double z)
    {
        double fi = Math.Clamp((x - X0) / CellSize - 0.5, 0, Nx - 1);
        double fj = Math.Clamp((y - Y0) / LayerHeight - 0.5, 0, Ny - 1);
        double fk = Math.Clamp((z - Z0) / CellSize - 0.5, 0, Nz - 1);
        int i = Math.Min((int)fi, Nx - 2), j = Math.Min((int)fj, Ny - 2), k = Math.Min((int)fk, Nz - 2);
        return new Interpolation(Index(i, j, k), Nx, Nx * Nz, (float)(fi - i), (float)(fj - j), (float)(fk - k));
    }

    private readonly record struct Interpolation(int B, int Dk, int Dj, float Tx, float Ty, float Tz)
    {
        public float Lerp(float[] f)
        {
            float a = f[B] + Tx * (f[B + 1] - f[B]), c = f[B + Dk] + Tx * (f[B + Dk + 1] - f[B + Dk]);
            float e = f[B + Dj] + Tx * (f[B + Dj + 1] - f[B + Dj]), g = f[B + Dj + Dk] + Tx * (f[B + Dj + Dk + 1] - f[B + Dj + Dk]);
            float low = a + Tz * (c - a), high = e + Tz * (g - e);
            return low + Ty * (high - low);
        }
    }

    /// <summary>Gets the mean wind at a point (m/s); see <see cref="Sample"/>.</summary>
    public Vector3 WindAt(double x, double y, double z) => Sample(x, y, z).Wind;

    /// <summary>Gets the value of a map at a point (nearest column).</summary>
    public float MapAt(float[] map, double x, double z)
    {
        int i = Math.Clamp((int)((x - X0) / CellSize), 0, Nx - 1), k = Math.Clamp((int)((z - Z0) / CellSize), 0, Nz - 1);
        return map[k * Nx + i];
    }

    /// <summary>Writes the field (see docs/AirflowFormat.md).</summary>
    public void Save(Stream stream) => AirflowFile.Write(this, stream);

    /// <summary>Reads a field written by <see cref="Save"/>.</summary>
    /// <exception cref="InvalidDataException">The data isn't an airflow field.</exception>
    public static AirflowField Load(Stream stream) => AirflowFile.Read(stream);

    /// <summary>Gets the field as bytes (see <see cref="Save"/>).</summary>
    public byte[] ToBytes()
    {
        using var memory = new MemoryStream();
        Save(memory);
        return memory.ToArray();
    }

    /// <summary>Reads a field from bytes written by <see cref="ToBytes"/>.</summary>
    public static AirflowField FromBytes(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        return Load(memory);
    }
}
