namespace ParagliderToolbox.Airflow.Atmosphere;

/// <summary>The well-mixed layer of air the sun's heat makes above the ground.</summary>
/// <param name="Ground">The ground height (m above sea level).</param>
/// <param name="Top">The height the layer reaches, where the sounding is as warm as the mixed air: the thermals' top (m).</param>
/// <param name="PotentialTemperature">The mixed air's potential temperature (K).</param>
/// <param name="MixingRatio">The mixed air's water vapor (kg/kg).</param>
/// <param name="CloudBase">Where rising mixed air condenses (m); above <paramref name="Top"/> the thermals stay blue.</param>
/// <param name="GroundTemperature">The mixed air's temperature at the ground (K).</param>
public readonly record struct MixedLayer(double Ground, double Top, double PotentialTemperature, double MixingRatio, double CloudBase, double GroundTemperature)
{
    /// <summary>Gets the depth of the layer (m).</summary>
    public double Depth => Top - Ground;

    /// <summary>Gets whether the thermals reach their condensation level, so cumulus clouds form.</summary>
    public bool HasClouds => CloudBase < Top;
}

/// <summary>
/// The convective boundary layer the sun's heat makes of a morning sounding, by encroachment: the heat warms the lowest
/// air until it is well mixed (one potential temperature) up to where the sounding is as warm. Inversions take much heat
/// to break through, so they cap the thermals.
/// </summary>
public static class BoundaryLayer
{
    private const double Step = 20;

    /// <summary>Grows the mixed layer above <paramref name="ground"/> with <paramref name="heat"/>.</summary>
    /// <param name="sounding">The morning sounding.</param>
    /// <param name="ground">The ground height (m above sea level).</param>
    /// <param name="heat">The sensible heat that went into the air since the morning (J/m²).</param>
    /// <param name="evaporation">The water that evaporated into it since then (kg/m²).</param>
    public static MixedLayer Grow(Sounding sounding, double ground, double heat, double evaporation = 0)
    {
        double low = sounding.PotentialTemperatureAt(ground + Step / 2);
        double thetaM = low, top = ground;
        if (heat > 0)
        {
            // The energy a mixed layer of potential temperature θ needs grows with θ: bisect for the one the heat makes.
            double high = low + 40;
            for (int i = 0; i < 40; i++)
            {
                double mid = (low + high) / 2;
                if (Energy(sounding, ground, mid, out _) < heat) low = mid;
                else high = mid;
            }
            thetaM = (low + high) / 2;
            Energy(sounding, ground, thetaM, out top);
        }

        // The vapor mixes through the layer, and the evaporated water joins it.
        double vapor = 0, mass = 0;
        for (double z = ground; z < top; z += Step)
        {
            double rho = sounding.DensityAt(z + Step / 2);
            vapor += sounding.MixingRatioAt(z + Step / 2) * rho * Step;
            mass += rho * Step;
        }
        double mixing = mass > 0 ? (vapor + Math.Max(evaporation, 0)) / mass : sounding.MixingRatioAt(ground);

        double pressure = sounding.PressureAt(ground);
        double temperature = Math.Max(Thermodynamics.TemperatureOf(thetaM, pressure), sounding.TemperatureAt(ground));
        double dewPoint = Thermodynamics.DewPoint(mixing, pressure);
        double cloudBase = ground + Thermodynamics.CondensationHeight(temperature, dewPoint);
        return new MixedLayer(ground, top, thetaM, mixing, cloudBase, temperature);
    }

    /// <summary>
    /// Gets the convective velocity scale w* (m/s), the typical climb of thermals in a mixed layer of
    /// <paramref name="depth"/> heated with <paramref name="heatFlux"/> (W/m²) (Deardorff); 0 without heating.
    /// </summary>
    public static double ConvectiveVelocity(double heatFlux, double depth, double potentialTemperature, double density = 1.1) =>
        heatFlux <= 0 || depth <= 0 ? 0 : Math.Cbrt(Thermodynamics.Gravity / potentialTemperature * heatFlux / (density * Thermodynamics.SpecificHeat) * depth);

    // The heat (J/m²) that warms the air above the ground to θ up to where the sounding is warmer.
    private static double Energy(Sounding sounding, double ground, double theta, out double top)
    {
        double energy = 0, z = ground;
        while (z < ground + 15000)
        {
            double middle = z + Step / 2;
            double deficit = theta - sounding.PotentialTemperatureAt(middle);
            if (deficit <= 0) break;
            double pressure = sounding.PressureAt(middle);
            energy += deficit * Thermodynamics.Exner(pressure) * sounding.DensityAt(middle) * Thermodynamics.SpecificHeat * Step;
            z += Step;
        }
        top = z;
        return energy;
    }
}
