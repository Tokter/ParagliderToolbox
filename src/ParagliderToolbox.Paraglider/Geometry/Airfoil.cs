using System.Globalization;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Paraglider.Geometry;

/// <summary>
/// A normalized airfoil: a camber line and a thickness distribution over the chord (x from 0 at the leading edge to 1 at
/// the trailing edge), so the thickness can be scaled per rib while the camber stays.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HalfThickness"/> is normalized to a profile of thickness 1 (its maximum is 0.5); a profile of thickness
/// <c>t</c> has the upper surface at <c>Camber(x) + t·HalfThickness(x)</c> and the lower at <c>Camber(x) − t·HalfThickness(x)</c>.
/// </para>
/// <para>
/// The parametric profile uses the NACA modified four-digit thickness distribution (adjustable leading edge radius and
/// position of the maximum thickness; closed trailing edge) on a four-digit camber line with a reflexed tail. Paraglider
/// sections are typically 14–20% thick with the maximum thickness at 20–25% chord, a blunt nose, 2–4% camber and a
/// little reflex for pitch stability.
/// </para>
/// </remarks>
public sealed class Airfoil
{
    private readonly Func<double, double> _camber;
    private readonly Func<double, double> _halfThickness;

    private Airfoil(string name, Func<double, double> camber, Func<double, double> halfThickness)
    {
        Name = name;
        _camber = camber;
        _halfThickness = halfThickness;
    }

    /// <summary>Gets the name of the profile.</summary>
    public string Name { get; }

    /// <summary>Gets the camber line height at chord position <paramref name="x"/> (fraction of chord).</summary>
    public double Camber(double x) => _camber(Math.Clamp(x, 0, 1));

    /// <summary>Gets the half thickness at <paramref name="x"/> for a profile of thickness 1.</summary>
    public double HalfThickness(double x) => Math.Max(0, _halfThickness(Math.Clamp(x, 0, 1)));

    /// <summary>Gets the upper surface height at <paramref name="x"/> for a profile <paramref name="thickness"/> thick.</summary>
    public double Upper(double x, double thickness) => Camber(x) + thickness * HalfThickness(x);

    /// <summary>Gets the lower surface height at <paramref name="x"/> for a profile <paramref name="thickness"/> thick.</summary>
    public double Lower(double x, double thickness) => Camber(x) - thickness * HalfThickness(x);

    /// <summary>Creates the profile a design describes: parametric, or its custom coordinates.</summary>
    /// <exception cref="FormatException">The custom coordinates can't be read.</exception>
    public static Airfoil FromDesign(GliderDesign design) =>
        design.AirfoilSource == AirfoilSource.Custom && !string.IsNullOrWhiteSpace(design.CustomAirfoil)
            ? FromDat(design.CustomAirfoil)
            : Parametric(design.MaxThicknessPosition, design.LeadingEdgeRadiusIndex, design.Camber, design.CamberPosition, design.Reflex);

    /// <summary>Creates a parametric reflexed section.</summary>
    /// <param name="maxThicknessPosition">Chord position of the maximum thickness (0.15–0.6).</param>
    /// <param name="leadingEdgeRadiusIndex">NACA leading edge radius index (0 sharp, 6 normal, 9 very blunt).</param>
    /// <param name="camber">Maximum camber (fraction of chord).</param>
    /// <param name="camberPosition">Chord position of the maximum camber.</param>
    /// <param name="reflex">Height the rear camber line rises by (fraction of chord).</param>
    public static Airfoil Parametric(double maxThicknessPosition, double leadingEdgeRadiusIndex, double camber, double camberPosition, double reflex)
    {
        var thickness = ModifiedFourDigitThickness(Math.Clamp(maxThicknessPosition, 0.15, 0.6), Math.Clamp(leadingEdgeRadiusIndex, 0.5, 9));
        double m = camber, p = Math.Clamp(camberPosition, 0.1, 0.7);
        const double reflexStart = 0.55;
        double CamberLine(double x)
        {
            double y = x < p
                ? m / (p * p) * (2 * p * x - x * x)
                : m / ((1 - p) * (1 - p)) * (1 - 2 * p + 2 * p * x - x * x);
            if (x > reflexStart)
            {
                // A dip that comes back up to the trailing edge: the tail of the camber line curves upward.
                double u = (x - reflexStart) / (1 - reflexStart);
                y -= reflex * 6.75 * u * u * (1 - u);
            }
            return y;
        }
        string name = string.Create(CultureInfo.InvariantCulture, $"Parametric (camber {camber:P1} at {p:P0}, reflex {reflex:P1})");
        return new Airfoil(name, CamberLine, thickness);
    }

    /// <summary>
    /// Reads Selig .dat coordinates: an optional name line, then x y pairs from the trailing edge over the upper surface
    /// to the leading edge and back along the lower surface. Lednicer files (upper and lower listed separately from the
    /// leading edge) are read too. The profile is normalized to chord 1; its thickness is scaled per rib later.
    /// </summary>
    /// <exception cref="FormatException">The text has too few coordinates.</exception>
    public static Airfoil FromDat(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string name = "Custom";
        var points = new List<(double X, double Y)>();
        foreach (string line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                // Lednicer headers ("61. 61.") give point counts, not coordinates.
                if (x > 1.5 && y > 1.5) continue;
                points.Add((x, y));
            }
            else if (points.Count == 0)
            {
                name = line;
            }
        }
        if (points.Count < 8) throw new FormatException("An airfoil needs at least 8 coordinates (x y per line).");

