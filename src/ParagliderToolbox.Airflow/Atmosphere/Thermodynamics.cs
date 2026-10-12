namespace ParagliderToolbox.Airflow.Atmosphere;

/// <summary>
/// The constants and conversions of moist air the soundings and the solver use: potential temperature, saturation and
/// mixing ratios, dew points and the lifting condensation level. Temperatures in kelvin, pressures in pascal, mixing
/// ratios in kg of water vapor per kg of dry air.
/// </summary>
public static class Thermodynamics
{
    /// <summary>The gravitational acceleration (m/s²).</summary>
    public const double Gravity = 9.80665;

    /// <summary>The gas constant of dry air (J/(kg K)).</summary>
    public const double DryAirConstant = 287.05;

    /// <summary>The gas constant of water vapor (J/(kg K)).</summary>
    public const double VaporConstant = 461.5;

    /// <summary>The specific heat of dry air at constant pressure (J/(kg K)).</summary>
    public const double SpecificHeat = 1004.6;

    /// <summary>The latent heat of condensation (J/kg).</summary>
    public const double LatentHeat = 2.501e6;

    /// <summary>The reference pressure of potential temperatures (Pa).</summary>
    public const double ReferencePressure = 100_000;

    /// <summary>R/cp of dry air.</summary>
    public const double Kappa = DryAirConstant / SpecificHeat;

    /// <summary>The ratio of the gas constants of dry air and vapor.</summary>
    public const double Epsilon = DryAirConstant / VaporConstant;

    /// <summary>0 °C in kelvin.</summary>
    public const double ZeroCelsius = 273.15;

    /// <summary>The dry adiabatic lapse rate (K/m).</summary>
    public const double DryLapseRate = Gravity / SpecificHeat;

    /// <summary>Gets the saturation vapor pressure over water (Pa) at <paramref name="temperature"/> (K; Bolton 1980).</summary>
    public static double SaturationVaporPressure(double temperature)
    {
        double celsius = temperature - ZeroCelsius;
        return 611.2 * Math.Exp(17.67 * celsius / (celsius + 243.5));
    }

    /// <summary>Gets the mixing ratio of air with <paramref name="vaporPressure"/> at <paramref name="pressure"/>.</summary>
    public static double MixingRatio(double vaporPressure, double pressure) =>
        Epsilon * vaporPressure / Math.Max(pressure - vaporPressure, 1);

    /// <summary>Gets the mixing ratio of saturated air at <paramref name="temperature"/> and <paramref name="pressure"/>.</summary>
    public static double SaturationMixingRatio(double temperature, double pressure) =>
        MixingRatio(SaturationVaporPressure(temperature), pressure);

    /// <summary>Gets the potential temperature (K) of air at <paramref name="temperature"/> and <paramref name="pressure"/>.</summary>
    public static double PotentialTemperature(double temperature, double pressure) =>
        temperature * Math.Pow(ReferencePressure / pressure, Kappa);

    /// <summary>Gets the temperature (K) of air with <paramref name="potentialTemperature"/> at <paramref name="pressure"/>.</summary>
    public static double TemperatureOf(double potentialTemperature, double pressure) =>
        potentialTemperature * Math.Pow(pressure / ReferencePressure, Kappa);

    /// <summary>Gets the Exner function (p/p₀)^κ, which turns potential temperatures into temperatures.</summary>
    public static double Exner(double pressure) => Math.Pow(pressure / ReferencePressure, Kappa);

    /// <summary>Gets the dew point (K) of air with <paramref name="mixingRatio"/> at <paramref name="pressure"/>.</summary>
    public static double DewPoint(double mixingRatio, double pressure)
    {
        double vapor = Math.Max(mixingRatio, 1e-8) * pressure / (Epsilon + mixingRatio);
        double ln = Math.Log(vapor / 611.2);
        return ZeroCelsius + 243.5 * ln / (17.67 - ln);
    }

    /// <summary>
    /// Gets the temperature (K) at which air at <paramref name="temperature"/> with <paramref name="dewPoint"/> becomes
    /// saturated when lifted dry adiabatically (Bolton 1980): the temperature at the cloud base.
    /// </summary>
    public static double CondensationTemperature(double temperature, double dewPoint) =>
        1 / (1 / (Math.Min(dewPoint, temperature) - 56) + Math.Log(temperature / Math.Min(dewPoint, temperature)) / 800) + 56;

    /// <summary>
    /// Gets how far (m) air at <paramref name="temperature"/> with <paramref name="dewPoint"/> rises dry adiabatically
    /// before it condenses: the cloud base above where it starts (about 122 m per kelvin of dew point spread).
    /// </summary>
    public static double CondensationHeight(double temperature, double dewPoint) =>
        (temperature - CondensationTemperature(temperature, dewPoint)) / DryLapseRate;

    /// <summary>Gets the density (kg/m³) of dry air at <paramref name="temperature"/> and <paramref name="pressure"/>.</summary>
    public static double Density(double temperature, double pressure) => pressure / (DryAirConstant * temperature);

    /// <summary>
    /// Gets how high a parcel of air rises through <paramref name="sounding"/> (m): dry adiabatically until it condenses,
    /// then moist adiabatically (its cloud water's latent heat warming it), until it is no longer warmer than the
    /// sounding: the top of the cumulus it makes, or of its thermal when it stays dry.
    /// </summary>
    /// <param name="sounding">The air around it.</param>
    /// <param name="height">Where it starts (m).</param>
    /// <param name="potentialTemperature">Its potential temperature (K).</param>
    /// <param name="mixingRatio">Its water vapor (kg/kg).</param>
    /// <param name="entrainment">How much of the air around it it mixes in per meter (1/m; cumuli about 0.001), or 0 for none.</param>
    public static double ParcelTop(Sounding sounding, double height, double potentialTemperature, double mixingRatio, double entrainment = 0)
    {
        const double step = 10;
        double top = sounding.Levels[^1].Height;
        double y = height;
        for (; y < top; y += step)
        {
            double pressure = sounding.PressureAt(y), exner = Exner(pressure);
            // The liquid water potential temperature and the total water are kept; the cloud water is what exceeds saturation.
            // Saturation adjustment (Newton steps: the latent heat changes the saturation it depends on).
            double temperature = potentialTemperature * exner, cloud = 0;
            for (int n = 0; n < 3; n++)
            {
                double saturation = SaturationMixingRatio(temperature, pressure);
                double excess = mixingRatio - cloud - saturation;
                if (excess <= 0 && cloud <= 0) break;
                double change = excess / (1 + LatentHeat * LatentHeat * saturation / (SpecificHeat * VaporConstant * temperature * temperature));
                change = Math.Max(change, -cloud);
                cloud += change;
                temperature += LatentHeat * change / SpecificHeat;
            }
            if (y > height + step && temperature <= sounding.TemperatureAt(y)) break;
            // In cloud the air around it mixes in: its warmth and its vapor (the cloud water evaporates into it). Below, the
            // mixed layer around a thermal is about as warm as the thermal.
            double mix = cloud > 0 ? entrainment * step : 0;
            potentialTemperature += mix * (sounding.PotentialTemperatureAt(y) - potentialTemperature);
            mixingRatio += mix * (sounding.MixingRatioAt(y) - mixingRatio);
        }
        return y;
    }
}
