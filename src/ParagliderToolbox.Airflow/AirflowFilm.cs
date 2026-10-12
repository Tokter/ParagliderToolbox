namespace ParagliderToolbox.Airflow;

/// <summary>
/// An airflow over time, compact, for viewers: every column's lift at a few heights above the ground, its cloud (base, top
/// and water) and the sunshine its ground gets, frame by frame (see <see cref="AirflowSimulation.FilmInterval"/>). Frames
/// are added while the simulation runs and can be read meanwhile (from another thread).
/// </summary>
public sealed class AirflowFilm
{
    private readonly List<AirflowFilmFrame> _frames = [];

    /// <summary>The heights above the ground the lift is kept at (m).</summary>
    public static readonly double[] LiftHeights = [100, 200, 300, 500, 800, 1200, 1700];

    /// <summary>The climb of a stored lift step (m/s): ±12.7 m/s in a signed byte.</summary>
    public const float LiftStep = 0.1f;

    /// <summary>The cloud water path of a stored step (kg/m²): up to 0.5 kg/m² in a byte.</summary>
    public const float WaterStep = 0.002f;

    /// <summary>Initializes an empty film of a grid's columns.</summary>
    public AirflowFilm(int nx, int nz, double cellSize, double x0, double z0, float[] groundHeight)
    {
        Nx = nx;
        Nz = nz;
        CellSize = cellSize;
        X0 = x0;
        Z0 = z0;
        GroundHeight = groundHeight;
    }

    /// <summary>Gets the columns from west to east.</summary>
    public int Nx { get; }

    /// <summary>Gets the columns from north to south.</summary>
    public int Nz { get; }

    /// <summary>Gets the columns' size (m).</summary>
    public double CellSize { get; }

    /// <summary>Gets the west edge (m).</summary>
    public double X0 { get; }

    /// <summary>Gets the north edge (m).</summary>
    public double Z0 { get; }

    /// <summary>Gets every column's mean ground height (m above sea level; <c>k * Nx + i</c>).</summary>
    public float[] GroundHeight { get; }

    /// <summary>Gets the frames so far.</summary>
    public int Count
    {
        get
        {
            lock (_frames) return _frames.Count;
        }
    }

    /// <summary>Gets a frame.</summary>
    public AirflowFilmFrame this[int index]
    {
        get
        {
            lock (_frames) return _frames[index];
        }
    }

    /// <summary>Adds a frame.</summary>
    public void Add(AirflowFilmFrame frame)
    {
        lock (_frames) _frames.Add(frame);
    }

    /// <summary>Gets the frame nearest a time (s of simulated time), or <c>null</c> before the first.</summary>
    public AirflowFilmFrame? At(double time)
    {
        lock (_frames)
        {
            if (_frames.Count == 0) return null;
            int low = 0, high = _frames.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_frames[middle].Time < time) low = middle + 1;
                else high = middle;
            }
            return low > 0 && time - _frames[low - 1].Time < _frames[low].Time - time ? _frames[low - 1] : _frames[low];
        }
    }

    /// <summary>Gets the column at a point (or the nearest at the edge).</summary>
    public int Column(double x, double z) =>
        Math.Clamp((int)((z - Z0) / CellSize), 0, Nz - 1) * Nx + Math.Clamp((int)((x - X0) / CellSize), 0, Nx - 1);
}

/// <summary>One moment of an <see cref="AirflowFilm"/>.</summary>
/// <param name="Time">The simulated time (s).</param>
/// <param name="Lift">The vertical wind at the <see cref="AirflowFilm.LiftHeights"/> (steps of <see cref="AirflowFilm.LiftStep"/>): <c>height * columns + column</c>.</param>
/// <param name="CloudBase">Every column's cloud base (m above sea level), 0 without cloud.</param>
/// <param name="CloudTop">Every column's cloud top (m above sea level), 0 without cloud.</param>
/// <param name="CloudWater">Every column's cloud water path (steps of <see cref="AirflowFilm.WaterStep"/>).</param>
/// <param name="Sunshine">The share of the sun's heat every column's ground gets (0–255 for 0–1; less in a cloud's shadow).</param>
public sealed record AirflowFilmFrame(double Time, sbyte[] Lift, ushort[] CloudBase, ushort[] CloudTop, byte[] CloudWater, byte[] Sunshine)
{
    /// <summary>Gets the vertical wind of a column at a height above the ground (m/s), between the kept heights.</summary>
    public float LiftAt(int column, double above)
    {
        var heights = AirflowFilm.LiftHeights;
        int columns = Sunshine.Length;
        if (above <= heights[0]) return Lift[column] * AirflowFilm.LiftStep;
        for (int n = 1; n < heights.Length; n++)
        {
            if (above > heights[n]) continue;
            float t = (float)((above - heights[n - 1]) / (heights[n] - heights[n - 1]));
            return (Lift[(n - 1) * columns + column] * (1 - t) + Lift[n * columns + column] * t) * AirflowFilm.LiftStep;
        }
        return Lift[(heights.Length - 1) * columns + column] * AirflowFilm.LiftStep;
    }
}
