using System.Numerics;
using ParagliderToolbox.Airflow.Sun;

namespace ParagliderToolbox.Airflow.Surface;

/// <summary>The heat a surface gives the air.</summary>
/// <param name="Sensible">The sensible heat flux, which warms the air (W/m²; negative when the ground cools it).</param>
/// <param name="Latent">The latent heat flux, which evaporates water (W/m²).</param>
/// <param name="Sunlight">The absorbed sunlight (W/m²).</param>
public readonly record struct SurfaceFlux(float Sensible, float Latent, float Sunlight);

/// <summary>
/// How the sun heats the ground and the ground the air: the sunlight on every slope (by its angle to the sun, in the
/// terrain's shadows), what the land cover makes of it (reflects, stores, evaporates, heats the air with), and the heat
/// gathered since the morning.
/// </summary>
/// <remarks>
/// Net radiation = absorbed sunlight − the longwave loss of a clear day (<see cref="LongwaveLoss"/>); a share goes into
/// the ground, the rest is split between sensible heat (warming the air: the thermals) and evaporation by the land
/// cover's <see cref="SurfaceProperties.SensibleShare"/>. Sunny slopes facing the sun get more than flat ground, shaded
/// ones only the diffuse light.
/// </remarks>
public static class SurfaceHeating
{
    /// <summary>The net longwave radiation the ground loses on a clear day (W/m²).</summary>
    public const double LongwaveLoss = 90;

    /// <summary>Gets which samples of <paramref name="field"/> lie in the terrain's shadow (by marching towards the sun).</summary>
    /// <param name="field">The terrain.</param>
    /// <param name="sun">The sun.</param>
    /// <param name="beyond">The ground around the field (m above sea level), whose mountains shade it too, or <c>null</c> for none.</param>
    public static bool[] Shadows(HeightField field, SunPosition sun, Func<double, double, float>? beyond = null)
    {
        var shadow = new bool[field.Heights.Length];
        if (!sun.IsUp) return Array.ConvertAll(shadow, _ => true);
        var direction = sun.Direction;
        double horizontal = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (horizontal < 1e-6) return shadow;
        double dx = direction.X / horizontal, dz = direction.Z / horizontal, rise = direction.Y / horizontal;
        double step = field.Spacing, far = Math.Max(field.Spacing, 25);
        Parallel.For(0, field.Rows, row =>
        {
            for (int column = 0; column < field.Columns; column++)
            {
                double x = field.X0 + column * field.Spacing, z = field.Z0 + row * field.Spacing;
                double y = field.Heights[row * field.Columns + column] + 1;
                for (double d = step; ; )
                {
                    double px = x + dx * d, pz = z + dz * d, py = y + rise * d;
                    bool inside = px >= field.X0 && pz >= field.Z0 && px <= field.X0 + field.Width && pz <= field.Z0 + field.Depth;
                    // Beyond the field, up to the highest mountains (and 30 km).
                    if (beyond is null ? !inside || py > field.MaxHeight : py > Math.Max(HighestGround, field.MaxHeight) || d > 30000) break;
                    if ((inside ? field.HeightAt(px, pz) : beyond!(px, pz)) > py)
                    {
                        shadow[row * field.Columns + column] = true;
                        break;
                    }
                    d += inside ? step : far;
                }
            }
        });
        return shadow;
    }

    // Above the highest ground of the Alps: no mountain shades a ray beyond it.
    private const double HighestGround = 4900;

    /// <summary>Gets the heat of every sample of <paramref name="field"/> with the sun at <paramref name="sun"/>.</summary>
    /// <param name="field">The terrain.</param>
    /// <param name="cover">The land cover of every sample, or <c>null</c> for meadows everywhere.</param>
    /// <param name="sun">The sun.</param>
    /// <param name="shadows">The samples in the shadow (see <see cref="Shadows"/>), or <c>null</c> to compute them.</param>
    public static SurfaceFlux[] Fluxes(HeightField field, LandCover[]? cover, SunPosition sun, bool[]? shadows = null)
    {
        shadows ??= Shadows(field, sun);
        var toSun = sun.Direction;
        var fluxes = new SurfaceFlux[field.Heights.Length];
        Parallel.For(0, field.Rows, row =>
        {
            for (int column = 0; column < field.Columns; column++)
            {
                int i = row * field.Columns + column;
                var kind = cover?[i] ?? LandCover.Meadow;
                var properties = LandCovers.Properties(kind);
                var normal = field.NormalAt(column, row);
                var (direct, diffuse) = SolarPosition.ClearSky(sun.Elevation, field.Heights[i]);
                double incidence = shadows[i] ? 0 : Math.Max(0, Vector3.Dot(normal, toSun));
                double sunlight = (1 - properties.Albedo) * (direct * incidence + diffuse * (1 + normal.Y) / 2);
                // Water stores the sunlight in its depth: its surface stays at the water's temperature, and the air
                // gets (or gives) heat by how much warmer it is (see WaterFlux).
                fluxes[i] = kind == LandCover.Water ? new SurfaceFlux(0, 0, (float)sunlight) : Partition(sunlight, properties);
            }
        });
        return fluxes;
    }

