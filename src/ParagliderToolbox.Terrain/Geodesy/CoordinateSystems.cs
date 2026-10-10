namespace ParagliderToolbox.Terrain.Geodesy;

/// <summary>
/// A map projection or coordinate system a data source's rasters are in: converts WGS84 positions to its x/y
/// coordinates and back.
/// </summary>
/// <remarks>
/// Sources sample their rasters at the terrain's sample points; <see cref="Sources.SampleRequest.Project"/> projects
/// every point into the source's system (exactly on a coarse lattice, interpolated in between).
/// </remarks>
public interface ICoordinateSystem
{
    /// <summary>Gets the system's name, e.g. "EPSG:2056".</summary>
    string Name { get; }

    /// <summary>Converts a WGS84 position to the system's x (easting) and y (northing).</summary>
    (double X, double Y) Project(GeoPoint point);

    /// <summary>Converts the system's x (easting) and y (northing) to a WGS84 position.</summary>
    GeoPoint Unproject(double x, double y);
}

/// <summary>Plain WGS84 latitude and longitude (EPSG:4326): x is the longitude, y the latitude, in degrees.</summary>
public sealed class GeographicCoordinates : ICoordinateSystem
{
    /// <summary>Gets the instance.</summary>
    public static GeographicCoordinates Instance { get; } = new();

    private GeographicCoordinates()
    {
    }

    /// <inheritdoc/>
    public string Name => "EPSG:4326";

    /// <inheritdoc/>
    public (double X, double Y) Project(GeoPoint point) => (point.Longitude, point.Latitude);

    /// <inheritdoc/>
    public GeoPoint Unproject(double x, double y) => new(y, x);
}

/// <summary>
/// Web Mercator (EPSG:3857), the projection of web map tiles (WMTS "GoogleMapsCompatible"): x and y in meters on a
/// sphere of the WGS84 equatorial radius.
/// </summary>
public sealed class WebMercator : ICoordinateSystem
{
    /// <summary>The sphere's radius (m).</summary>
    public const double Radius = 6378137.0;

    /// <summary>Half the projected world's width (m): x and y run from −<see cref="HalfWorld"/> to +<see cref="HalfWorld"/>.</summary>
    public const double HalfWorld = Math.PI * Radius;

    /// <summary>The latitude limit of the projection (°).</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>Gets the instance.</summary>
    public static WebMercator Instance { get; } = new();

    private WebMercator()
    {
    }

    /// <inheritdoc/>
    public string Name => "EPSG:3857";

