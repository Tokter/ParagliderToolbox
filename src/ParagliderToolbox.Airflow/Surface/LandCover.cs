namespace ParagliderToolbox.Airflow.Surface;

/// <summary>What covers the ground, as far as heating the air goes.</summary>
public enum LandCover : byte
{
    /// <summary>Grass: alpine meadows and pastures, moist; the default without imagery.</summary>
    Meadow,

    /// <summary>Forest: dark, absorbs well but evaporates much and is rough.</summary>
    Forest,

    /// <summary>Fields, bare soil, dry grass: the classic thermal triggers.</summary>
    Field,

    /// <summary>Rock and scree: dry, heats up most.</summary>
    Rock,

    /// <summary>Towns, roads and gravel: dry and warm.</summary>
    Urban,

    /// <summary>Lakes and rivers: store the heat, the air above stays cool.</summary>
    Water,

    /// <summary>Snow and glaciers: reflect most of the sunlight.</summary>
    Snow,
}

/// <summary>How a land cover turns sunlight into heat.</summary>
/// <param name="Albedo">The share of the sunlight reflected.</param>
/// <param name="SensibleShare">The share of the available energy (net radiation minus what goes into the ground) that heats the air; the rest evaporates water.</param>
/// <param name="GroundShare">The share of the net radiation that goes into the ground (or the water).</param>
/// <param name="Roughness">The aerodynamic roughness length (m), which sets the friction of the wind.</param>
public readonly record struct SurfaceProperties(double Albedo, double SensibleShare, double GroundShare, double Roughness);

/// <summary>The land covers' properties and the classification of aerial image colors into them.</summary>
public static class LandCovers
{
    private static readonly SurfaceProperties[] s_properties =
    [
        new(0.20, 0.35, 0.10, 0.05),   // Meadow
        new(0.12, 0.38, 0.05, 1.00),   // Forest
        new(0.17, 0.62, 0.15, 0.10),   // Field
        new(0.20, 0.80, 0.25, 0.05),   // Rock
        new(0.15, 0.72, 0.30, 0.80),   // Urban
        new(0.07, 0.10, 0.70, 0.0002), // Water
        new(0.75, 0.30, 0.20, 0.001),  // Snow
    ];

    // Map colors (RGBA, red in the lowest byte), for legends.
    private static readonly uint[] s_colors =
    [
        0xFF5AC07Au, 0xFF2E6B2Eu, 0xFF4AA6D9u, 0xFF8C8C8Cu, 0xFF5A50C0u, 0xFFD08A30u, 0xFFF4F4F4u,
    ];

    /// <summary>Gets the properties of <paramref name="cover"/>.</summary>
    public static SurfaceProperties Properties(LandCover cover) => s_properties[(int)cover];

    /// <summary>Gets a color for <paramref name="cover"/> on maps (RGBA, red in the lowest byte).</summary>
    public static uint MapColor(LandCover cover) => s_colors[(int)cover];

    /// <summary>
    /// Classifies an aerial image color (RGBA, red in the lowest byte; alpha 0 for none) at a point of the terrain: bright
    /// and gray is snow, blue and flat is water, green is forest (dark) or meadow, brown is fields, gray is rock (steep or
    /// high) or towns.
    /// </summary>
    /// <param name="rgba">The color.</param>
    /// <param name="slope">The slope there (degrees).</param>
    /// <param name="height">The height there (m above sea level).</param>
    /// <param name="relief">
    /// How much the ground varies around the point (m, highest minus lowest within about 100 m), or NaN when unknown:
    /// lakes are flat to a few centimeters in elevation models, so flat and bluish is water even where shallow water or
    /// glacial silt makes it bright turquoise.
    /// </param>
    public static LandCover Classify(uint rgba, double slope, double height, double relief = double.NaN)
    {
        if (rgba >> 24 == 0) return height > 3000 ? LandCover.Rock : LandCover.Meadow;
        double r = (rgba & 0xFF) / 255.0, g = ((rgba >> 8) & 0xFF) / 255.0, b = ((rgba >> 16) & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double saturation = max > 0 ? (max - min) / max : 0;
        double excessGreen = 2 * g - r - b;

        if (luminance > 0.68 && saturation < 0.2) return LandCover.Snow;
        if (relief < 0.15 && b >= g * 0.85 && b > r * 1.05) return LandCover.Water;
        if (slope < 4 && ((b >= g * 0.95 && b > r * 1.1 && luminance < 0.6) || luminance < 0.09)) return LandCover.Water;
        if (excessGreen > 0.035 && g >= r) return luminance < 0.27 ? LandCover.Forest : LandCover.Meadow;
        if (r > g * 1.02 && r > b * 1.15 && saturation > 0.12) return LandCover.Field;
        if (saturation < 0.22) return slope > 22 || height > 2300 ? LandCover.Rock : LandCover.Urban;
        return LandCover.Meadow;
    }
}
