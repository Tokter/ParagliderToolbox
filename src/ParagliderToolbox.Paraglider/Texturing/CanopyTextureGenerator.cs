using System.Numerics;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using SkiaSharp;

namespace ParagliderToolbox.Paraglider.Texturing;

/// <summary>An RGBA8 image, row 0 at the top.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Pixels">The pixels, 4 bytes each.</param>
public sealed record TextureImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Encodes the image as PNG.</summary>
    public byte[] EncodePng()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, Pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>
/// Paints the canopy textures in the canopy's UV layout (u = (η + 1) / 2 along the flat span, v = (t + 1) / 2 around the
/// profile from the upper trailing edge over the nose to the lower trailing edge): a base color with the color design,
/// the panel seams and their stitching, the reinforcement tapes and the brand text; and a tangent-space normal map
/// (OpenGL convention) with the seam ridges and the ripstop weave.
/// </summary>
public static class CanopyTextureGenerator
{
    /// <summary>Paints the base color and the normal map of <paramref name="shape"/>'s canopy at <paramref name="size"/> pixels square.</summary>
    public static (TextureImage BaseColor, TextureImage Normal) Generate(GliderShape shape, int size)
    {
        size = Math.Clamp(size, 256, 8192);
        var design = shape.Design;
        var primary = ParseColor(design.PrimaryColor, new SKColor(0x15, 0x65, 0xC0));
        var secondary = ParseColor(design.SecondaryColor, SKColors.WhiteSmoke);
        var accent = ParseColor(design.AccentColor, new SKColor(0xFF, 0x6F, 0x00));
        var lower = ParseColor(design.LowerSurfaceColor, new SKColor(0xEC, 0xEF, 0xF1));
        var ribs = shape.RibPositions;
        var ribU = ribs.Select(e => (e + 1) / 2).ToArray();
        double[] upperSeams = [0.3, 0.55, 0.8];
        double[] lowerSeams = [0.45, 0.8];

        var color = new byte[size * size * 4];
        var height = new float[size * size];
        double uPerPixel = 1.0 / size;

        Parallel.For(0, size, py =>
        {
            double v = (py + 0.5) / size;
            double t = 2 * v - 1;
            bool upper = t < 0;
            double x = GliderShape.ChordFraction(t);
            // How many pixels one unit of x covers here (for seam widths that stay constant on the fabric).
            double dxPerPixel = Math.Abs(GliderShape.ChordFraction(2 * ((py + 1.5) / size) - 1) - GliderShape.ChordFraction(2 * ((py - 0.5) / size) - 1)) / 2;

            for (int px = 0; px < size; px++)
            {
                double u = (px + 0.5) / size;
                double eta = 2 * u - 1;
                double chord = shape.Chord(eta);
                var c = upper ? PatternColor(design.Pattern, Math.Abs(eta), x, primary, secondary, accent) : lower;
                float h = 0;

                // Reinforcement tapes along the nose and the trailing edge.
                if (x > 0.985) c = Shade(c, 0.78);
                if (x < 0.025) c = Shade(c, 0.93);

                // Rib seams: a darker line with stitches; a raised ridge in the normal map.
                double ribDistance = NearestDistance(ribU, u) / uPerPixel; // pixels
                double ribWidth = Math.Max(1.2, 0.006 / (2 * shape.HalfSpan) / uPerPixel);
                if (ribDistance < ribWidth * 2.5)
                {
                    double w = Math.Clamp(1 - ribDistance / (ribWidth * 2.5), 0, 1);
                    c = Shade(c, 1 - 0.18 * w);
                    h += (float)(w * w);
                    double stitch = x * chord / 0.006;
                    if (ribDistance > ribWidth * 0.8 && ribDistance < ribWidth * 1.8 && stitch - Math.Floor(stitch) < 0.55) c = Shade(c, 0.82);
                }

                // Spanwise panel seams.
                foreach (double seam in upper ? upperSeams : lowerSeams)
                {
                    double d = Math.Abs(x - seam) / Math.Max(dxPerPixel, 1e-9);
                    double seamWidth = Math.Max(1.2, 0.006 / chord / Math.Max(dxPerPixel, 1e-9));
                    if (d < seamWidth * 2.5)
                    {
                        double w = 1 - d / (seamWidth * 2.5);
                        c = Shade(c, 1 - 0.14 * w);
                        h += (float)(0.8 * w * w);
                    }
                }

                // Ripstop weave: a raised grid every 6 mm.
                double sm = eta * shape.HalfSpan / 0.006, cm = x * chord / 0.006;
                double grid = Math.Max(Ripstop(sm), Ripstop(cm));
                h += (float)(0.15 * grid);
                c = Shade(c, 1 - 0.025 * grid);

                int o = (py * size + px) * 4;
                color[o] = c.Red;
                color[o + 1] = c.Green;
                color[o + 2] = c.Blue;
                color[o + 3] = 255;
                height[py * size + px] = h;
            }
        });

        DrawBrandText(color, size, design.BrandText, lower);
        return (new TextureImage(size, size, color), new TextureImage(size, size, NormalsFromHeight(height, size, strength: 1.5f)));
    }

