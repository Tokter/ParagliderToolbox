using ILGPU;

namespace ParagliderToolbox.Airflow.Gpu;

/// <summary>The grid and the sounding on the device (see AirflowGrid and AirflowSetup for what they hold).</summary>
internal struct GridViews
{
    public int Nx, Ny, Nz;
    public float H, Dy;
    public ArrayView<float> OpenX, OpenY, OpenZ, Air, Ground;
    public ArrayView<int> FirstLevel, FirstX, FirstZ, IsAir;
    public ArrayView<float> Theta0, Vapor0, Density0, Exner0, Pressure0, ThetaFace;

    public readonly int Cell(int i, int j, int k) => (k * Nx + i) * Ny + j;

    public readonly int FaceX(int i, int j, int k) => (k * (Nx + 1) + i) * Ny + j;

    public readonly int FaceY(int i, int j, int k) => (k * Nx + i) * (Ny + 1) + j;

    public readonly int FaceZ(int i, int j, int k) => (k * Nx + i) * Ny + j;
}

/// <summary>Three arrays of velocities: on the faces normal to X, Y and Z, or at the cells' centers.</summary>
internal struct VelocityViews
{
    public ArrayView<float> X, Y, Z;
}

/// <summary>The cells' scalars: potential temperature above the sounding's, vapor and cloud water (or work arrays like them).</summary>
internal struct ScalarViews
{
    public ArrayView<float> Theta, Vapor, Cloud;
}

/// <summary>A field with its staggering: index 0 sits at (Ox, Oy, Oz) in cell units; <c>(k * Ni + i) * Nj + j</c>.</summary>
internal struct Staggered
{
    public ArrayView<float> Data;
    public int Ni, Nj, Nk;
    public float Ox, Oy, Oz;
}

/// <summary>The ground's fluxes per column (the land's as they are now), and where the solver puts the ones it works out.</summary>
internal struct SurfaceViews
{
    public ArrayView<float> Heat, Evaporation, Water, WaterTemperature, Drag, SurfaceHeat, WaterHeat;

    // The land's heat and evaporation in the sun and in a cloud's shadow (Heat and Evaporation follow them).
    public ArrayView<float> FullHeat, FullEvaporation, ShadedHeat, ShadedEvaporation;
}

/// <summary>Which sides are upwind (1) and where the sponge starts (in levels).</summary>
internal struct BoundaryParams
{
    public int West, East, North, South;
    public float SpongeStart;
}

/// <summary>A level of the pressure solver's hierarchy.</summary>
internal struct LevelViews
{
    public int Nx, Ny, Nz;
    public ArrayView<float> Wx, Wy, Wz, Diag, InvDiag;
    public ArrayView<int> First;
}

/// <summary>The running sums of the averages (time-weighted), per cell.</summary>
internal struct AverageViews
{
    public ArrayView<float> X, Y, Z, XX, YY, ZZ, Subgrid, LiftTime, LiftSum, CloudTime, Theta, Vapor;
}

/// <summary>Three floats (System.Numerics' Vector3 is built on intrinsics the kernel compiler doesn't know).</summary>
internal readonly struct Float3(float x, float y, float z)
{
    public readonly float X = x, Y = y, Z = z;
}
