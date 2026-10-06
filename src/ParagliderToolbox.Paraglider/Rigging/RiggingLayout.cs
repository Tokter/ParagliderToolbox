using System.Numerics;
using ParagliderToolbox.Paraglider.Geometry;

namespace ParagliderToolbox.Paraglider.Rigging;

/// <summary>The kind of a rigging point.</summary>
public enum RigPointKind
{
    /// <summary>A line tab on the lower surface of a rib.</summary>
    Tab,
    /// <summary>A brake line tab on the trailing edge.</summary>
    BrakeTab,
    /// <summary>Where lines join (a cascade knot).</summary>
    Knot,
    /// <summary>The top of a riser (its maillon), where the main lines attach.</summary>
    RiserTop,
    /// <summary>A carabiner, where the risers meet the harness.</summary>
    Carabiner,
    /// <summary>The brake pulley on the rear riser.</summary>
    Pulley,
    /// <summary>The brake toggle.</summary>
    Toggle,
}

/// <summary>The level of a line in the cascade.</summary>
public enum LineLevel
{
    Upper,
    Middle,
    Main,
    BrakeUpper,
    BrakeMiddle,
    BrakeMain,
    /// <summary>The short brake line from the pulley down to the toggle.</summary>
    BrakeHandle,
    /// <summary>A riser strap.</summary>
    Riser,
}

/// <summary>A point of the rigging.</summary>
/// <param name="Id">The index in <see cref="RiggingLayout.Points"/>.</param>
/// <param name="Kind">What the point is.</param>
/// <param name="Position">The position in flight attitude (m).</param>
/// <param name="Side">+1 left, −1 right.</param>
/// <param name="Row">The line row (0 = A), <see cref="RiggingLayout.BrakeRow"/> for brakes, −1 for none.</param>
/// <param name="Eta">For tabs: the span position.</param>
/// <param name="Chord">For tabs: the chord fraction.</param>
/// <param name="Rib">For tabs: the rib index.</param>
public sealed record RigPoint(int Id, RigPointKind Kind, Vector3 Position, int Side, int Row, double Eta = 0, double Chord = 0, int Rib = -1);

/// <summary>A line (or riser) between two rigging points; <see cref="Upper"/> is the end toward the canopy.</summary>
public sealed record RigLine(int Upper, int Lower, LineLevel Level, int Row, int Side, double Diameter, string Name)
{
    /// <summary>Gets the length (m) in the layout.</summary>
    public double Length { get; init; }
}

/// <summary>
/// The line plan of a design: tabs, cascade knots, risers, brakes and their lines, in flight attitude.
/// </summary>
/// <remarks>
/// Line tabs sit on the lower surface of every <see cref="Design.GliderDesign.TabRibInterval"/>-th rib, counted from the
/// tips, at the chord positions of the rows. Per side and row, <see cref="Design.GliderDesign.UpperLinesPerMiddle"/>
/// neighboring tabs join into a middle line, and <see cref="Design.GliderDesign.MiddleLinesPerMain"/> middle lines into
/// a main line on the row's riser. Knots lie on the way from their tabs' center to the riser, at the cascade heights.
/// Lines are straight, as they are under load; their lengths are the layout's distances.
/// </remarks>
public sealed class RiggingLayout
{
    /// <summary>The row number of the brakes.</summary>
    public const int BrakeRow = 9;

    /// <summary>Gets the rigging points.</summary>
    public List<RigPoint> Points { get; } = [];

    /// <summary>Gets the lines and risers.</summary>
    public List<RigLine> Lines { get; } = [];

    /// <summary>Gets the indices of the ribs with line tabs.</summary>
    public List<int> TabRibs { get; } = [];

    /// <summary>Gets the indices of the ribs with brake tabs.</summary>
    public List<int> BrakeRibs { get; } = [];

    /// <summary>Gets the carabiner point of a side (+1 left, −1 right).</summary>
    public RigPoint Carabiner(int side) => Points.First(p => p.Kind == RigPointKind.Carabiner && p.Side == side);

    /// <summary>Gets the riser top of a row and side.</summary>
    public RigPoint RiserTop(int row, int side) => Points.First(p => p.Kind == RigPointKind.RiserTop && p.Side == side && p.Row == row);

