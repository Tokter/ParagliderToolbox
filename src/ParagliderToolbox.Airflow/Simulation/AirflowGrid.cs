using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>
/// The solver's grid over a terrain: cubes of <see cref="CellSize"/> by <see cref="LayerHeight"/> from below the lowest
/// ground to <c>topClearance</c> above the highest, with the terrain cut into them. Every face knows the share of it that
/// is open air (<see cref="OpenX"/>, <see cref="OpenY"/>, <see cref="OpenZ"/>) and every cell its share of air
/// (<see cref="Air"/>) and of the ground surface (<see cref="Ground"/>), measured on sub-cell samples of the heightfield,
/// so the flow follows slopes smoothly instead of in steps.
/// </summary>
/// <remarks>
/// <para>
/// The frame is the terrain's (+X east, +Y up, +Z south). Cell (i, j, k) spans X0 + i·h … X0 + (i+1)·h, Y0 + j·dy …,
/// Z0 + k·h …; j counts the levels upwards. Arrays of cells are column by column, levels innermost:
/// <c>(k * Nx + i) * Ny + j</c> (<see cref="Cell"/>); the faces of each axis likewise with one more along their axis
/// (<see cref="FaceX"/>, <see cref="FaceY"/>, <see cref="FaceZ"/>). Velocities live on the faces (a staggered grid),
/// temperatures and moisture in the cells.
/// </para>
/// </remarks>
public sealed class AirflowGrid
{
    // Faces with less open air than this count as closed, and cells with less air as ground: slivers of air are hardly
    // constrained by the pressure and would carry wild velocities.
    private const float MinimumOpening = 0.1f;
    private const float MinimumAir = 0.15f;

    /// <summary>Lays a grid over <paramref name="terrain"/>.</summary>
    /// <param name="terrain">The terrain; the grid covers as much of it as whole cells do, centered.</param>
    /// <param name="cellSize">The horizontal size of a cell (m).</param>
    /// <param name="layerHeight">The height of a cell (m).</param>
    /// <param name="topClearance">How far the grid reaches above the highest ground (m).</param>
    /// <param name="subsamples">The heightfield samples per cell side the shares are measured on.</param>
    public AirflowGrid(HeightField terrain, double cellSize, double layerHeight, double topClearance, int subsamples = 4)
    {
        Terrain = terrain;
        CellSize = cellSize;
        LayerHeight = layerHeight;
        Nx = Math.Max(4, (int)Math.Floor(terrain.Width / cellSize));
        Nz = Math.Max(4, (int)Math.Floor(terrain.Depth / cellSize));
        X0 = terrain.X0 + (terrain.Width - Nx * cellSize) / 2;
        Z0 = terrain.Z0 + (terrain.Depth - Nz * cellSize) / 2;
        Y0 = Math.Floor((terrain.MinHeight - layerHeight) / layerHeight) * layerHeight;
        Ny = Math.Max(4, (int)Math.Ceiling((terrain.MaxHeight + topClearance - Y0) / layerHeight));

        int s = Math.Max(1, subsamples);
        Subsamples = s;
        double h = cellSize, dy = layerHeight;
        int fineX = Nx * s, fineZ = Nz * s;
        // The ground under each sub-cell sample, and along the faces' lines.
        var fine = new float[fineX * fineZ];
        var lineX = new float[(Nx + 1) * fineZ]; // along z at x = X0 + i·h
        var lineZ = new float[(Nz + 1) * fineX]; // along x at z = Z0 + k·h
        Parallel.For(0, fineZ, b =>
        {
            double z = Z0 + (b + 0.5) * h / s;
            for (int a = 0; a < fineX; a++) fine[b * fineX + a] = terrain.HeightAt(X0 + (a + 0.5) * h / s, z);
            for (int i = 0; i <= Nx; i++) lineX[i * fineZ + b] = terrain.HeightAt(X0 + i * h, z);
        });
        Parallel.For(0, Nz + 1, k =>
        {
            for (int a = 0; a < fineX; a++) lineZ[k * fineX + a] = terrain.HeightAt(X0 + (a + 0.5) * h / s, Z0 + k * h);
        });

        Air = new float[Nx * Ny * Nz];
        Ground = new float[Nx * Ny * Nz];
        OpenX = new float[(Nx + 1) * Ny * Nz];
        OpenY = new float[Nx * (Ny + 1) * Nz];
        OpenZ = new float[Nx * Ny * (Nz + 1)];
        GroundHeight = new float[Nx * Nz];
        FirstLevel = new int[Nx * Nz];
        float invS2 = 1f / (s * s);
        Parallel.For(0, Nz, k =>
        {
            for (int i = 0; i < Nx; i++)
            {
                int column = k * Nx + i;
                double sum = 0;
                for (int b = 0; b < s; b++)
                {
                    for (int a = 0; a < s; a++) sum += fine[(k * s + b) * fineX + i * s + a];
                }
                GroundHeight[column] = (float)(sum * invS2);
                int first = Ny;
                for (int j = 0; j < Ny; j++)
                {
                    double bottom = Y0 + j * dy, top = bottom + dy;
                    float air = 0, ground = 0, below = 0;
                    for (int b = 0; b < s; b++)
                    {
                        for (int a = 0; a < s; a++)
                        {
                            float g = fine[(k * s + b) * fineX + i * s + a];
                            air += (float)Math.Clamp((top - g) / dy, 0, 1);
                            if (g >= bottom && g < top) ground++;
                            if (g < bottom) below++;
                        }
                    }
                    int c = Cell(i, j, k);
                    Air[c] = air * invS2;
                    Ground[c] = ground * invS2;
                    // The face under the cell is open where the ground is below it (the domain's floor is closed).
                    float openY = j == 0 ? 0 : below * invS2;
                    OpenY[FaceY(i, j, k)] = openY < MinimumOpening ? 0 : openY;
                    if (Air[c] > 0 && first == Ny) first = j;
                }
                // The lid is closed.
                OpenY[FaceY(i, Ny, k)] = 0;
                FirstLevel[column] = first;
            }
        });
        Parallel.For(0, Nz, k =>
        {
            for (int i = 0; i <= Nx; i++)
            {
                for (int j = 0; j < Ny; j++)
                {
                    double top = Y0 + (j + 1) * dy;
                    float open = 0;
                    for (int b = 0; b < s; b++) open += (float)Math.Clamp((top - lineX[i * fineZ + k * s + b]) / dy, 0, 1);
                    open /= s;
                    OpenX[FaceX(i, j, k)] = open < MinimumOpening ? 0 : open;
                }
            }
        });
        Parallel.For(0, Nz + 1, k =>
        {
            for (int i = 0; i < Nx; i++)
            {
                for (int j = 0; j < Ny; j++)
                {
                    double top = Y0 + (j + 1) * dy;
                    float open = 0;
                    for (int a = 0; a < s; a++) open += (float)Math.Clamp((top - lineZ[k * fineX + i * s + a]) / dy, 0, 1);
                    open /= s;
                    OpenZ[FaceZ(i, j, k)] = open < MinimumOpening ? 0 : open;
                }
            }
        });

        // Slivers become ground: their faces close and their share of the surface moves to the cell above.
        for (int k = 0; k < Nz; k++)
        {
            for (int i = 0; i < Nx; i++)
            {
                for (int j = 0; j < Ny; j++)
                {
                    int c = Cell(i, j, k);
                    if (Air[c] <= 0 || Air[c] >= MinimumAir) continue;
                    Air[c] = 0;
                    if (j + 1 < Ny) Ground[c + 1] += Ground[c];
                    Ground[c] = 0;
                    OpenX[FaceX(i, j, k)] = OpenX[FaceX(i + 1, j, k)] = 0;
                    OpenY[FaceY(i, j, k)] = OpenY[FaceY(i, j + 1, k)] = 0;
                    OpenZ[FaceZ(i, j, k)] = OpenZ[FaceZ(i, j, k + 1)] = 0;
                }
                int first = Ny;
                for (int j = 0; j < Ny && first == Ny; j++)
                {
                    if (Air[Cell(i, j, k)] > 0) first = j;
                }
                FirstLevel[k * Nx + i] = first;
            }
        }
    }

