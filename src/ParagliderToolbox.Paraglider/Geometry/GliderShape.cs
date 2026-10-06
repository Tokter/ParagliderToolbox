using System.Numerics;
using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Paraglider.Geometry;

/// <summary>
/// The shape of a design: the smooth canopy surface without ballooning, as a function of the span position and the
/// position around the profile, plus the cell layout and the flight attitude.
/// </summary>
/// <remarks>
/// <para>
/// Coordinates follow glTF: meters, +Y up, +Z forward (the flight direction), +X to the pilot's left. The carabiners'
/// midpoint is at the origin; the canopy flies above, pitched to its trim attitude.
/// </para>
/// <para>
/// Span positions <c>η</c> run from −1 (right tip) over 0 (center) to +1 (left tip) along the flat span. The planform
/// gives the chord and the leading edge position at <c>η</c>. The arc bends the flat span into the front view: the roll
/// angle θ(η) of the ribs is integrated along the span, so the arc's length equals the flat span and every rib stands
/// perpendicular to the arc. Each rib is twisted (washout) about <see cref="GliderDesign.TwistAxis"/>.
/// </para>
/// <para>
/// Positions around the profile use <c>t</c> from −1 (upper trailing edge) over 0 (leading edge) to +1 (lower
/// trailing edge); the chord fraction is <c>x = 1 − cos(π|t|/2)</c>, which samples the nose densely.
/// </para>
/// </remarks>
public sealed class GliderShape
{
    private const int ArcSamples = 1024;

    private readonly double[] _arcLateral = new double[ArcSamples + 1];
    private readonly double[] _arcVertical = new double[ArcSamples + 1];
    private readonly double _tipAngle;
    private readonly Matrix4x4 _attitude;

    /// <summary>Builds the shape of <paramref name="design"/>.</summary>
    public GliderShape(GliderDesign design)
    {
        Design = design;
        Airfoil = Airfoil.FromDesign(design);
        HalfSpan = design.FlatSpan / 2;
        RootChord = design.FlatArea / (design.FlatSpan * design.ChordDistribution.Integrate());

        _tipAngle = design.ArcMode == ArcMode.TipAngle ? DegToRad(design.TipArcAngle) : SolveTipAngle(design.ProjectedSpanRatio);
        BuildArc(_tipAngle);

        RibPositions = LayoutRibs();
        _attitude = ComputeAttitude();
    }

    /// <summary>Gets the design.</summary>
    public GliderDesign Design { get; }

    /// <summary>Gets the profile.</summary>
    public Airfoil Airfoil { get; }

    /// <summary>Gets half the flat span (m).</summary>
    public double HalfSpan { get; }

    /// <summary>Gets the chord at the center (m).</summary>
    public double RootChord { get; }

    /// <summary>Gets the roll angle of the tip rib (radians).</summary>
    public double TipArcAngle => _tipAngle;

    /// <summary>Gets the span positions η of the ribs, from the right tip (−1) to the left tip (+1); <c>CellCount + 1</c> of them.</summary>
    public double[] RibPositions { get; }

    /// <summary>Gets the number of cells.</summary>
    public int CellCount => RibPositions.Length - 1;

    /// <summary>Gets the projected span (m): the distance between the tips in the front view.</summary>
    public double ProjectedSpan => 2 * ArcLateral(1);

    /// <summary>Gets the projected area (m²): the canopy seen from above (the arc's horizontal extent times the chord).</summary>
    public double ProjectedArea
    {
        get
        {
            const int n = 400;
            double area = 0;
            for (int i = 0; i < n; i++)
            {
                double e0 = i / (double)n, e1 = (i + 1) / (double)n;
                area += (ArcLateral(e1) - ArcLateral(e0)) * Chord((e0 + e1) / 2);
            }
            return 2 * area;
        }
    }

    /// <summary>Gets the projected aspect ratio.</summary>
    public double ProjectedAspectRatio => ProjectedSpan * ProjectedSpan / ProjectedArea;

    /// <summary>Gets the transform from the canopy frame (center leading edge at the origin, chord along −Z) to flight attitude.</summary>
    public Matrix4x4 Attitude => _attitude;

    #region Planform, arc and profile distributions

    /// <summary>Gets the chord (m) at span position <paramref name="eta"/>.</summary>
    public double Chord(double eta) => RootChord * Design.ChordDistribution.Evaluate(Math.Abs(eta));

    /// <summary>Gets how far the leading edge lies behind the center leading edge (m), in the flat planform.</summary>
    public double LeadingEdgeOffset(double eta)
    {
        double e = Math.Abs(eta);
        return Design.StraightChordLine * (RootChord - Chord(e)) + Design.LeadingEdgeSweep.Evaluate(e) * RootChord;
    }

    /// <summary>Gets the profile thickness (fraction of chord) at <paramref name="eta"/>.</summary>
    public double Thickness(double eta) =>
        Design.RootThickness + (Design.TipThickness - Design.RootThickness) * Design.ThicknessDistribution.Evaluate(Math.Abs(eta));