    /// <summary>Gets the toggle of a side.</summary>
    public RigPoint Toggle(int side) => Points.First(p => p.Kind == RigPointKind.Toggle && p.Side == side);

    /// <summary>Builds the line plan of <paramref name="shape"/>.</summary>
    public static RiggingLayout Build(GliderShape shape)
    {
        var layout = new RiggingLayout();
        var design = shape.Design;
        var ribs = shape.RibPositions;
        int cells = shape.CellCount;
        var rows = design.RowPositions;

        // Ribs with tabs, counted from each tip toward the center (never the center rib, which belongs to neither side).
        int interval = Math.Max(1, design.TabRibInterval);
        int brakeInterval = Math.Max(1, design.BrakeTabInterval);
        for (int r = 0; r <= cells; r++)
        {
            int fromTip = Math.Min(r, cells - r);
            if (Math.Abs(ribs[r]) < 1e-9) continue;
            if (fromTip % interval == 0) layout.TabRibs.Add(r);
            if (fromTip % brakeInterval == 0 && Math.Abs(ribs[r]) > 0.08) layout.BrakeRibs.Add(r);
        }

        foreach (int side in new[] { 1, -1 })
        {
            // Risers: from the carabiner toward the lines' center, the rows side by side front to back.
            var carabinerPos = new Vector3(side * (float)design.CarabinerSpacing / 2, 0, 0);
            var carabiner = layout.AddPoint(RigPointKind.Carabiner, carabinerPos, side, -1);

            var sideTabRibs = layout.TabRibs.Where(r => Math.Sign(ribs[r]) == side).OrderBy(r => Math.Abs(ribs[r])).ToList();
            var center = Vector3.Zero;
            foreach (int r in sideTabRibs) center += shape.SurfacePoint(ribs[r], GliderShape.ProfileParameter(rows[rows.Length / 2], upper: false));
            center /= Math.Max(1, sideTabRibs.Count);
            var riserDir = Vector3.Normalize(center - carabinerPos);
            float riserLength = (float)design.RiserLength;
            float spread = 0.025f;

            var riserTops = new RigPoint[rows.Length];
            for (int row = 0; row < rows.Length; row++)
            {
                float offset = spread * ((rows.Length - 1) / 2f - row);
                var top = carabinerPos + riserDir * riserLength + new Vector3(0, 0, offset);
                riserTops[row] = layout.AddPoint(RigPointKind.RiserTop, top, side, row);
                layout.AddLine(riserTops[row].Id, carabiner.Id, LineLevel.Riser, row, side, 25, $"Riser {RowName(row)}");
            }

            // Line cascades per row.
            for (int row = 0; row < rows.Length; row++)
            {
                var tabs = sideTabRibs
                    .Select(r => layout.AddPoint(RigPointKind.Tab, shape.SurfacePoint(ribs[r], GliderShape.ProfileParameter(rows[row], upper: false)), side, row, ribs[r], rows[row], r))
                    .ToList();
                layout.AddCascade(tabs, riserTops[row], row, side, design.UpperLinesPerMiddle, design.MiddleLinesPerMain,
                    design.UpperCascadeHeight, design.MiddleCascadeHeight, design, RowName(row), brake: false);
            }

            // Brakes: through a pulley on the rear riser down to the toggle.
            var pulleyPos = carabinerPos + riserDir * (riserLength * 0.72f) + new Vector3(0, 0, -0.05f);
            var pulley = layout.AddPoint(RigPointKind.Pulley, pulleyPos, side, BrakeRow);
            var toggle = layout.AddPoint(RigPointKind.Toggle, pulleyPos - riserDir * 0.16f + new Vector3(0, 0, -0.02f), side, BrakeRow);
            var brakeTabs = layout.BrakeRibs
                .Where(r => Math.Sign(ribs[r]) == side)
                .OrderBy(r => Math.Abs(ribs[r]))
                .Select(r => layout.AddPoint(RigPointKind.BrakeTab, shape.SurfacePoint(ribs[r], 1), side, BrakeRow, ribs[r], 1, r))
                .ToList();
            if (brakeTabs.Count > 0)
            {
                layout.AddCascade(brakeTabs, pulley, BrakeRow, side, design.UpperLinesPerMiddle + 1, 3, 0.22, 0.5, design, "Brake", brake: true);
            }
            layout.AddLine(pulley.Id, toggle.Id, LineLevel.BrakeHandle, BrakeRow, side, design.MainLineDiameter * 1.4, "Brake handle");
        }
        return layout;
    }

