using System.Numerics;
using ParagliderToolbox.Paraglider.Mathematics;
using ParagliderToolbox.Paraglider.Rigging;

namespace ParagliderToolbox.Paraglider.Geometry;

/// <summary>
/// Builds the high resolution canopy: the skin with cell ballooning, seam creases, tab wrinkles, trailing edge gathering
/// and the air inlets, the tip panels, and the internal ribs (with cross-vents), mini-ribs and diagonal ribs.
/// </summary>
/// <remarks>
/// <para>
/// The skin is a grid of columns (span positions: every rib, and <see cref="Design.GliderDesign.SpanwiseSegmentsPerCell"/>
/// steps across each cell) by rows (positions around the profile, from the upper trailing edge over the nose to the
/// lower trailing edge). Each grid point starts on the smooth surface of the <see cref="GliderShape"/> and is then
/// displaced:
/// </para>
/// <list type="bullet">
/// <item><b>Ballooning</b>: internal pressure bulges the fabric between two ribs into a circular arc whose height
/// (sagitta) is <see cref="Design.GliderDesign.Ballooning"/> times the local cell width, scaled along the chord by
/// <see cref="Design.GliderDesign.BallooningDistribution"/> (small at the reinforced nose, zero at the trailing edge
/// seam). Behind the front edge of the mini-ribs the trailing edge balloons in sub-cells instead.</item>
/// <item><b>Seam creases</b>: irregular ripples along the ribs, zero on the seam and largest a few centimeters beside it.</item>
/// <item><b>Tab wrinkles</b>: radial wrinkles around the line tabs, where the lines pull the ribs down.</item>
/// <item><b>Trailing edge pucker</b>: the trailing edge is pulled forward between the brake tabs and gathers.</item>
/// <item><b>Inlets</b>: the skin between <see cref="Design.GliderDesign.InletStart"/> and
/// <see cref="Design.GliderDesign.InletEnd"/> is left open (except in the closed tip cells); the rims sag into the
/// cell between the ribs.</item>
/// </list>
/// <para>
/// Displacements vanish on the ribs, so ribs built from the skin's rib columns close the cells exactly.
/// </para>
/// </remarks>
public sealed class CanopyBuilder
{
    /// <summary>The number of cells at each tip without an inlet.</summary>
    public const int ClosedTipCells = 1;

    private readonly GliderShape _shape;
    private readonly RiggingLayout _rigging;
    private readonly Design.GliderDesign _design;

    private Column[] _columns = [];
    private double[] _rows = [];
    private Vector3[,] _base = new Vector3[0, 0];
    private Vector3[,] _final = new Vector3[0, 0];
    private int _inletStartRow, _inletEndRow;
    private int _segmentsPerCell;

    /// <summary>A column of the skin grid.</summary>
    /// <param name="Eta">The span position.</param>
    /// <param name="Cell">The cell the column lies in (the cell to its left for rib columns, the last cell for the left tip).</param>
    /// <param name="U">The position across the cell (0 at its right rib, 1 at its left rib).</param>
    /// <param name="Rib">The rib index for rib columns, otherwise −1.</param>
    /// <param name="MiniRib">Whether a mini-rib sits on this column.</param>
    private readonly record struct Column(double Eta, int Cell, double U, int Rib, bool MiniRib);

    /// <summary>Initializes a builder for <paramref name="shape"/> with its line plan.</summary>
    public CanopyBuilder(GliderShape shape, RiggingLayout rigging)
    {
        _shape = shape;
        _rigging = rigging;
        _design = shape.Design;
    }

    /// <summary>Gets the profile positions of the skin rows (−1 upper trailing edge … +1 lower trailing edge).</summary>
    public IReadOnlyList<double> Rows => _rows;