    private static SKColor PatternColor(CanopyPattern pattern, double e, double x, SKColor primary, SKColor secondary, SKColor accent)
    {
        switch (pattern)
        {
            case CanopyPattern.LeadingEdgeBand:
                if (x < 0.16 + 0.05 * e) return secondary;
                if (x < 0.19 + 0.05 * e) return accent;
                return primary;
            case CanopyPattern.Chevron:
            {
                double start = 0.18 + 0.5 * e;
                if (x > start && x < start + 0.14) return secondary;
                if (x > start + 0.14 && x < start + 0.18) return accent;
                return primary;
            }
            case CanopyPattern.Stripes:
            {
                double band = e * 6;
                return (int)band % 2 == 1 ? secondary : primary;
            }
            case CanopyPattern.Tips:
                if (e > 0.78) return accent;
                if (e > 0.72) return secondary;
                return primary;
            default:
                return primary;
        }
    }

    private static double Ripstop(double coordinate)
    {
        double f = coordinate - Math.Floor(coordinate);
        return f < 0.12 ? 1 - f / 0.12 : 0;
    }

    private static double NearestDistance(double[] sorted, double value)
    {
        int i = Array.BinarySearch(sorted, value);
        if (i >= 0) return 0;
        i = ~i;
        double best = double.MaxValue;
        if (i < sorted.Length) best = sorted[i] - value;
        if (i > 0) best = Math.Min(best, value - sorted[i - 1]);
        return best;
    }

    private static SKColor Shade(SKColor c, double factor) =>
        new((byte)Math.Clamp(c.Red * factor, 0, 255), (byte)Math.Clamp(c.Green * factor, 0, 255), (byte)Math.Clamp(c.Blue * factor, 0, 255), c.Alpha);

    /// <summary>Parses a hex color, or returns <paramref name="fallback"/>.</summary>
    public static SKColor ParseColor(string? text, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(text) && SKColor.TryParse(text.Trim(), out var color) ? color : fallback;

    // The brand text on the lower surface, read from below.
    private static void DrawBrandText(byte[] pixels, int size, string? text, SKColor background)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using (var canvas = new SKCanvas(bitmap))
        {
            float vTop = (float)((GliderShape.ProfileParameter(0.3, upper: false) + 1) / 2 * size);
            float vBottom = (float)((GliderShape.ProfileParameter(0.62, upper: false) + 1) / 2 * size);
            float bandHeight = vBottom - vTop;
            float bandWidth = size * 0.36f;
            bool dark = background.Red * 0.3 + background.Green * 0.59 + background.Blue * 0.11 > 140;
            using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), bandHeight * 0.8f);
            using var paint = new SKPaint { IsAntialias = true, Color = dark ? new SKColor(0x26, 0x32, 0x38) : SKColors.White };
            float measured = font.MeasureText(text.Trim());
            float scaleX = Math.Min(1, bandWidth / Math.Max(1, measured)) * 1.0f;
            canvas.Save();
            canvas.Translate(size / 2f, vTop + bandHeight / 2);
            canvas.Scale(scaleX, 1);
            canvas.DrawText(text.Trim(), -measured / 2, bandHeight * 0.28f, SKTextAlign.Left, font, paint);
            canvas.Restore();
        }
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
    }

    private static byte[] NormalsFromHeight(float[] height, int size, float strength)
    {
        var normal = new byte[size * size * 4];
        Parallel.For(0, size, y =>
        {
            int y0 = Math.Max(0, y - 1), y1 = Math.Min(size - 1, y + 1);
            for (int x = 0; x < size; x++)
            {
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(size - 1, x + 1);
                float dx = height[y * size + x1] - height[y * size + x0];
                float dy = height[y1 * size + x] - height[y0 * size + x];
                var n = Vector3.Normalize(new Vector3(-dx * strength, dy * strength, 1));
                int o = (y * size + x) * 4;
                normal[o] = (byte)((n.X * 0.5f + 0.5f) * 255);
                normal[o + 1] = (byte)((n.Y * 0.5f + 0.5f) * 255);
                normal[o + 2] = (byte)((n.Z * 0.5f + 0.5f) * 255);
                normal[o + 3] = 255;
            }
        });
        return normal;
    }
}