    /// <summary>The bulk transfer coefficient of heat and vapor over water, about 25 m above it.</summary>
    public const double WaterTransfer = 1.1e-3;

    // The surface temperature of central European lowland lakes (about 500 m) by month (°C).
    private static readonly double[] s_lakeTemperature = [4.5, 4, 5.5, 8, 13, 18, 21, 21.5, 18, 13.5, 9, 6];

    /// <summary>
    /// Gets a lake's usual surface temperature (°C) on a day of the year at <paramref name="height"/>: the season's
    /// temperature of the lowland lakes (21 °C in July, 4–5 °C in winter), 6 K colder per kilometer higher, never below
    /// freezing.
    /// </summary>
    public static double WaterTemperature(int month, int day, double height)
    {
        // Between the middles of the months.
        double position = month - 1 + (day - 15) / 30.0;
        int a = ((int)Math.Floor(position) % 12 + 12) % 12, b = (a + 1) % 12;
        double t = position - Math.Floor(position);
        double lowland = s_lakeTemperature[a] + t * (s_lakeTemperature[b] - s_lakeTemperature[a]);
        return Math.Max(0.5, lowland - 0.006 * (height - 500));
    }

    /// <summary>
    /// Gets the heat fluxes of water at <paramref name="waterTemperature"/> under air at <paramref name="airTemperature"/>
    /// with <paramref name="airVapor"/> and <paramref name="wind"/> (bulk formulas): sensible heat (W/m², negative when the
    /// water is colder and cools the air, as lakes do on summer afternoons) and evaporation (kg/(m² s)).
    /// </summary>
    public static (double Sensible, double Evaporation) WaterFlux(double waterTemperature, double airTemperature, double airVapor, double wind,
        double pressure, double density)
    {
        // Calm air still exchanges heat (free convection, gusts): at least 1 m/s.
        double exchange = density * WaterTransfer * Math.Max(wind, 1);
        double sensible = exchange * Atmosphere.Thermodynamics.SpecificHeat * (waterTemperature - airTemperature);
        double saturation = Atmosphere.Thermodynamics.SaturationMixingRatio(waterTemperature, pressure);
        return (sensible, Math.Max(0, exchange * (saturation - airVapor)));
    }

    /// <summary>Splits the absorbed <paramref name="sunlight"/> (W/m²) of a surface into its heat fluxes.</summary>
    public static SurfaceFlux Partition(double sunlight, SurfaceProperties properties)
    {
        double net = sunlight - LongwaveLoss;
        double available = net - properties.GroundShare * Math.Max(net, 0);
        // In the shade (or at night) the ground's stored heat covers most of the loss; the air cools a little.
        double sensible = available > 0 ? available * properties.SensibleShare : available * 0.4;
        double latent = available > 0 ? available * (1 - properties.SensibleShare) : 0;
        return new SurfaceFlux((float)sensible, (float)latent, (float)sunlight);
    }

    /// <summary>
    /// Gathers the heat of every sample from the morning (the first hour the sun is up) until <paramref name="until"/>
    /// (UTC), in steps of <paramref name="step"/>: the sensible heat (J/m², at least 0) and the evaporated water (kg/m²).
    /// </summary>
    public static (float[] Heat, float[] Evaporation) Accumulate(HeightField field, LandCover[]? cover, double latitude, double longitude,
        DateTime until, TimeSpan step)
    {
        var heat = new double[field.Heights.Length];
        var water = new double[field.Heights.Length];
        // From six hours before solar noon at the latest (the sun rises before), in whole steps back from the end.
        var start = until.Date.AddHours(12 - longitude / 15 - 7);
        for (var time = until - step / 2; time > start; time -= step)
        {
            var sun = SolarPosition.At(latitude, longitude, time);
            if (sun.Elevation < 1) continue;
            var fluxes = Fluxes(field, cover, sun);
            double seconds = step.TotalSeconds;
            for (int i = 0; i < heat.Length; i++)
            {
                heat[i] += fluxes[i].Sensible * seconds;
                water[i] += fluxes[i].Latent * seconds / Atmosphere.Thermodynamics.LatentHeat;
            }
        }
        return (Array.ConvertAll(heat, h => (float)Math.Max(h, 0)), Array.ConvertAll(water, w => (float)w));
    }
}