    /// <summary>Builds the skin and the tip panels.</summary>
    public MeshPart BuildCanopy()
    {
        BuildGrid();
        var part = new MeshPart("Canopy", GliderMaterial.Canopy);
        int columns = _columns.Length, rows = _rows.Length;
        var index = new uint[columns, rows];
        for (int j = 0; j < columns; j++)
        {
            for (int i = 0; i < rows; i++)
            {
                double t = _rows[i];
                double x = GliderShape.ChordFraction(t);
                double h = t < 0 ? 1 : t > 0 ? 0 : 0.5;
                var uv = new Vector2((float)((_columns[j].Eta + 1) / 2), (float)((t + 1) / 2));
                index[j, i] = part.AddVertex(_final[j, i], uv, VertexBind.OnCanopy(_columns[j].Eta, x, h));
            }
        }

        for (int j = 0; j < columns - 1; j++)
        {
            bool inletCell = IsInletCell(_columns[j].Cell);
            for (int i = 0; i < rows - 1; i++)
            {
                if (inletCell && i >= _inletStartRow && i < _inletEndRow) continue;
                // Rows run from the upper trailing edge to the lower one, columns from right to left: this winding faces out.
                part.AddQuad(index[j, i], index[j, i + 1], index[j + 1, i + 1], index[j + 1, i]);
            }
        }

        AddTipPanel(part, 0, flip: true);
        AddTipPanel(part, columns - 1, flip: false);
        part.ComputeNormals();
        return part;
    }

    /// <summary>Builds the internal ribs with their cross-vents, the mini-ribs and the diagonal ribs. Call after <see cref="BuildCanopy"/>.</summary>
    public MeshPart BuildRibs()
    {
        if (_columns.Length == 0) BuildGrid();
        var part = new MeshPart("Ribs", GliderMaterial.Ribs);
        int lastRib = _shape.CellCount;
        for (int j = 0; j < _columns.Length; j++)
        {
            var column = _columns[j];
            if (column.Rib > 0 && column.Rib < lastRib) AddRib(part, j, full: true);
            else if (column.MiniRib) AddRib(part, j, full: false);
        }
        if (_design.DiagonalRibs)
        {
            foreach (int rib in _rigging.TabRibs)
            {
                if (rib <= 0 || rib >= lastRib) continue;
                AddDiagonal(part, rib, rib - 1);
                AddDiagonal(part, rib, rib + 1);
            }
        }
        part.ComputeNormals();
        return part;
    }

    #region Grid

    private void BuildGrid()
    {
        var ribs = _shape.RibPositions;
        int mini = Math.Clamp(_design.MiniRibsPerCell, 0, 3);
        int perCell = Math.Max(2, _design.SpanwiseSegmentsPerCell);
        perCell = (perCell + mini) / (mini + 1) * (mini + 1); // mini-ribs land on columns
        _segmentsPerCell = perCell;

        var columns = new List<Column>();
        for (int c = 0; c < _shape.CellCount; c++)
        {
            for (int s = 0; s < perCell; s++)
            {
                double u = s / (double)perCell;
                double eta = ribs[c] + (ribs[c + 1] - ribs[c]) * u;
                bool miniRib = mini > 0 && s > 0 && s % (perCell / (mini + 1)) == 0;
                columns.Add(new Column(eta, c, u, s == 0 ? c : -1, miniRib));
            }
        }
        columns.Add(new Column(1, _shape.CellCount - 1, 1, _shape.CellCount, false));
        _columns = columns.ToArray();

        // Rows: even in t (dense at the nose in x), plus the inlet and mini-rib edges.
        int segments = Math.Max(8, _design.ChordwiseSegments);
        var rows = new List<double>();
        for (int i = 0; i <= 2 * segments; i++) rows.Add(-1 + i / (double)segments);
        double inletStart = GliderShape.ProfileParameter(Math.Abs(_design.InletStart), upper: _design.InletStart < 0);
        double inletEnd = GliderShape.ProfileParameter(Math.Max(_design.InletEnd, Math.Abs(_design.InletStart) + 0.01), upper: false);
        rows.Add(inletStart);
        rows.Add(inletEnd);
        if (mini > 0)
        {
            double miniStart = 1 - Math.Clamp(_design.MiniRibLength, 0.02, 0.5);
            rows.Add(GliderShape.ProfileParameter(miniStart, upper: true));
            rows.Add(GliderShape.ProfileParameter(miniStart, upper: false));
        }
        rows.Sort();
        var unique = new List<double>();
        foreach (double r in rows)
        {
            if (unique.Count == 0 || r - unique[^1] > 1e-4) unique.Add(r);
            else if (r == inletStart || r == inletEnd) unique[^1] = r; // keep the exact inlet edges
        }
        _rows = unique.ToArray();
        _inletStartRow = Array.IndexOf(_rows, inletStart);
        _inletEndRow = Array.IndexOf(_rows, inletEnd);
        if (_inletStartRow < 0) _inletStartRow = NearestRow(inletStart);
        if (_inletEndRow < 0) _inletEndRow = NearestRow(inletEnd);

        ComputeBase();
        ComputeFinal();
    }

