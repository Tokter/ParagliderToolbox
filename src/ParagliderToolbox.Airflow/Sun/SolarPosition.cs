using System.Numerics;

namespace ParagliderToolbox.Airflow.Sun;

/// <summary>Where the sun is in the sky.</summary>
/// <param name="Azimuth">Degrees clockwise from north (90 east, 180 south).</param>
/// <param name="Elevation">Degrees above the horizon (negative at night).</param>
public readonly record struct SunPosition(double Azimuth, double Elevation)
{
    /// <summary>Gets whether the sun is above the horizon.</summary>
    public bool IsUp => Elevation > 0;

    /// <summary>Gets the unit vector towards the sun in the terrain's frame (+X east, +Y up, +Z south).</summary>
    public Vector3 Direction
    {
        get
        {
            double az = Azimuth * Math.PI / 180, el = Elevation * Math.PI / 180;
            return new Vector3((float)(Math.Cos(el) * Math.Sin(az)), (float)Math.Sin(el), (float)(-Math.Cos(el) * Math.Cos(az)));
        }
    }
}

/// <summary>
/// The sun's position (NOAA's solar calculator, good to a fraction of a degree for centuries around today) and its clear
/// sky irradiance.
/// </summary>
public static class SolarPosition
{
    /// <summary>The solar constant (W/m²).</summary>
    public const double SolarConstant = 1361;

    /// <summary>Gets the sun's position seen from <paramref name="latitude"/>, <paramref name="longitude"/> at <paramref name="utc"/>.</summary>
    public static SunPosition At(double latitude, double longitude, DateTime utc)
    {
        double julianDay = utc.ToOADate() + 2415018.5;
        double t = (julianDay - 2451545) / 36525;
        double meanLongitude = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        double meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        double eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double m = Rad(meanAnomaly);
        double center = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(2 * m) * (0.019993 - 0.000101 * t) + Math.Sin(3 * m) * 0.000289;
        double omega = Rad(125.04 - 1934.136 * t);
        double apparentLongitude = meanLongitude + center - 0.00569 - 0.00478 * Math.Sin(omega);
        double meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        double obliquity = Rad(meanObliquity + 0.00256 * Math.Cos(omega));
        double declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(Rad(apparentLongitude)));
        double y = Math.Tan(obliquity / 2) * Math.Tan(obliquity / 2);
        double l0 = Rad(meanLongitude);
        double equationOfTime = 4 * Deg(y * Math.Sin(2 * l0) - 2 * eccentricity * Math.Sin(m) + 4 * eccentricity * y * Math.Sin(m) * Math.Cos(2 * l0)
                                        - 0.5 * y * y * Math.Sin(4 * l0) - 1.25 * eccentricity * eccentricity * Math.Sin(2 * m));
        double minutes = utc.TimeOfDay.TotalMinutes;
        double trueSolarTime = Mod(minutes + equationOfTime + 4 * longitude, 1440);
        double hourAngle = Rad(trueSolarTime / 4 - 180);

        double phi = Rad(latitude);
        double cosZenith = Math.Clamp(Math.Sin(phi) * Math.Sin(declination) + Math.Cos(phi) * Math.Cos(declination) * Math.Cos(hourAngle), -1, 1);
        double elevation = 90 - Deg(Math.Acos(cosZenith));
        // Measured from south, westwards positive; turned to clockwise from north.
        double fromSouth = Math.Atan2(Math.Sin(hourAngle), Math.Cos(hourAngle) * Math.Sin(phi) - Math.Tan(declination) * Math.Cos(phi));
        double azimuth = Mod(Deg(fromSouth) + 180, 360);
        return new SunPosition(azimuth, elevation + Refraction(elevation));
    }

    /// <summary>
    /// Gets the clear sky irradiance at <paramref name="elevation"/> (degrees) seen from <paramref name="height"/> (m):
    /// the direct beam, normal to the sun, and the diffuse light on a horizontal surface (W/m²).
    /// </summary>
    /// <remarks>Meinel's direct beam through the air mass of Kasten and Young, thinner in the mountains; diffuse light about 15% of the beam on the horizontal.</remarks>
    public static (double Direct, double Diffuse) ClearSky(double elevation, double height)
    {
        if (elevation <= 0) return (0, 0);
        double airMass = 1 / (Math.Sin(Rad(elevation)) + 0.50572 * Math.Pow(elevation + 6.07995, -1.6364));
        airMass *= Math.Exp(-Math.Max(height, 0) / 8434);
        double direct = SolarConstant * Math.Pow(0.7, Math.Pow(airMass, 0.678));
        double diffuse = 0.15 * direct * Math.Sin(Rad(elevation)) + 10 * Math.Sin(Rad(elevation));
        return (direct, diffuse);
    }

    // The standard atmospheric refraction near the horizon (degrees).
    private static double Refraction(double elevation)
    {
        if (elevation > 85 || elevation < -2) return 0;
        double e = Math.Max(elevation, -0.575);
        return 1.02 / Math.Tan(Rad(e + 10.3 / (e + 5.11))) / 60;
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static double Deg(double radians) => radians * 180 / Math.PI;

    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
