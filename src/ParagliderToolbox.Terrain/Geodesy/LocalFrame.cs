namespace ParagliderToolbox.Terrain.Geodesy;

/// <summary>
/// The terrain's coordinates: meters on a transverse Mercator projection whose central meridian runs through the
/// origin (the terrain's center), in the glTF frame: +X east, +Y up, +Z south (north is −Z).
/// </summary>
/// <remarks>
/// The projection keeps distances true to within 0.003% at 25 km from the origin (0.012% at 50 km), so the terrain is
/// a flat world in meters, as a game expects; grid north (−Z) is true north on the central meridian. Heights (Y) are
/// the sources' heights above sea level.
/// </remarks>
public sealed class LocalFrame
{
    private readonly TransverseMercator _projection;

    /// <summary>Initializes the frame centered on <paramref name="origin"/>.</summary>
    public LocalFrame(GeoPoint origin)
    {
        Origin = origin;
        _projection = new TransverseMercator(origin.Longitude, origin.Latitude);
    }

    /// <summary>Gets the position at the frame's origin (X = Z = 0).</summary>
    public GeoPoint Origin { get; }

    /// <summary>Gets the projection (easting = X, northing = −Z).</summary>
    public TransverseMercator Projection => _projection;

    /// <summary>Gets the WGS84 position of local <paramref name="x"/> (east) and <paramref name="z"/> (south).</summary>
    public GeoPoint ToGeo(double x, double z) => _projection.Unproject(x, -z);

    /// <summary>Gets the local X (east) and Z (south) of a WGS84 position.</summary>
    public (double X, double Z) FromGeo(GeoPoint point)
    {
        var (e, n) = _projection.Project(point);
        return (e, -n);
    }

    /// <summary>Gets the latitude/longitude box around a local rectangle (its corners and edge midpoints).</summary>
    public GeoBounds Bounds(double x0, double z0, double x1, double z1)
    {
        double xm = (x0 + x1) / 2, zm = (z0 + z1) / 2;
        return GeoBounds.Around([
            ToGeo(x0, z0), ToGeo(xm, z0), ToGeo(x1, z0), ToGeo(x1, zm),
            ToGeo(x1, z1), ToGeo(xm, z1), ToGeo(x0, z1), ToGeo(x0, zm),
        ]);
    }
}
