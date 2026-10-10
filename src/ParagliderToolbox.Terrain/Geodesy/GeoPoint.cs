using System.Globalization;
using System.Text.RegularExpressions;

namespace ParagliderToolbox.Terrain.Geodesy;

/// <summary>A position on the WGS84 ellipsoid: latitude (north positive) and longitude (east positive) in degrees.</summary>
/// <param name="Latitude">The latitude, −90 to 90°.</param>
/// <param name="Longitude">The longitude, −180 to 180°.</param>
public readonly partial record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>Gets whether the latitude and longitude are within their ranges.</summary>
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;

    /// <summary>Formats the point as "46.686300, 7.863200" (latitude, longitude; invariant culture).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.000000}, {Longitude:0.000000}");

    /// <summary>
    /// Reads a position: decimal degrees as maps copy them ("46.6863, 7.8632", latitude first), degrees, minutes and
    /// seconds ("46°41'10.7\"N 7°51'47.5\"E"), or Swiss grid coordinates (LV95 "2'632'500, 1'169'500" or LV03
    /// "632500 169500", easting first).
    /// </summary>
    /// <returns>Whether <paramref name="text"/> is a position.</returns>
    public static bool TryParse(string? text, out GeoPoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var dms = DmsPattern().Matches(text);
        if (dms.Count == 2)
        {
            double? lat = null, lon = null;
            foreach (Match m in dms)
            {
                double value = double.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture)
                               + (m.Groups["m"].Success ? double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) / 60 : 0)
                               + (m.Groups["s"].Success ? double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) / 3600 : 0);
                char hemisphere = char.ToUpperInvariant(m.Groups["h"].Value[0]);
                if (hemisphere is 'S' or 'W') value = -value;
                if (hemisphere is 'N' or 'S') lat = value;
                else lon = value;
            }
            if (lat is { } la && lon is { } lo)
            {
                point = new GeoPoint(la, lo);
                return point.IsValid;
            }
            return false;
        }

        // Swiss coordinates group their thousands with apostrophes.
        string[] parts = text.Replace("'", "").Replace("’", "")
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            return false;
        }
        if (Math.Abs(a) > 1000 && Math.Abs(b) > 1000)
        {
            // LV03 is LV95 without the leading 2 and 1 million.
            if (a < 2_000_000) a += 2_000_000;
            if (b < 1_000_000) b += 1_000_000;
            if (a is < 2_400_000 or > 2_900_000 || b is < 1_000_000 or > 1_350_000) return false;
            point = SwissGrid.ToWgs84(a, b);
            return true;
        }
        point = new GeoPoint(a, b);
        return point.IsValid;
    }

    [GeneratedRegex(@"(?<d>\d+(?:\.\d+)?)\s*°\s*(?:(?<m>\d+(?:\.\d+)?)\s*['′]\s*)?(?:(?<s>\d+(?:\.\d+)?)\s*(?:""|″|''|′′)\s*)?(?<h>[NSEWnsew])")]
    private static partial Regex DmsPattern();
}

/// <summary>A box of latitudes and longitudes, in degrees.</summary>
/// <param name="South">The southern edge.</param>
/// <param name="West">The western edge.</param>
/// <param name="North">The northern edge.</param>
/// <param name="East">The eastern edge.</param>
public readonly record struct GeoBounds(double South, double West, double North, double East)
{
    /// <summary>Gets whether the box overlaps <paramref name="other"/>.</summary>
    public bool Intersects(GeoBounds other) =>
        South < other.North && other.South < North && West < other.East && other.West < East;

    /// <summary>Gets whether <paramref name="point"/> is in the box.</summary>
    public bool Contains(GeoPoint point) =>
        point.Latitude >= South && point.Latitude <= North && point.Longitude >= West && point.Longitude <= East;

    /// <summary>Gets the box around <paramref name="points"/>.</summary>
    public static GeoBounds Around(IEnumerable<GeoPoint> points)
    {
        double south = double.MaxValue, west = double.MaxValue, north = double.MinValue, east = double.MinValue;
        foreach (var p in points)
        {
            south = Math.Min(south, p.Latitude);
            north = Math.Max(north, p.Latitude);
            west = Math.Min(west, p.Longitude);
            east = Math.Max(east, p.Longitude);
        }
        return new GeoBounds(south, west, north, east);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{South:0.#####},{West:0.#####} – {North:0.#####},{East:0.#####}");
}