    private int NearestRow(double t)
    {
        int best = 0;
        for (int i = 1; i < _rows.Length; i++)
        {
            if (Math.Abs(_rows[i] - t) < Math.Abs(_rows[best] - t)) best = i;
        }
        return best;
    }

    private void ComputeBase()
    {
        int columns = _columns.Length, rows = _rows.Length;
        _base = new Vector3[columns, rows];
        var x = new double[rows];
        var camber = new double[rows];
        var half = new double[rows];
        var shark = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            x[i] = GliderShape.ChordFraction(_rows[i]);
            camber[i] = _shape.Airfoil.Camber(x[i]);
            half[i] = _shape.Airfoil.HalfThickness(x[i]);
            // The shark nose lift is proportional to the thickness: sample it at thickness 1.
            shark[i] = _rows[i] > 0 ? _shape.ProfileHeight(0, _rows[i]) - _shape.Airfoil.Lower(x[i], _shape.Thickness(0)) : 0;
            if (_rows[i] > 0) shark[i] /= _shape.Thickness(0);
        }
        Parallel.For(0, columns, j =>
        {
            double eta = _columns[j].Eta;
            var frame = _shape.Frame(eta);
            double thickness = _shape.Thickness(eta);
            for (int i = 0; i < rows; i++)
            {
                double h = _rows[i] <= 0
                    ? camber[i] + thickness * half[i]
                    : camber[i] - thickness * half[i] + shark[i] * thickness;
                _base[j, i] = frame.Point(x[i], h);
            }
        });
    }

    private void ComputeFinal()
    {
        int columns = _columns.Length, rows = _rows.Length;
        _final = new Vector3[columns, rows];
        var ribs = _shape.RibPositions;
        var balloon = _design.BallooningDistribution;
        double miniStart = 1 - Math.Clamp(_design.MiniRibLength, 0.02, 0.5);
        int mini = Math.Clamp(_design.MiniRibsPerCell, 0, 3);
        var brakeEtas = _rigging.BrakeRibs.Select(r => ribs[r]).Append(-1).Append(1).Distinct().OrderBy(e => e).ToArray();
        var tabs = _rigging.Points.Where(p => p.Kind == RigPointKind.Tab).ToArray();

        Parallel.For(0, columns, j =>
        {
            var column = _columns[j];
            int cell = column.Cell;
            double u = column.U;
            int jStart = cell * _segmentsPerCell;
            int jEnd = jStart + _segmentsPerCell;
            var frame = _shape.Frame(column.Eta);
            double chord = frame.Chord;
            var random = new SmoothNoise(cell * 7919 + 17);

            for (int i = 0; i < rows; i++)
            {
                double t = _rows[i];
                double x = GliderShape.ChordFraction(t);
                bool upper = t < 0;
                var p = _base[j, i];
                var n = BaseNormal(j, i);
                double width = Vector3.Distance(_base[jStart, i], _base[jEnd, i]);
                double fromRib = Math.Min(u, 1 - u) * width;

                // Ballooning: a circular arc across the cell, sub-cells behind the mini-ribs.
                double sideFactor = upper ? 1 : _design.LowerBallooningFactor;
                double rods = _design.LeadingEdgeRods ? 0.55 + 0.45 * GliderShape.SmoothStep(0.02, 0.15, x) : 1;
                double sagitta = _design.Ballooning * width * balloon.Evaluate(x) * sideFactor * rods;
                double d = ArcHeight(u, width, sagitta);
                if (mini > 0)
                {
                    double beta = GliderShape.SmoothStep(miniStart - 0.02, miniStart + 0.02, x);
                    if (beta > 0)
                    {
                        double sub = u * (mini + 1);
                        double subU = sub - Math.Floor(sub);
                        double subWidth = width / (mini + 1);
                        double subSagitta = _design.Ballooning * subWidth * balloon.Evaluate(x) * sideFactor;
                        d = (1 - beta) * d + beta * ArcHeight(subU, subWidth, subSagitta);
                    }
                }

                // Seam creases: irregular ripples beside the rib seams.
                const double creaseOffset = 0.03;
                double envelope = fromRib / creaseOffset * Math.Exp(1 - fromRib / creaseOffset);
                double alongChord = x * chord;
                double side = u < 0.5 ? 0 : 1;
                double crease = _design.SeamCreasing * width * envelope
                    * random.Sample(alongChord / 0.045 + side * 31.7)
                    * (0.4 + 0.6 * balloon.Evaluate(x));
                d += crease;

                // Wrinkles fanning from the line tabs.
                if (_design.TabWrinkles > 0)
                {
                    double ribFade = GliderShape.SmoothStep(0, 0.03, fromRib);
                    foreach (var tab in tabs)
                    {
                        if (tab.Rib != cell && tab.Rib != cell + 1) continue;
                        double ds = (column.Eta - tab.Eta) * _shape.HalfSpan;
                        double dc = (x - tab.Chord) * chord;
                        double r = Math.Sqrt(ds * ds + dc * dc);
                        const double radius = 0.35;
                        if (r >= radius) continue;
                        double angle = Math.Atan2(dc, Math.Abs(ds));
                        double fall = (1 - r / radius) * (1 - r / radius) * GliderShape.SmoothStep(0, 0.05, r);
                        d += _design.TabWrinkles * width * Math.Sin(9 * angle) * fall * ribFade * (upper ? 0.5 : 1);
                    }
                }

                var position = p + n * (float)d;

                // Trailing edge pucker: pulled forward between the brake tabs, with gathers.
                if (_design.TrailingEdgePucker > 0 && x > 0.85)
                {
                    int k = Array.FindLastIndex(brakeEtas, e => e <= column.Eta);
                    if (k >= 0 && k < brakeEtas.Length - 1)
                    {
                        double ub = (column.Eta - brakeEtas[k]) / (brakeEtas[k + 1] - brakeEtas[k]);
                        double span = (brakeEtas[k + 1] - brakeEtas[k]) * _shape.HalfSpan;
                        double s = GliderShape.SmoothStep(0.88, 1, x);
                        double pull = _design.TrailingEdgePucker * span * Math.Sin(Math.PI * ub) * s;
                        double gather = 0.25 * pull * Math.Sin(2 * Math.PI * 4 * ub);
                        position += -frame.ChordDirection * (float)pull + n * (float)gather;
                    }
                }

                _final[j, i] = position;
            }
        });

        SagInletRims();
    }

    // The inlet rims hang into the cell between the ribs: the upper rim drops, the lower rises.
    private void SagInletRims()
    {
        if (_design.InletRimSag <= 0) return;
        int a = _inletStartRow, b = _inletEndRow;
        if (a < 0 || b <= a) return;
        double[] falloff = [1, 0.45, 0.15];
        for (int j = 0; j < _columns.Length; j++)
        {
            var column = _columns[j];
            if (!IsInletCell(column.Cell) || column.Rib >= 0) continue;
            var rimA = _final[j, a];
            var rimB = _final[j, b];
            var gap = rimB - rimA;
            float amount = (float)(_design.InletRimSag * 0.5 * Math.Sin(Math.PI * column.U));
            for (int k = 0; k < falloff.Length; k++)
            {
                if (a - k >= 0) _final[j, a - k] += gap * amount * (float)falloff[k];
                if (b + k < _rows.Length) _final[j, b + k] -= gap * amount * (float)falloff[k];
            }
        }
    }

    private Vector3 BaseNormal(int j, int i)
    {
        int columns = _columns.Length, rows = _rows.Length;
        var dSpan = _base[Math.Min(j + 1, columns - 1), i] - _base[Math.Max(j - 1, 0), i];
        var dRow = _base[j, Math.Min(i + 1, rows - 1)] - _base[j, Math.Max(i - 1, 0)];
        var n = Vector3.Cross(dRow, dSpan);
        float length = n.Length();
        if (length < 1e-12f) return Vector3.UnitY;
        n /= length;
        // Outward: away from the inside of the profile.
        var inside = _shape.CamberPoint(_columns[j].Eta, 0.35);
        if (Vector3.Dot(n, _base[j, i] - inside) < 0) n = -n;
        return n;
    }

    // The height of a circular arc with the given chord (width) and sagitta at fraction u across it.
    private static double ArcHeight(double u, double width, double sagitta)
    {
        if (sagitta <= 1e-9 || width <= 1e-9) return 0;
        double radius = (width * width / 4 + sagitta * sagitta) / (2 * sagitta);
        double offset = (u - 0.5) * width;
        return Math.Sqrt(Math.Max(0, radius * radius - offset * offset)) - (radius - sagitta);
    }

    private bool IsInletCell(int cell) => cell >= ClosedTipCells && cell < _shape.CellCount - ClosedTipCells;

    #endregion

    #region Ribs and panels

    private void AddTipPanel(MeshPart part, int j, bool flip)
    {
        var frame = _shape.Frame(_columns[j].Eta);
        var outline = Enumerable.Range(0, _rows.Length - 1).ToList(); // the last row repeats the closed trailing edge
        var points2 = outline.Select(i => ToPlane(frame, _final[j, i])).ToList();
        var triangles = PolygonTriangulator.Triangulate(points2);
        var indices = outline.Select(i =>
        {
            double t = _rows[i];
            return part.AddVertex(_final[j, i], new Vector2((float)((_columns[j].Eta + 1) / 2), (float)((t + 1) / 2)),
                VertexBind.OnCanopy(_columns[j].Eta, GliderShape.ChordFraction(t), t < 0 ? 1 : 0));
        }).ToArray();
        AddTriangles(part, indices, triangles, flip ^ IsMirrored(frame));
    }

    private void AddRib(MeshPart part, int j, bool full)
    {
        var column = _columns[j];
        var frame = _shape.Frame(column.Eta);
        double miniStart = 1 - Math.Clamp(_design.MiniRibLength, 0.02, 0.5);
        var outline = new List<int>();
        for (int i = 0; i < _rows.Length - 1; i++)
        {
            if (full || GliderShape.ChordFraction(_rows[i]) >= miniStart - 1e-6) outline.Add(i);
        }
        if (outline.Count < 3) return;

        var points2 = outline.Select(i => ToPlane(frame, _final[j, i])).ToList();
        var holes = new List<IReadOnlyList<Vector2>>();
        var holeBinds = new List<List<(Vector3 Position, VertexBind Bind)>>();
        if (full && _design.CrossVentCount > 0 && _design.CrossVentRadius > 0)
        {
            for (int k = 0; k < _design.CrossVentCount; k++)
            {
                double x = 0.2 + 0.45 * (k + 0.5) / _design.CrossVentCount;
                double upper = _shape.ProfileHeight(column.Eta, GliderShape.ProfileParameter(x, true));
                double lower = _shape.ProfileHeight(column.Eta, GliderShape.ProfileParameter(x, false));
                double center = (upper + lower) / 2;
                double radius = _design.CrossVentRadius * (upper - lower);
                const int sides = 20;
                var ring = new List<Vector2>();
                var binds = new List<(Vector3, VertexBind)>();
                for (int s = 0; s < sides; s++)
                {
                    double a = 2 * Math.PI * s / sides;
                    double hx = x + radius * Math.Cos(a);
                    double hh = center + radius * Math.Sin(a);
                    var p3 = frame.Point(hx, hh);
                    ring.Add(ToPlane(frame, p3));
                    double h = (hh - lower) / Math.Max(1e-6, upper - lower);
                    binds.Add((p3, VertexBind.OnCanopy(column.Eta, hx, h)));
                }
                holes.Add(ring);
                holeBinds.Add(binds);
            }
        }

        var triangles = PolygonTriangulator.Triangulate(points2, holes);
        var indices = new List<uint>();
        foreach (int i in outline)
        {
            double t = _rows[i];
            var uv = new Vector2((float)GliderShape.ChordFraction(t), t < 0 ? 0f : 1f);
            indices.Add(part.AddVertex(_final[j, i], uv, VertexBind.OnCanopy(column.Eta, GliderShape.ChordFraction(t), t < 0 ? 1 : 0)));
        }
        foreach (var hole in holeBinds)
        {
            foreach (var (position, bind) in hole) indices.Add(part.AddVertex(position, new Vector2(bind.B, 0.5f), bind));
        }
        AddTriangles(part, indices.ToArray(), triangles, IsMirrored(frame));
    }

    // A V-rib from the lower surface of a tab rib up to the upper surface of its neighbor, between the first and last row.
    private void AddDiagonal(MeshPart part, int tabRib, int neighbor)
    {
        int jTab = Array.FindIndex(_columns, c => c.Rib == tabRib);
        int jNeighbor = Array.FindIndex(_columns, c => c.Rib == neighbor);
        if (jTab < 0 || jNeighbor < 0) return;
        var rows = _design.RowPositions;
        double x0 = Math.Max(0.05, rows[0] - 0.06), x1 = Math.Min(0.9, rows[^1] + 0.06);
        const int steps = 12;
        uint prevLower = 0, prevUpper = 0;
        for (int s = 0; s <= steps; s++)
        {
            double x = x0 + (x1 - x0) * s / steps;
            int lowerRow = NearestRow(GliderShape.ProfileParameter(x, upper: false));
            int upperRow = NearestRow(GliderShape.ProfileParameter(x, upper: true));
            uint lower = part.AddVertex(_final[jTab, lowerRow], new Vector2((float)x, 1), VertexBind.OnCanopy(_columns[jTab].Eta, x, 0));
            uint upper = part.AddVertex(_final[jNeighbor, upperRow], new Vector2((float)x, 0), VertexBind.OnCanopy(_columns[jNeighbor].Eta, x, 1));
            if (s > 0) part.AddQuad(prevLower, lower, upper, prevUpper);
            prevLower = lower;
            prevUpper = upper;
        }
    }

    private static Vector2 ToPlane(GliderShape.ProfileFrame frame, Vector3 p)
    {
        var d = p - frame.Pivot;
        return new Vector2(Vector3.Dot(d, frame.ChordDirection), Vector3.Dot(d, frame.Up));
    }

    // The plane coordinates (chord, up) are left-handed seen from +span on the right wing.
    private static bool IsMirrored(GliderShape.ProfileFrame frame) => frame.Span.X < 0;

    private static void AddTriangles(MeshPart part, IReadOnlyList<uint> indices, List<int> triangles, bool flip)
    {
        for (int k = 0; k < triangles.Count; k += 3)
        {
            uint a = indices[triangles[k]], b = indices[triangles[k + 1]], c = indices[triangles[k + 2]];
            if (flip) part.AddTriangle(a, c, b);
            else part.AddTriangle(a, b, c);
        }
    }

    #endregion
}

/// <summary>Smooth 1D value noise in [−1, 1], for irregular creases.</summary>
internal readonly struct SmoothNoise(int seed)
{
    public double Sample(double x)
    {
        double i = Math.Floor(x);
        double f = x - i;
        double a = Hash((long)i), b = Hash((long)i + 1);
        double s = f * f * (3 - 2 * f);
        return a + (b - a) * s;
    }

    private double Hash(long i)
    {
        unchecked
        {
            ulong h = ((ulong)i * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)seed * 0xBF58476D1CE4E5B9UL);
            h ^= h >> 31;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 29;
            return (h & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1;
        }
    }
}