    /// <summary>Gets the letter of a row (A, B, C, D) or "Brake".</summary>
    public static string RowName(int row) => row == BrakeRow ? "Brake" : ((char)('A' + row)).ToString();

    /// <summary>Gets the total length of all lines of a level (m), e.g. for ordering line material.</summary>
    public double TotalLength(LineLevel level) => Lines.Where(l => l.Level == level).Sum(l => l.Length);

    private RigPoint AddPoint(RigPointKind kind, Vector3 position, int side, int row, double eta = 0, double chord = 0, int rib = -1)
    {
        var point = new RigPoint(Points.Count, kind, position, side, row, eta, chord, rib);
        Points.Add(point);
        return point;
    }

    private void AddLine(int upper, int lower, LineLevel level, int row, int side, double diameterMm, string name)
    {
        double length = Vector3.Distance(Points[upper].Position, Points[lower].Position);
        Lines.Add(new RigLine(upper, lower, level, row, side, diameterMm, name) { Length = length });
    }

    private void AddCascade(List<RigPoint> tabs, RigPoint anchor, int row, int side, int perMiddle, int perMain,
        double upperHeight, double middleHeight, Design.GliderDesign design, string rowName, bool brake)
    {
        perMiddle = Math.Max(1, perMiddle);
        perMain = Math.Max(1, perMain);
        var upperLevel = brake ? LineLevel.BrakeUpper : LineLevel.Upper;
        var middleLevel = brake ? LineLevel.BrakeMiddle : LineLevel.Middle;
        var mainLevel = brake ? LineLevel.BrakeMain : LineLevel.Main;

        var middleGroups = Chunk(tabs, perMiddle);
        var middleKnots = new List<(RigPoint Knot, List<RigPoint> Tabs)>();
        int lineNumber = 1;
        foreach (var group in middleGroups)
        {
            var knot = AddPoint(RigPointKind.Knot, Toward(group, anchor, upperHeight), side, row);
            foreach (var tab in group)
            {
                AddLine(tab.Id, knot.Id, upperLevel, row, side, design.UpperLineDiameter, $"{rowName} upper {side switch { 1 => "L", _ => "R" }}{lineNumber++}");
            }
            middleKnots.Add((knot, group));
        }

        int mainNumber = 1, middleNumber = 1;
        foreach (var group in Chunk(middleKnots, perMain))
        {
            var allTabs = group.SelectMany(g => g.Tabs).ToList();
            var mainKnot = AddPoint(RigPointKind.Knot, Toward(allTabs, anchor, middleHeight), side, row);
            foreach (var (knot, _) in group)
            {
                AddLine(knot.Id, mainKnot.Id, middleLevel, row, side, design.MiddleLineDiameter, $"{rowName} middle {(side == 1 ? "L" : "R")}{middleNumber++}");
            }
            AddLine(mainKnot.Id, anchor.Id, mainLevel, row, side, design.MainLineDiameter, $"{rowName} main {(side == 1 ? "L" : "R")}{mainNumber++}");
        }
    }

    private static Vector3 Toward(List<RigPoint> tabs, RigPoint anchor, double fraction)
    {
        var center = Vector3.Zero;
        foreach (var t in tabs) center += t.Position;
        center /= tabs.Count;
        return Vector3.Lerp(center, anchor.Position, (float)Math.Clamp(fraction, 0, 1));
    }

    private static List<List<T>> Chunk<T>(List<T> items, int size)
    {
        var groups = new List<List<T>>();
        for (int i = 0; i < items.Count; i += size) groups.Add(items.GetRange(i, Math.Min(size, items.Count - i)));
        // A lone item at the end joins the group before it, so no middle line carries a single upper line.
        if (groups.Count > 1 && groups[^1].Count == 1 && size > 1)
        {
            groups[^2].AddRange(groups[^1]);
            groups.RemoveAt(groups.Count - 1);
        }
        return groups;
    }
}