    /// <summary>Gets the washout (radians, positive nose down) at <paramref name="eta"/>.</summary>
    public double Washout(double eta) => DegToRad(Design.TipWashout * Design.WashoutDistribution.Evaluate(Math.Abs(eta)));

    /// <summary>Gets the roll angle of the arc (radians) at <paramref name="eta"/> ≥ 0, without the tip cant.</summary>
    public double ArcAngle(double eta) => _tipAngle * Design.ArcDistribution.Evaluate(Math.Abs(eta));

    /// <summary>Gets the roll angle of the ribs (radians) at <paramref name="eta"/> ≥ 0: the arc angle plus the tip cant.</summary>
    public double RibRoll(double eta)
    {
        double e = Math.Abs(eta);
        double cantStart = 1 - Math.Clamp(Design.TipCantSpan, 0.01, 1);
        double blend = SmoothStep(cantStart, 1, e);
        return ArcAngle(e) + DegToRad(Design.TipCant) * blend;
    }

    /// <summary>Gets the horizontal distance of the arc from the center (m) at <paramref name="eta"/> ≥ 0.</summary>
    public double ArcLateral(double eta) => Interpolate(_arcLateral, Math.Abs(eta));

    /// <summary>Gets the drop of the arc below the center (m, negative) at <paramref name="eta"/> ≥ 0.</summary>
    public double ArcVertical(double eta) => Interpolate(_arcVertical, Math.Abs(eta));

    /// <summary>Gets the chord fraction of profile position <paramref name="t"/> (−1 upper trailing edge … +1 lower trailing edge).</summary>
    public static double ChordFraction(double t) => 1 - Math.Cos(Math.PI * Math.Abs(t) / 2);

    /// <summary>Gets the profile position on the upper (<paramref name="upper"/>) or lower surface at chord fraction <paramref name="x"/>.</summary>
    public static double ProfileParameter(double x, bool upper)
    {
        double t = 2 / Math.PI * Math.Acos(1 - Math.Clamp(x, 0, 1));
        return upper ? -t : t;
    }

    /// <summary>
    /// Gets the profile height (fraction of chord) at profile position <paramref name="t"/> and span position
    /// <paramref name="eta"/>, with the shark nose applied to the lower surface.
    /// </summary>
    public double ProfileHeight(double eta, double t)
    {
        double x = ChordFraction(t);
        double thickness = Thickness(eta);
        if (t <= 0) return Airfoil.Upper(x, thickness);
        return Airfoil.Lower(x, thickness) + SharknoseLift(x, thickness);
    }

    // The shark nose: the lower surface around the inlet is pulled toward the camber line, forming a concave scoop.
    private double SharknoseLift(double x, double thickness)
    {
        double depth = Design.SharknoseDepth;
        if (depth <= 0) return 0;
        double start = Math.Max(0.004, Design.InletStart);
        double end = Design.InletEnd + 0.1;
        if (x <= start || x >= end) return 0;
        double u = (x - start) / (end - start);
        // Rises quickly behind the nose, peaks just behind the inlet, fades back into the profile.
        double bump = Math.Pow(Math.Sin(Math.PI * Math.Pow(u, 0.7)), 2);
        return depth * thickness * 2 * Airfoil.HalfThickness(x) * bump;
    }

    #endregion

    #region Frames and points

    /// <summary>The frame of the profile at a span position, in flight attitude.</summary>
    /// <param name="Pivot">The point on the chord line at <see cref="GliderDesign.TwistAxis"/>.</param>
    /// <param name="ChordDirection">Unit vector from the leading edge to the trailing edge.</param>
    /// <param name="Up">Unit vector of the profile's height axis.</param>
    /// <param name="Chord">The chord (m).</param>
    /// <param name="Axis">The twist axis chord fraction.</param>
    public readonly record struct ProfileFrame(Vector3 Pivot, Vector3 ChordDirection, Vector3 Up, float Chord, float Axis)
    {
        /// <summary>Gets the point at chord fraction <paramref name="x"/> and height <paramref name="height"/> (fractions of chord).</summary>
        public Vector3 Point(double x, double height) =>
            Pivot + ChordDirection * (float)((x - Axis) * Chord) + Up * (float)(height * Chord);

        /// <summary>Gets the direction toward the pilot's left (+X at the center), perpendicular to the profile plane.</summary>
        public Vector3 Span => Vector3.Normalize(Vector3.Cross(ChordDirection, Up));
    }