    /// <summary>Gets the terrain.</summary>
    public HeightField Terrain { get; }

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

    /// <summary>Gets the heightfield samples per cell side the shares were measured on.</summary>
    public int Subsamples { get; }

    /// <summary>Gets the share of every cell that is air.</summary>
    public float[] Air { get; }

    /// <summary>Gets the share of every cell's footprint whose ground surface lies in the cell.</summary>
    public float[] Ground { get; }

    /// <summary>Gets the open share of every face normal to X.</summary>
    public float[] OpenX { get; }

    /// <summary>Gets the open share of every face normal to Y (the lid's and the floor's are closed).</summary>
    public float[] OpenY { get; }

    /// <summary>Gets the open share of every face normal to Z.</summary>
    public float[] OpenZ { get; }

    /// <summary>Gets the mean ground height of every column (<c>k * Nx + i</c>).</summary>
    public float[] GroundHeight { get; }

    /// <summary>Gets the lowest level of every column with air in it.</summary>
    public int[] FirstLevel { get; }

    /// <summary>Gets the number of cells.</summary>
    public int CellCount => Nx * Ny * Nz;

    /// <summary>Gets the number of cells with air in them.</summary>
    public int AirCellCount => Air.Count(a => a > 0);

    /// <summary>Gets the index of cell (i, j, k).</summary>
    public int Cell(int i, int j, int k) => (k * Nx + i) * Ny + j;

    /// <summary>Gets the index of the face normal to X on the west of cell (i, j, k); i = Nx is the east edge.</summary>
    public int FaceX(int i, int j, int k) => (k * (Nx + 1) + i) * Ny + j;

    /// <summary>Gets the index of the face normal to Y under cell (i, j, k); j = Ny is the lid.</summary>
    public int FaceY(int i, int j, int k) => (k * Nx + i) * (Ny + 1) + j;

    /// <summary>Gets the index of the face normal to Z on the north of cell (i, j, k); k = Nz is the south edge.</summary>
    public int FaceZ(int i, int j, int k) => (k * Nx + i) * Ny + j;

    /// <summary>Gets the height of the middle of level <paramref name="j"/> (m).</summary>
    public double LevelHeight(int j) => Y0 + (j + 0.5) * LayerHeight;

    /// <summary>Gets the center of cell (i, j, k).</summary>
    public (double X, double Y, double Z) CellCenter(int i, int j, int k) =>
        (X0 + (i + 0.5) * CellSize, Y0 + (j + 0.5) * LayerHeight, Z0 + (k + 0.5) * CellSize);
}
