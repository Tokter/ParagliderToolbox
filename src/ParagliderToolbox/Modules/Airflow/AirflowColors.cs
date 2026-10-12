using System.Numerics;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>What a slice through the airflow colors.</summary>
public enum AirflowQuantity
{
    /// <summary>The vertical wind: lift red, sink blue.</summary>
    Lift,

    /// <summary>The turbulence: calm green to rough red.</summary>
    Turbulence,

    /// <summary>The wind speed.</summary>
    Wind,

    /// <summary>How often the air rises at 1 m/s or more.</summary>
    LiftChance,

    /// <summary>How much warmer the air is than the morning sounding.</summary>
    Temperature,
}

/// <summary>What the map on the ground shows.</summary>
public enum GroundMap
{
    /// <summary>The sensible heat the ground gives the air.</summary>
    Heating,

    /// <summary>The land cover read from the aerial images.</summary>
    LandCover,
}

/// <summary>The color scales of the airflow's views (linear RGBA), with the stops their legends show.</summary>
public static class AirflowColors
{
    /// <summary>A color scale: values and their colors, interpolated in between.</summary>
    public sealed record Scale(string Title, string Unit, (float Value, Vector3 Color)[] Stops)
    {
        /// <summary>Gets the color of <paramref name="value"/>.</summary>
        public Vector3 At(float value)
        {
            var stops = Stops;
            if (float.IsNaN(value) || value <= stops[0].Value) return stops[0].Color;
            for (int i = 1; i < stops.Length; i++)
            {
                if (value > stops[i].Value) continue;
                float t = (value - stops[i - 1].Value) / (stops[i].Value - stops[i - 1].Value);
                return Vector3.Lerp(stops[i - 1].Color, stops[i].Color, t);
            }
            return stops[^1].Color;
        }
    }

    /// <summary>Vertical wind (m/s): sink blue, still white, lift yellow to red.</summary>
    public static Scale Lift { get; } = new("Vertical wind", "m/s",
    [
        (-3, new(0.10f, 0.25f, 0.95f)), (-1.5f, new(0.30f, 0.60f, 1.00f)), (0, new(0.92f, 0.92f, 0.92f)), (1, new(1.00f, 0.85f, 0.25f)),
        (2.5f, new(1.00f, 0.45f, 0.08f)), (4, new(0.85f, 0.08f, 0.25f)),
    ]);

    /// <summary>Turbulence (m/s): calm green to rough red.</summary>
    public static Scale Turbulence { get; } = new("Turbulence", "m/s",
    [
        (0, new(0.25f, 0.70f, 0.40f)), (0.6f, new(0.85f, 0.90f, 0.30f)), (1.2f, new(1.00f, 0.60f, 0.15f)), (2, new(0.95f, 0.25f, 0.20f)),
        (3, new(0.65f, 0.10f, 0.55f)),
    ]);

    /// <summary>Wind speed (km/h).</summary>
    public static Scale Wind { get; } = new("Wind", "km/h",
    [
        (0, new(0.25f, 0.40f, 0.90f)), (15, new(0.30f, 0.80f, 0.55f)), (30, new(0.95f, 0.85f, 0.25f)), (45, new(1.00f, 0.45f, 0.12f)),
        (65, new(0.80f, 0.10f, 0.35f)),
    ]);

    /// <summary>The share of the time the air rises at 1 m/s or more (%).</summary>
    public static Scale LiftChance { get; } = new("Lift ≥ 1 m/s", "% of the time",
    [
        (0, new(0.55f, 0.55f, 0.60f)), (15, new(0.95f, 0.90f, 0.40f)), (35, new(1.00f, 0.55f, 0.12f)), (60, new(0.85f, 0.10f, 0.25f)),
    ]);

    /// <summary>Warming above the morning sounding (K).</summary>
    public static Scale Temperature { get; } = new("Warmer than the morning", "K",
    [
        (-2, new(0.15f, 0.35f, 0.95f)), (0, new(0.90f, 0.90f, 0.90f)), (2, new(1.00f, 0.60f, 0.15f)), (5, new(0.85f, 0.10f, 0.25f)),
    ]);

    /// <summary>Sensible heat (W/m²).</summary>
    public static Scale Heating { get; } = new("Heating", "W/m²",
    [
        (-40, new(0.25f, 0.40f, 0.90f)), (0, new(0.55f, 0.55f, 0.60f)), (120, new(0.95f, 0.85f, 0.30f)), (250, new(1.00f, 0.45f, 0.12f)),
        (400, new(0.85f, 0.08f, 0.25f)),
    ]);

    /// <summary>Gets the scale of <paramref name="quantity"/>.</summary>
    public static Scale Of(AirflowQuantity quantity) => quantity switch
    {
        AirflowQuantity.Turbulence => Turbulence,
        AirflowQuantity.Wind => Wind,
        AirflowQuantity.LiftChance => LiftChance,
        AirflowQuantity.Temperature => Temperature,
        _ => Lift,
    };

    /// <summary>Gets the value of <paramref name="quantity"/> in a sample, in its scale's unit.</summary>
    public static float Value(AirflowQuantity quantity, ParagliderToolbox.Airflow.AirflowSample sample) => quantity switch
    {
        AirflowQuantity.Turbulence => sample.Turbulence,
        AirflowQuantity.Wind => sample.Wind.Length() * 3.6f,
        AirflowQuantity.LiftChance => sample.LiftChance * 100,
        AirflowQuantity.Temperature => sample.Temperature,
        _ => sample.Wind.Y,
    };

    /// <summary>Gets a color of a land cover on maps (linear RGB).</summary>
    public static Vector3 Of(ParagliderToolbox.Airflow.Surface.LandCover cover)
    {
        uint c = ParagliderToolbox.Airflow.Surface.LandCovers.MapColor(cover);
        return new Vector3((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);
    }
}