    /// <summary>Gets the profile frame at span position <paramref name="eta"/>, in flight attitude.</summary>
    public ProfileFrame Frame(double eta)
    {
        double e = Math.Abs(eta);
        double side = eta < 0 ? -1 : 1;
        double roll = RibRoll(e);
        double twist = Washout(e);
        double chord = Chord(e);
        double axis = Design.TwistAxis;

        var back = new Vector3(0, 0, -1);
        var up = new Vector3((float)(side * Math.Sin(roll)), (float)Math.Cos(roll), 0);
        // Nose-down washout lifts the trailing edge toward the rib's up axis.
        var chordDir = back * (float)Math.Cos(twist) + up * (float)Math.Sin(twist);
        var profileUp = up * (float)Math.Cos(twist) - back * (float)Math.Sin(twist);

        var arcPoint = new Vector3((float)(side * ArcLateral(e)), (float)ArcVertical(e), 0);
        var pivot = arcPoint + back * (float)(LeadingEdgeOffset(e) + axis * chord);

        return new ProfileFrame(
            Vector3.Transform(pivot, _attitude),
            Vector3.Normalize(Vector3.TransformNormal(chordDir, _attitude)),
            Vector3.Normalize(Vector3.TransformNormal(profileUp, _attitude)),
            (float)chord,
            (float)axis);
    }

    /// <summary>Gets the point of the smooth canopy surface at span position <paramref name="eta"/> and profile position <paramref name="t"/>.</summary>
    public Vector3 SurfacePoint(double eta, double t) => Frame(eta).Point(ChordFraction(t), ProfileHeight(eta, t));

    /// <summary>Gets the point on the camber line at span position <paramref name="eta"/> and chord fraction <paramref name="x"/>.</summary>
    public Vector3 CamberPoint(double eta, double x) => Frame(eta).Point(x, Airfoil.Camber(x));

    #endregion

    #region Layout

    // Cell widths blend between equal and proportional to the chord.
    private double[] LayoutRibs()
    {
        int cells = Math.Max(2, Design.CellCount);
        double k = Math.Clamp(Design.CellWidthChordFactor, 0, 1);
        const int n = 4000;
        var cumulative = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            double eta = -1 + 2 * (i + 0.5) / n;
            double width = (1 - k) + k * Design.ChordDistribution.Evaluate(Math.Abs(eta));
            cumulative[i + 1] = cumulative[i] + 1 / Math.Max(width, 0.05);
        }
        var ribs = new double[cells + 1];
        int j = 0;
        for (int r = 0; r <= cells; r++)
        {
            double target = cumulative[n] * r / cells;
            while (j < n - 1 && cumulative[j + 1] < target) j++;
            double span = cumulative[j + 1] - cumulative[j];
            double f = span > 0 ? (target - cumulative[j]) / span : 0;
            ribs[r] = -1 + 2 * (j + f) / n;
        }
        ribs[0] = -1;
        ribs[cells] = 1;
        // Exact symmetry.
        for (int r = 0; r <= cells / 2; r++)
        {
            double v = (ribs[cells - r] - ribs[r]) / 2;
            ribs[r] = -v;
            ribs[cells - r] = v;
        }
        if (cells % 2 == 0) ribs[cells / 2] = 0;
        return ribs;
    }

    private void BuildArc(double tipAngle)
    {
        _arcLateral[0] = 0;
        _arcVertical[0] = 0;
        double ds = HalfSpan / ArcSamples;
        for (int i = 0; i < ArcSamples; i++)
        {
            double theta = tipAngle * Design.ArcDistribution.Evaluate((i + 0.5) / ArcSamples);
            _arcLateral[i + 1] = _arcLateral[i] + Math.Cos(theta) * ds;
            _arcVertical[i + 1] = _arcVertical[i] - Math.Sin(theta) * ds;
        }
    }

    private double SolveTipAngle(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.4, 0.999);
        double lo = 0, hi = Math.PI * 0.95;
        for (int i = 0; i < 50; i++)
        {
            double mid = (lo + hi) / 2;
            BuildArc(mid);
            double projected = _arcLateral[ArcSamples] / HalfSpan;
            if (projected > ratio) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    // Pitches the canopy to its trim attitude and puts the carabiners at the origin.
    private Matrix4x4 ComputeAttitude()
    {
        double glideAngle = Math.Atan(1 / Math.Max(1, Design.TrimGlideRatio));
        double pitch = DegToRad(Design.TrimAngleOfAttack) - glideAngle; // nose up above the horizon
        float c = (float)Math.Cos(pitch), s = (float)Math.Sin(pitch);
        // Nose up: +Z rotates toward +Y.
        var rotation = new Matrix4x4(
            1, 0, 0, 0,
            0, c, -s, 0,
            0, s, c, 0,
            0, 0, 0, 1);
        // The point of the center chord the carabiners hang below.
        var hangPoint = new Vector3(0, 0, (float)(-Design.CarabinerChordPosition * RootChord));
        var hangInFlight = Vector3.Transform(hangPoint, rotation);
        var carabiners = hangInFlight - new Vector3(0, (float)Design.CarabinerHeight, 0);
        return rotation * Matrix4x4.CreateTranslation(-carabiners);
    }

    #endregion

    internal static double DegToRad(double degrees) => degrees * Math.PI / 180;

    internal static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Interpolate(double[] table, double eta)
    {
        double p = Math.Clamp(eta, 0, 1) * ArcSamples;
        int i = Math.Min((int)p, ArcSamples - 1);
        double f = p - i;
        return table[i] + (table[i + 1] - table[i]) * f;
    }
}
