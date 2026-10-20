namespace ParagliderToolbox.Terrain.Features;

/// <summary>What covers the ground at a point of the aerial image, as <see cref="GroundCovers.Classify"/> reads it.</summary>
public enum GroundCover : byte
{
    /// <summary>No data.</summary>
    None,

    /// <summary>The crown of a tree.</summary>
    Tree,

    /// <summary>Bushes and young trees (up to a few meters).</summary>
    Shrub,

    /// <summary>Meadows, lawns, pastures.</summary>
    Grass,

    /// <summary>Bare or plowed soil, dirt.</summary>
    Soil,

    /// <summary>Asphalt and concrete: roads, squares, parking lots.</summary>
    Paved,

    /// <summary>Gravel and light dirt: tracks, paths, gravel pits.</summary>
    Gravel,

    /// <summary>Bare rock, cliffs and scree.</summary>
    Rock,

    /// <summary>A roof.</summary>
    Building,

    /// <summary>Lakes and rivers.</summary>
    Water,

    /// <summary>Snow and ice.</summary>
    Snow,

    /// <summary>Too dark to tell (deep shadow); resolved from the surroundings.</summary>
    Shadow,
}

/// <summary>What the classifier reads at a point: the image's color and texture, and the heights there.</summary>
/// <param name="Rgba">The aerial image's color (red in the lowest byte; alpha 0 for none).</param>
/// <param name="Texture">How much the brightness varies within about a meter (standard deviation, 0–1).</param>
/// <param name="Slope">The ground's slope (degrees).</param>
/// <param name="Altitude">The ground's height above sea level (m).</param>
/// <param name="ObjectHeight">The height of what stands on the ground (surface less terrain model, m), or NaN without a surface model.</param>
/// <param name="Roughness">How much that height varies within about a meter (m): crowns are rough, roofs smooth; NaN without a surface model.</param>
public readonly record struct CoverSample(uint Rgba, float Texture, float Slope, float Altitude, float ObjectHeight, float Roughness);

/// <summary>Classifies the ground cover of a point of an aerial image (see <see cref="Classify"/>).</summary>
public static class GroundCovers
{
    /// <summary>The colors of the classes in pictures of the ground cover (RGBA, red in the lowest byte).</summary>
    public static IReadOnlyList<uint> MapColors { get; } =
    [
        0x00000000, // None
        0xFF1E5A23, // Tree
        0xFF3C9646, // Shrub
        0xFF7DD28C, // Grass
        0xFF285A8C, // Soil
        0xFF505050, // Paved
        0xFF96C8DC, // Gravel
        0xFFA0A0A0, // Rock
        0xFF2828C8, // Building
        0xFFC86428, // Water
        0xFFFFFFFF, // Snow
        0xFF140A0A, // Shadow
    ];

    /// <summary>The height (m) above which something standing on the ground is a tree or a building.</summary>
    public const float TallObject = 2.5f;

    /// <summary>
    /// Gets whether a color (RGBA, red in the lowest byte) is vegetation green: more green than red and blue (excess green
    /// over 4%) and saturated (gray gravel and rock with a faint green tint aren't grass).
    /// </summary>
    public static bool IsGreen(uint rgba)
    {
        float r = (rgba & 0xFF) / 255f, g = ((rgba >> 8) & 0xFF) / 255f, b = ((rgba >> 16) & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float sum = r + g + b;
        float excessGreen = sum > 0.02f ? (2 * g - r - b) / sum : 0;
        return excessGreen > 0.04f && g >= r && max > 0 && (max - min) / max > 0.14f;
    }

    /// <summary>
    /// Classifies a point. With a surface model, what stands above <see cref="TallObject"/> is a tree (green or rough) or
    /// a building (smooth and not green), lower green is shrub; on the ground the color decides: white is snow (above
    /// 1600 m), flat and bluish (or very dark and smooth) water, saturated green grass (without a surface model: dark
    /// and textured green is tree crowns), gray and rough or steep rock, gray and smooth asphalt (darker) or gravel
    /// (lighter), light beige gravel, brown soil, very dark shadow.
    /// </summary>
    public static GroundCover Classify(in CoverSample s)
    {
        if (s.Rgba >> 24 == 0) return GroundCover.None;
        float r = (s.Rgba & 0xFF) / 255f, g = ((s.Rgba >> 8) & 0xFF) / 255f, b = ((s.Rgba >> 16) & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        float saturation = max > 0 ? (max - min) / max : 0;
        bool green = IsGreen(s.Rgba);
        bool measured = !float.IsNaN(s.ObjectHeight);

        if (measured && s.ObjectHeight > TallObject)
        {
            if (green || luminance < 0.14f) return GroundCover.Tree;
            // Roofs are smooth planes; crowns stay rough even where shadow or autumn takes their green.
            return s.Roughness > 0.6f ? GroundCover.Tree : GroundCover.Building;
        }
        if (measured && s.ObjectHeight > 0.7f && green) return GroundCover.Shrub;

        // White: snow up high (gravel and pale rock are as bright as 0.75 in summer images).
        if (luminance > 0.84f && saturation < 0.12f && s.Altitude > 1600) return GroundCover.Snow;
        if (luminance < 0.07f) return GroundCover.Shadow;
        if (s.Slope < 3 && s.Texture < 0.025f && ((b >= g * 0.95f && b > r * 1.08f && luminance < 0.55f) || luminance < 0.11f)) return GroundCover.Water;
        if (green)
        {
            // Without heights, crowns are the dark, textured green (sunlit tops and shadowed gaps).
            if (!measured && luminance < 0.3f && s.Texture > 0.05f) return GroundCover.Tree;
            return GroundCover.Grass;
        }
        if (luminance < 0.13f) return GroundCover.Shadow;
        if (saturation < 0.16f)
        {
            // Gray: rough or steep is rock; smooth is asphalt (darker) or gravel (light). Measured in summer images:
            // village streets 0.63–0.66, limestone gravel roads 0.70–0.77.
            if (s.Texture > 0.07f || s.Slope > 35) return GroundCover.Rock;
            return luminance > 0.68f ? GroundCover.Gravel : GroundCover.Paved;
        }
        if (r >= g && g >= b * 0.95f) return luminance > 0.45f ? GroundCover.Gravel : GroundCover.Soil;
        return saturation < 0.25f ? (s.Texture > 0.07f ? GroundCover.Rock : GroundCover.Paved) : GroundCover.Grass;
    }
}
