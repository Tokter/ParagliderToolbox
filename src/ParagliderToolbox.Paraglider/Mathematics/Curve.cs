using System.Globalization;
using System.Text.Json.Serialization;

namespace ParagliderToolbox.Paraglider.Mathematics;

/// <summary>A control point of a <see cref="Curve"/>.</summary>
/// <param name="X">The position, from 0 to 1.</param>
/// <param name="Y">The value.</param>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// A smooth function on [0, 1] through control points, used for the distributions of a design: chord, arc, twist,
/// thickness and ballooning along the span or the chord.
/// </summary>
/// <remarks>
/// The curve is a monotone cubic (Fritsch–Carlson) spline: it passes through every point and doesn't overshoot between
/// them, so a falling chord distribution never bulges above its neighbors. Outside the first and last point it stays
/// constant. Curves are immutable; editors create new ones.
/// </remarks>
public sealed class Curve : IEquatable<Curve>
{
    private readonly CurvePoint[] _points;
    private readonly double[] _tangents;

    /// <summary>Initializes a curve through <paramref name="points"/> (sorted by X; at least one).</summary>
    [JsonConstructor]
    public Curve(IReadOnlyList<CurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("A curve needs at least one point.", nameof(points));
        _points = points.OrderBy(p => p.X).ToArray();
        _tangents = ComputeTangents(_points);
    }

    /// <summary>Initializes a curve through the points given as x, y pairs.</summary>
    public Curve(params double[] xy) : this(ToPoints(xy))
    {
    }

    /// <summary>Gets the control points, sorted by X.</summary>
    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>Gets a constant curve.</summary>
    public static Curve Constant(double value) => new(0, value, 1, value);

    /// <summary>Samples <paramref name="f"/> at <paramref name="count"/> evenly spaced points.</summary>
    public static Curve FromFunction(Func<double, double> f, int count = 9) =>
        new(Enumerable.Range(0, count).Select(i => { double x = i / (double)(count - 1); return new CurvePoint(x, f(x)); }).ToArray());

    /// <summary>Evaluates the curve at <paramref name="x"/>.</summary>
    public double Evaluate(double x)
    {
        var p = _points;
        if (p.Length == 1 || x <= p[0].X) return p[0].Y;
        if (x >= p[^1].X) return p[^1].Y;

        int i = FindSegment(x);
        double h = p[i + 1].X - p[i].X;
        if (h <= 0) return p[i + 1].Y;
        double t = (x - p[i].X) / h;
        double t2 = t * t, t3 = t2 * t;
        double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
        return h00 * p[i].Y + h10 * h * _tangents[i] + h01 * p[i + 1].Y + h11 * h * _tangents[i + 1];
    }

    /// <summary>Gets the integral of the curve from 0 to 1 (numerically).</summary>
    public double Integrate(int samples = 400)
    {
        double sum = 0;
        for (int i = 0; i < samples; i++) sum += Evaluate((i + 0.5) / samples);
        return sum / samples;
    }

    /// <summary>Returns a copy with every value multiplied by <paramref name="factor"/>.</summary>
    public Curve Scale(double factor) => new(_points.Select(p => p with { Y = p.Y * factor }).ToArray());

    /// <summary>Formats the curve as "x:y; x:y; ..." (invariant culture), the text the property editor shows.</summary>
    public override string ToString() =>
        string.Join("; ", _points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.###}:{p.Y:0.####}")));

    /// <summary>Parses the text written by <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">The text isn't a list of x:y pairs.</exception>
    public static Curve Parse(string text)
    {
        var points = new List<CurvePoint>();
        foreach (string part in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(':', StringSplitOptions.TrimEntries);
            if (xy.Length != 2
                || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                throw new FormatException($"'{part}' isn't an x:y point.");
            }
            points.Add(new CurvePoint(Math.Clamp(x, 0, 1), y));
        }
        if (points.Count == 0) throw new FormatException("A curve needs at least one point.");
        return new Curve(points);
    }

    /// <inheritdoc/>
    public bool Equals(Curve? other) => other is not null && _points.AsSpan().SequenceEqual(other._points);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Curve);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in _points) hash.Add(p);
        return hash.ToHashCode();
    }

    private int FindSegment(double x)
    {
        int lo = 0, hi = _points.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_points[mid].X <= x) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static CurvePoint[] ToPoints(double[] xy)
    {
        if (xy.Length % 2 != 0) throw new ArgumentException("Give x, y pairs.", nameof(xy));
        var points = new CurvePoint[xy.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new CurvePoint(xy[2 * i], xy[2 * i + 1]);
        return points;
    }

    // Fritsch–Carlson: secant-based tangents, limited so every segment stays monotone.
    private static double[] ComputeTangents(CurvePoint[] p)
    {
        int n = p.Length;
        var m = new double[n];
        if (n < 2) return m;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double h = p[i + 1].X - p[i].X;
            d[i] = h > 0 ? (p[i + 1].Y - p[i].Y) / h : 0;
        }
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        }
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }
            double a = m[i] / d[i], b = m[i + 1] / d[i];
            double s = a * a + b * b;
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }
        return m;
    }
}
