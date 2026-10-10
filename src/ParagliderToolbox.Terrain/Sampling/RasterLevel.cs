using ParagliderToolbox.Terrain.Data;

namespace ParagliderToolbox.Terrain.Sampling;

/// <summary>
/// One resolution of a georeferenced raster, read in blocks (a COG's tiles, a map service's tiles): pixel (col, row)
/// has its center at (<see cref="X0"/> + col·<see cref="Dx"/>, <see cref="Y0"/> − row·<see cref="Dy"/>) in its
/// coordinate system.
/// </summary>
/// <typeparam name="T">The pixel type: heights (<see cref="float"/>, NaN for no data) or colors (RGBA <see cref="uint"/>, alpha 0 for no data).</typeparam>
public abstract class RasterLevel<T>
{
    /// <summary>Initializes the level's geometry.</summary>
    protected RasterLevel(double x0, double y0, double dx, double dy, int width, int height, int blockWidth, int blockHeight)
    {
        X0 = x0;
        Y0 = y0;
        Dx = dx;
        Dy = dy;
        Width = width;
        Height = height;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
    }

    /// <summary>Gets the x of the first pixel's center.</summary>
    public double X0 { get; }

    /// <summary>Gets the y of the first pixel's center.</summary>
    public double Y0 { get; }

    /// <summary>Gets the pixel width.</summary>
    public double Dx { get; }

    /// <summary>Gets the pixel height (rows run towards smaller y).</summary>
    public double Dy { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the width of a block in pixels.</summary>
    public int BlockWidth { get; }

    /// <summary>Gets the height of a block in pixels.</summary>
    public int BlockHeight { get; }

    /// <summary>Gets the number of blocks across.</summary>
    public int BlocksAcross => (Width + BlockWidth - 1) / BlockWidth;

    /// <summary>Gets the number of blocks down.</summary>
    public int BlocksDown => (Height + BlockHeight - 1) / BlockHeight;

    /// <summary>Gets the left edge of the pixels.</summary>
    public double MinX => X0 - Dx / 2;

    /// <summary>Gets the right edge of the pixels.</summary>
    public double MaxX => X0 + (Width - 0.5) * Dx;

    /// <summary>Gets the top edge of the pixels.</summary>
    public double MaxY => Y0 + Dy / 2;

    /// <summary>Gets the bottom edge of the pixels.</summary>
    public double MinY => Y0 - (Height - 0.5) * Dy;

    /// <summary>Gets a key that identifies the level among all (for the block cache).</summary>
    public abstract string Key { get; }

    /// <summary>Loads a block: <see cref="BlockWidth"/> × <see cref="BlockHeight"/> pixels, row by row; <c>null</c> when it has no data.</summary>
    public abstract Task<T[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken);
}

/// <summary>An image (full resolution or overview) of a GeoTIFF read as heights.</summary>
public sealed class GeoTiffElevationLevel(GeoTiff tiff, int image) : RasterLevel<float>(
    tiff.FirstPixelCenter(image).X, tiff.FirstPixelCenter(image).Y, tiff.PixelSize(image).Width, tiff.PixelSize(image).Height,
    tiff.Images[image].Width, tiff.Images[image].Height, tiff.Images[image].BlockWidth, tiff.Images[image].BlockHeight)
{
    /// <inheritdoc/>
    public override string Key { get; } = tiff.Name + "#" + image;

    /// <inheritdoc/>
    public override async Task<float[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken) =>
        await tiff.ReadElevationBlockAsync(image, blockX, blockY, cancellationToken);
}

/// <summary>An image (full resolution or overview) of a GeoTIFF read as colors.</summary>
public sealed class GeoTiffColorLevel(GeoTiff tiff, int image) : RasterLevel<uint>(
    tiff.FirstPixelCenter(image).X, tiff.FirstPixelCenter(image).Y, tiff.PixelSize(image).Width, tiff.PixelSize(image).Height,
    tiff.Images[image].Width, tiff.Images[image].Height, tiff.Images[image].BlockWidth, tiff.Images[image].BlockHeight)
{
    /// <inheritdoc/>
    public override string Key { get; } = tiff.Name + "#" + image;

    /// <inheritdoc/>
    public override async Task<uint[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken) =>
        await tiff.ReadColorBlockAsync(image, blockX, blockY, cancellationToken);
}