    /// <inheritdoc/>
    public (double X, double Y) Project(GeoPoint point)
    {
        double lat = Math.Clamp(point.Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        return (Radius * point.Longitude * Math.PI / 180, Radius * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    /// <inheritdoc/>
    public GeoPoint Unproject(double x, double y) =>
        new((2 * Math.Atan(Math.Exp(y / Radius)) - Math.PI / 2) * 180 / Math.PI, x / Radius * 180 / Math.PI);
}

/// <summary>
/// The Swiss projection coordinates LV95 (CH1903+, EPSG:2056) of swisstopo's data: easting E (2'480'000 to
/// 2'840'000) and northing N (1'070'000 to 1'300'000) in meters.
/// </summary>
/// <remarks>
/// Uses swisstopo's approximate formulas between LV95 and WGS84, accurate to about a meter (well within a sample of
/// the terrain; elevation and imagery go through the same formulas, so they stay aligned to each other).
/// </remarks>
public sealed class SwissGrid : ICoordinateSystem
{
    /// <summary>Gets the instance.</summary>
    public static SwissGrid Instance { get; } = new();

    private SwissGrid()
    {
    }

    /// <inheritdoc/>
    public string Name => "EPSG:2056";

    /// <inheritdoc/>
    public (double X, double Y) Project(GeoPoint point) => ToLv95(point);

    /// <inheritdoc/>
    public GeoPoint Unproject(double x, double y) => ToWgs84(x, y);

    /// <summary>Converts a WGS84 position to LV95 easting and northing.</summary>
    public static (double E, double N) ToLv95(GeoPoint point)
    {
        // Auxiliary values: the differences to Bern in units of 10000 arc seconds.
        double phi = (point.Latitude * 3600 - 169028.66) / 10000;
        double lambda = (point.Longitude * 3600 - 26782.5) / 10000;
        double e = 2600072.37 + 211455.93 * lambda - 10938.51 * lambda * phi - 0.36 * lambda * phi * phi - 44.54 * lambda * lambda * lambda;
        double n = 1200147.07 + 308807.95 * phi + 3745.25 * lambda * lambda + 76.63 * phi * phi - 194.56 * lambda * lambda * phi
                   + 119.79 * phi * phi * phi;
        return (e, n);
    }

    /// <summary>Converts LV95 easting and northing to a WGS84 position.</summary>
    public static GeoPoint ToWgs84(double e, double n)
    {
        double y = (e - 2600000) / 1000000;
        double x = (n - 1200000) / 1000000;
        double lambda = 2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x - 0.0436 * y * y * y;
        double phi = 16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x - 0.0447 * y * y * x - 0.0140 * x * x * x;
        // In units of 10000 arc seconds: × 10000 / 3600 = × 100 / 36 degrees.
        return new GeoPoint(phi * 100 / 36, lambda * 100 / 36);
    }
}

/// <summary>
/// The transverse Mercator projection of the WGS84 ellipsoid (Krüger's series, accurate to millimeters within several
/// hundred kilometers of the central meridian): UTM and the terrain's own local frame.
/// </summary>
public sealed class TransverseMercator : ICoordinateSystem
{
    private const double SemiMajorAxis = 6378137.0;
    private const double Flattening = 1 / 298.257223563;

    private static readonly double s_n = Flattening / (2 - Flattening);
    // The rectifying radius: meridian arc length = A × rectifying latitude.
    private static readonly double s_a = SemiMajorAxis / (1 + s_n) * (1 + s_n * s_n / 4 + Math.Pow(s_n, 4) / 64);
    // Krüger's series to the fourth order in n (Karney 2011, equations 35 and 36).
    private static readonly double[] s_alpha =
    [
        s_n / 2 - 2 * s_n * s_n / 3 + 5 * Math.Pow(s_n, 3) / 16 + 41 * Math.Pow(s_n, 4) / 180,
        13 * s_n * s_n / 48 - 3 * Math.Pow(s_n, 3) / 5 + 557 * Math.Pow(s_n, 4) / 1440,
        61 * Math.Pow(s_n, 3) / 240 - 103 * Math.Pow(s_n, 4) / 140,
        49561 * Math.Pow(s_n, 4) / 161280,
    ];
    private static readonly double[] s_beta =
    [
        s_n / 2 - 2 * s_n * s_n / 3 + 37 * Math.Pow(s_n, 3) / 96 - Math.Pow(s_n, 4) / 360,
        s_n * s_n / 48 + Math.Pow(s_n, 3) / 15 - 437 * Math.Pow(s_n, 4) / 1440,
        17 * Math.Pow(s_n, 3) / 480 - 37 * Math.Pow(s_n, 4) / 840,
        4397 * Math.Pow(s_n, 4) / 161280,
    ];
    // The first eccentricity, 2√n / (1 + n).
    private static readonly double s_e = 2 * Math.Sqrt(s_n) / (1 + s_n);

    private readonly double _lambda0;
    private readonly double _k0;
    private readonly double _falseEasting;
    private readonly double _falseNorthing;
    private readonly double _northingOffset;

    /// <summary>Initializes a projection.</summary>
    /// <param name="centralMeridian">The central meridian (°).</param>
    /// <param name="originLatitude">The latitude whose northing is <paramref name="falseNorthing"/> (°).</param>
    /// <param name="scale">The scale on the central meridian (UTM: 0.9996).</param>
    /// <param name="falseEasting">The easting of the central meridian (m).</param>
    /// <param name="falseNorthing">The northing of the origin latitude (m).</param>
    public TransverseMercator(double centralMeridian, double originLatitude = 0, double scale = 1, double falseEasting = 0, double falseNorthing = 0)
    {
        _lambda0 = centralMeridian * Math.PI / 180;
        _k0 = scale;
        _falseEasting = falseEasting;
        _falseNorthing = falseNorthing;
        CentralMeridian = centralMeridian;
        _northingOffset = 0;
        _northingOffset = Forward(originLatitude, centralMeridian).N - falseNorthing;
    }

    /// <summary>Gets the central meridian (°).</summary>
    public double CentralMeridian { get; }

    /// <summary>Creates the projection of a UTM zone (1–60).</summary>
    public static TransverseMercator Utm(int zone, bool north = true) =>
        new(zone * 6 - 183, 0, 0.9996, 500000, north ? 0 : 10000000);

    /// <inheritdoc/>
    public string Name => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Transverse Mercator {CentralMeridian:0.######}°");

    /// <inheritdoc/>
    public (double X, double Y) Project(GeoPoint point)
    {
        var (e, n) = Forward(point.Latitude, point.Longitude);
        return (e, n);
    }

    /// <inheritdoc/>
    public GeoPoint Unproject(double x, double y)
    {
        double xi = (y - _falseNorthing + _northingOffset) / (_k0 * s_a);
        double eta = (x - _falseEasting) / (_k0 * s_a);
        double xiPrime = xi, etaPrime = eta;
        for (int j = 1; j <= s_beta.Length; j++)
        {
            xiPrime -= s_beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaPrime -= s_beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        double chi = Math.Asin(Math.Sin(xiPrime) / Math.Cosh(etaPrime));
        double lambda = _lambda0 + Math.Atan2(Math.Sinh(etaPrime), Math.Cos(xiPrime));
        return new GeoPoint(ConformalToGeodetic(chi) * 180 / Math.PI, lambda * 180 / Math.PI);
    }

    // The geodetic latitude of a conformal latitude, by Newton's method on their tangents (Karney 2011, equations 7–9).
    private static double ConformalToGeodetic(double chi)
    {
        double e2 = s_e * s_e;
        double tauPrime = Math.Tan(chi), tau = tauPrime;
        for (int i = 0; i < 6; i++)
        {
            double root = Math.Sqrt(1 + tau * tau);
            double sigma = Math.Sinh(s_e * Math.Atanh(s_e * tau / root));
            double tauI = tau * Math.Sqrt(1 + sigma * sigma) - sigma * root;
            double step = (tauPrime - tauI) * (1 + (1 - e2) * tau * tau) / ((1 - e2) * Math.Sqrt(1 + tauI * tauI) * root);
            tau += step;
            if (Math.Abs(step) < 1e-15 * Math.Max(1, Math.Abs(tau))) break;
        }
        return Math.Atan(tau);
    }

    private (double E, double N) Forward(double latitude, double longitude)
    {
        double phi = latitude * Math.PI / 180;
        double dLambda = longitude * Math.PI / 180 - _lambda0;
        double sinPhi = Math.Sin(phi);
        // The conformal latitude's tangent.
        double t = Math.Sinh(Math.Atanh(sinPhi) - s_e * Math.Atanh(s_e * sinPhi));
        double xiPrime = Math.Atan2(t, Math.Cos(dLambda));
        double etaPrime = Math.Atanh(Math.Sin(dLambda) / Math.Sqrt(1 + t * t));
        double xi = xiPrime, eta = etaPrime;
        for (int j = 1; j <= s_alpha.Length; j++)
        {
            xi += s_alpha[j - 1] * Math.Sin(2 * j * xiPrime) * Math.Cosh(2 * j * etaPrime);
            eta += s_alpha[j - 1] * Math.Cos(2 * j * xiPrime) * Math.Sinh(2 * j * etaPrime);
        }
        return (_falseEasting + _k0 * s_a * eta, _falseNorthing + _k0 * s_a * xi - _northingOffset);
    }
}