        // Lednicer: two runs that both start at the leading edge. Selig: one run through the leading edge.
        int split = points.FindIndex(1, p => p.X < 1e-9 || p.X <= points.Min(q => q.X));
        List<(double X, double Y)> upper, lower;
        int restart = Enumerable.Range(1, points.Count - 1).FirstOrDefault(i => points[i].X < points[i - 1].X - 0.5);
        if (restart > 0)
        {
            upper = points.Take(restart).ToList();
            lower = points.Skip(restart).ToList();
        }
        else
        {
            upper = points.Take(split + 1).Reverse().ToList();
            lower = points.Skip(split).ToList();
        }

        // Normalize to chord 1 with the leading edge at the origin.
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double chord = maxX - minX;
        if (chord <= 0) throw new FormatException("The coordinates have no chord.");
        var le = points.MinBy(p => p.X);
        Func<(double X, double Y), CurvePoint> normalize = p => new CurvePoint((p.X - minX) / chord, (p.Y - le.Y) / chord);
        var upperCurve = Resample(upper.Select(normalize));
        var lowerCurve = Resample(lower.Select(normalize));
        if (upperCurve.Integrate() < lowerCurve.Integrate()) (upperCurve, lowerCurve) = (lowerCurve, upperCurve);

        // Camber and thickness on a dense cosine grid, the thickness normalized to 1.
        const int samples = 241;
        var camber = new CurvePoint[samples];
        var half = new CurvePoint[samples];
        double maxThickness = 0;
        for (int i = 0; i < samples; i++)
        {
            double x = 0.5 * (1 - Math.Cos(Math.PI * i / (samples - 1)));
            double yu = upperCurve.Evaluate(x), yl = lowerCurve.Evaluate(x);
            camber[i] = new CurvePoint(x, (yu + yl) / 2);
            half[i] = new CurvePoint(x, (yu - yl) / 2);
            maxThickness = Math.Max(maxThickness, yu - yl);
        }
        if (maxThickness <= 0) throw new FormatException("The profile has no thickness.");
        var camberCurve = new Curve(camber);
        var halfCurve = new Curve(half.Select(p => p with { Y = p.Y / maxThickness }).ToArray());
        // Near the nose the half thickness grows like √x; interpolate in √x so the leading edge stays round.
        double nose = half[1].X;
        double noseValue = halfCurve.Evaluate(nose);
        return new Airfoil(name, camberCurve.Evaluate, x => x < nose ? noseValue * Math.Sqrt(x / nose) : halfCurve.Evaluate(x));
    }

    private static Curve Resample(IEnumerable<CurvePoint> points)
    {
        // Duplicate x values (a vertical nose) would break the spline: keep the first.
        var list = new List<CurvePoint>();
        foreach (var p in points.OrderBy(p => p.X))
        {
            if (list.Count == 0 || p.X - list[^1].X > 1e-7) list.Add(p);
        }
        return new Curve(list);
    }

    // NACA modified four-digit thickness (Abbott & von Doenhoff), normalized to thickness 1, with a closed trailing edge.
    private static Func<double, double> ModifiedFourDigitThickness(double m, double radiusIndex)
    {
        // Trailing edge slope by position of maximum thickness.
        double[] positions = [0.2, 0.3, 0.4, 0.5, 0.6];
        double[] slopes = [0.200, 0.234, 0.315, 0.465, 0.700];
        double d1 = new Curve(positions.Zip(slopes, (x, y) => new CurvePoint(x, y)).ToArray()).Evaluate(m);
        double l = 1 - m;
        const double d0 = 0;
        double d3 = (d1 * l - 0.2 + 2 * d0) / (l * l * l);
        double d2 = (0.1 - d0 - d1 * l - d3 * l * l * l) / (l * l);
        double curvatureAtMax = 2 * d2 + 6 * d3 * l;

        double a0 = 0.296904 * radiusIndex / 6;
        // a1, a2, a3 from: y(m) = 0.1, y'(m) = 0, y''(m) matches the aft part.
        double sm = Math.Sqrt(m);
        double[,] a =
        {
            { m, m * m, m * m * m },
            { 1, 2 * m, 3 * m * m },
            { 0, 2, 6 * m },
        };
        double[] b = [0.1 - a0 * sm, -a0 / (2 * sm), curvatureAtMax + a0 / (4 * m * sm)];
        var (a1, a2, a3) = Solve3(a, b);

        return x =>
        {
            double y = x < m
                ? a0 * Math.Sqrt(x) + a1 * x + a2 * x * x + a3 * x * x * x
                : d0 + d1 * (1 - x) + d2 * (1 - x) * (1 - x) + d3 * (1 - x) * (1 - x) * (1 - x);
            return y * 5; // 0.1 half thickness at a 20% profile => 0.5 for thickness 1
        };
    }

    private static (double, double, double) Solve3(double[,] a, double[] b)
    {
        double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double det = Det(a);
        double[] result = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var m = (double[,])a.Clone();
            for (int r = 0; r < 3; r++) m[r, c] = b[r];
            result[c] = Det(m) / det;
        }
        return (result[0], result[1], result[2]);
    }
}
