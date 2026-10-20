namespace ParagliderToolbox.Terrain.Features;

/// <summary>Image operations on rasters stored row by row (<c>width × height</c> values) that the feature detector uses.</summary>
public static class Rasters
{
    /// <summary>The mean of the values within <paramref name="radius"/> pixels (a square window, clipped at the edges), from an integral image.</summary>
    public static float[] BoxMean(float[] values, int width, int height, int radius)
    {
        var integral = new double[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            double row = 0;
            for (int x = 0; x < width; x++)
            {
                row += values[y * width + x];
                integral[(y + 1) * (width + 1) + x + 1] = integral[y * (width + 1) + x + 1] + row;
            }
        }
        var mean = new float[values.Length];
        Parallel.For(0, height, y =>
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(height, y + radius + 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(width, x + radius + 1);
                double total = integral[y1 * (width + 1) + x1] - integral[y0 * (width + 1) + x1] - integral[y1 * (width + 1) + x0] + integral[y0 * (width + 1) + x0];
                mean[y * width + x] = (float)(total / ((x1 - x0) * (y1 - y0)));
            }
        });
        return mean;
    }

    /// <summary>The standard deviation of the values within <paramref name="radius"/> pixels.</summary>
    public static float[] LocalDeviation(float[] values, int width, int height, int radius)
    {
        var squares = new float[values.Length];
        for (int i = 0; i < values.Length; i++) squares[i] = values[i] * values[i];
        var mean = BoxMean(values, width, height, radius);
        var meanSquares = BoxMean(squares, width, height, radius);
        var deviation = new float[values.Length];
        for (int i = 0; i < values.Length; i++) deviation[i] = MathF.Sqrt(MathF.Max(0, meanSquares[i] - mean[i] * mean[i]));
        return deviation;
    }

    /// <summary>A Gaussian blur with standard deviation <paramref name="sigma"/> pixels (separable, edges clamped).</summary>
    public static float[] Blur(float[] values, int width, int height, float sigma)
    {
        if (sigma <= 0) return (float[])values.Clone();
        int radius = (int)MathF.Ceiling(3 * sigma);
        var kernel = new float[2 * radius + 1];
        float total = 0;
        for (int k = -radius; k <= radius; k++) total += kernel[k + radius] = MathF.Exp(-k * k / (2 * sigma * sigma));
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
        var across = new float[values.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] * values[y * width + Math.Clamp(x + k, 0, width - 1)];
                across[y * width + x] = sum;
            }
        });
        var result = new float[values.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] * across[Math.Clamp(y + k, 0, height - 1) * width + x];
                result[y * width + x] = sum;
            }
        });
        return result;
    }

    /// <summary>The slope (degrees) of heights <paramref name="spacing"/> meters apart, from central differences.</summary>
    public static float[] Slope(float[] heights, int width, int height, double spacing)
    {
        var slope = new float[heights.Length];
        Parallel.For(0, height, y =>
        {
            int ya = Math.Max(0, y - 1), yb = Math.Min(height - 1, y + 1);
            for (int x = 0; x < width; x++)
            {
                int xa = Math.Max(0, x - 1), xb = Math.Min(width - 1, x + 1);
                double dx = (heights[y * width + xb] - heights[y * width + xa]) / ((xb - xa) * spacing);
                double dz = (heights[yb * width + x] - heights[ya * width + x]) / ((yb - ya) * spacing);
                slope[y * width + x] = float.IsFinite((float)dx) && float.IsFinite((float)dz) ? (float)(Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI) : 0;
            }
        });
        return slope;
    }

    /// <summary>
    /// How strongly each pixel lies on a bright line about <paramref name="sigma"/> pixels wide (a ridge): from the
    /// Hessian of the values blurred by <paramref name="sigma"/>, −σ²·λ₁ where the strongest curvature λ₁ is negative
    /// and the curvature along the line (λ₂) is less than half as strong; 0 elsewhere. Lines darker than their
    /// surroundings: pass the values negated.
    /// </summary>
    public static float[] Ridges(float[] values, int width, int height, float sigma)
    {
        var blurred = Blur(values, width, height, sigma);
        var ridge = new float[values.Length];
        float scale = sigma * sigma;
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                float dxx = blurred[i + 1] - 2 * blurred[i] + blurred[i - 1];
                float dyy = blurred[i + width] - 2 * blurred[i] + blurred[i - width];
                float dxy = (blurred[i + width + 1] - blurred[i + width - 1] - blurred[i - width + 1] + blurred[i - width - 1]) / 4;
                float mean = (dxx + dyy) / 2, spread = MathF.Sqrt((dxx - dyy) * (dxx - dyy) / 4 + dxy * dxy);
                float across = mean - spread, along = mean + spread;
                if (across >= 0 || MathF.Abs(along) > 0.5f * -across) continue;
                ridge[i] = -across * scale;
            }
        });
        return ridge;
    }

    /// <summary>The luminance (0–1) of RGBA colors (red in the lowest byte).</summary>
    public static float[] Luminance(uint[] colors)
    {
        var luminance = new float[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            uint c = colors[i];
            luminance[i] = (0.2126f * (c & 0xFF) + 0.7152f * ((c >> 8) & 0xFF) + 0.0722f * ((c >> 16) & 0xFF)) / 255f;
        }
        return luminance;
    }
}
