using System.Collections.Concurrent;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>
/// A grid of points in the terrain's <see cref="LocalFrame"/> that a source fills with heights or colors: sample
/// (column, row) is at x = <see cref="X0"/> + column·<see cref="Spacing"/> (east), z = <see cref="Z0"/> +
/// row·<see cref="Spacing"/> (south). Arrays of values are row by row, from the north-west corner.
/// </summary>
public sealed class SampleRequest
{
    private const int LatticeStep = 8;
    private readonly ConcurrentDictionary<ICoordinateSystem, (double[] X, double[] Y)> _projected = new();

    /// <summary>Initializes a request.</summary>
    public SampleRequest(LocalFrame frame, double x0, double z0, double spacing, int columns, int rows)
    {
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns), "A request needs at least one sample.");
        if (!(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(spacing));
        Frame = frame;
        X0 = x0;
        Z0 = z0;
        Spacing = spacing;
        Columns = columns;
        Rows = rows;
        Bounds = frame.Bounds(x0, z0, x0 + (columns - 1) * spacing, z0 + (rows - 1) * spacing);
    }

    /// <summary>Gets the terrain's frame.</summary>
    public LocalFrame Frame { get; }

    /// <summary>Gets the x (east) of the first sample.</summary>
    public double X0 { get; }

    /// <summary>Gets the z (south) of the first sample.</summary>
    public double Z0 { get; }

    /// <summary>Gets the distance between samples (m): sources prefer data about as fine, and average finer data.</summary>
    public double Spacing { get; }

    /// <summary>Gets the samples across.</summary>
    public int Columns { get; }

    /// <summary>Gets the samples down.</summary>
    public int Rows { get; }

    /// <summary>Gets the number of samples.</summary>
    public int Count => Columns * Rows;

    /// <summary>Gets the latitude/longitude box around the samples.</summary>
    public GeoBounds Bounds { get; }

    /// <summary>Gets the position of a sample.</summary>
    public GeoPoint Point(int column, int row) => Frame.ToGeo(X0 + column * Spacing, Z0 + row * Spacing);

    /// <summary>
    /// Gets every sample's coordinates in <paramref name="system"/>: exact on a lattice of every eighth sample,
    /// interpolated in between (the projections are smooth; the error is far below a millimeter).
    /// </summary>
    public (double[] X, double[] Y) Project(ICoordinateSystem system) => _projected.GetOrAdd(system, ProjectAll);

    private (double[] X, double[] Y) ProjectAll(ICoordinateSystem system)
    {
        int latticeColumns = (Columns - 1 + LatticeStep - 1) / LatticeStep + 1;
        int latticeRows = (Rows - 1 + LatticeStep - 1) / LatticeStep + 1;
        var lx = new double[latticeColumns * latticeRows];
        var ly = new double[latticeColumns * latticeRows];
        for (int j = 0; j < latticeRows; j++)
        {
            int row = Math.Min(j * LatticeStep, Rows - 1);
            for (int i = 0; i < latticeColumns; i++)
            {
                int column = Math.Min(i * LatticeStep, Columns - 1);
                var (x, y) = system.Project(Point(column, row));
                lx[j * latticeColumns + i] = x;
                ly[j * latticeColumns + i] = y;
            }
        }

        var xs = new double[Count];
        var ys = new double[Count];
        for (int row = 0; row < Rows; row++)
        {
            int j = Math.Min(row / LatticeStep, latticeRows - 2);
            if (latticeRows == 1) j = 0;
            int rowA = j * LatticeStep, rowB = Math.Min((j + 1) * LatticeStep, Rows - 1);
            double ty = rowB > rowA ? (double)(row - rowA) / (rowB - rowA) : 0;
            for (int column = 0; column < Columns; column++)
            {
                int i = latticeColumns == 1 ? 0 : Math.Min(column / LatticeStep, latticeColumns - 2);
                int columnA = i * LatticeStep, columnB = Math.Min((i + 1) * LatticeStep, Columns - 1);
                double tx = columnB > columnA ? (double)(column - columnA) / (columnB - columnA) : 0;
                int a = j * latticeColumns + i;
                int b = latticeColumns == 1 ? a : a + 1;
                int c = latticeRows == 1 ? a : a + latticeColumns;
                int d = latticeRows == 1 ? b : b + latticeColumns;
                xs[row * Columns + column] = Lerp(Lerp(lx[a], lx[b], tx), Lerp(lx[c], lx[d], tx), ty);
                ys[row * Columns + column] = Lerp(Lerp(ly[a], ly[b], tx), Lerp(ly[c], ly[d], tx), ty);
            }
        }
        return (xs, ys);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
