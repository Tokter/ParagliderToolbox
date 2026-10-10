namespace ParagliderToolbox.Terrain;

/// <summary>
/// Colors a terrain by height and slope where there is no imagery (or when asked to): water at sea level, green valleys,
/// alpine meadows, rock on steep slopes and snow high up. The colors are albedo, lit by the renderer.
/// </summary>
public static class ElevationColors
{
    private static readonly (float Height, (float R, float G, float B) Color)[] s_bands =
    [
        (0, (74, 98, 52)),
        (800, (82, 108, 56)),
        (1500, (104, 118, 66)),
        (2100, (128, 124, 88)),
        (2600, (124, 116, 104)),
    ];

    private static readonly (float R, float G, float B) s_rock = (118, 110, 100);
    private static readonly (float R, float G, float B) s_snow = (236, 239, 243);
    private static readonly (float R, float G, float B) s_water = (46, 82, 108);

    /// <summary>Gets the color (RGBA, red in the lowest byte) of a point at <paramref name="height"/> (m) on a slope of <paramref name="slope"/> degrees.</summary>
    public static uint Color(float height, float slope)
    {
        if (height <= 0.5f && slope < 2) return Pack(s_water);
        var color = s_bands[^1].Color;
        for (int i = 0; i < s_bands.Length - 1; i++)
        {
            if (height < s_bands[i + 1].Height)
            {
                float t = Math.Clamp((height - s_bands[i].Height) / (s_bands[i + 1].Height - s_bands[i].Height), 0, 1);
                color = Lerp(s_bands[i].Color, s_bands[i + 1].Color, t);
                break;
            }
        }
        color = Lerp(color, s_rock, SmoothStep(30, 45, slope));
        color = Lerp(color, s_snow, SmoothStep(2500, 2900, height) * (1 - SmoothStep(35, 55, slope)));
        return Pack(color);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static (float R, float G, float B) Lerp((float R, float G, float B) a, (float R, float G, float B) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static uint Pack((float R, float G, float B) c) =>
        (uint)Math.Clamp(c.R + 0.5f, 0, 255) | (uint)Math.Clamp(c.G + 0.5f, 0, 255) << 8 | (uint)Math.Clamp(c.B + 0.5f, 0, 255) << 16 | 0xFF000000u;
}
