using System.Diagnostics;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Rigging;
using ParagliderToolbox.Paraglider.Texturing;

namespace ParagliderToolbox.Paraglider;

/// <summary>Options for <see cref="GliderGenerator.Generate"/>.</summary>
/// <param name="TextureSize">The canopy texture size; 0 uses the size of the design's <see cref="MeshSettings"/>, −1 skips the textures.</param>
public readonly record struct GenerateOptions(int TextureSize = 0);

/// <summary>A generated paraglider: the high resolution parts, the textures, the line plan and the physics proxy with the skin binding.</summary>
public sealed class GliderModel
{
    public required GliderDesign Design { get; init; }
    public required GliderShape Shape { get; init; }
    public required RiggingLayout Rigging { get; init; }
    public required List<MeshPart> Parts { get; init; }
    public required ProxyBuild Proxy { get; init; }

    /// <summary>Gets the skin weights of each part's vertices (same order as <see cref="Parts"/>).</summary>
    public required List<SkinWeights[]> Skin { get; init; }

    /// <summary>
    /// Gets the canopy nodes the rigging hangs from, the joints after the node joints (see
    /// <see cref="SkinBinder.Attachments"/>); pass them to <see cref="ProxyDeformer"/>.
    /// </summary>
    public int[] SkinAttachments { get; init; } = [];

    public TextureImage? BaseColor { get; init; }
    public TextureImage? NormalMap { get; init; }

    /// <summary>Gets how long the generation took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Gets the part with <paramref name="material"/>, or null.</summary>
    public MeshPart? Part(GliderMaterial material) => Parts.FirstOrDefault(p => p.Material == material);

    /// <summary>Gets the number of triangles of all parts.</summary>
    public int TriangleCount => Parts.Sum(p => p.TriangleCount);
}

/// <summary>Generates the model of a design: shape, line plan, high resolution mesh, textures, proxy and skin.</summary>
public static class GliderGenerator
{
    /// <summary>Generates the model of <paramref name="design"/> (a snapshot; don't change it meanwhile).</summary>
    /// <exception cref="FormatException">The custom airfoil can't be read.</exception>
    public static GliderModel Generate(GliderDesign design, GenerateOptions options = default, CancellationToken cancellation = default)
    {
        var watch = Stopwatch.StartNew();
        var meshSettings = MeshSettings.FromDesign(design);
        var shape = new GliderShape(design);
        var rigging = RiggingLayout.Build(shape);
        cancellation.ThrowIfCancellationRequested();

        var canopy = new CanopyBuilder(shape, rigging);
        var parts = new List<MeshPart> { canopy.BuildCanopy() };
        if (meshSettings.Ribs) parts.Add(canopy.BuildRibs());
        cancellation.ThrowIfCancellationRequested();

        if (design.GenerateRigging)
        {
            var rig = new RiggingBuilder(rigging, design);
            parts.Add(rig.BuildLines());
            parts.Add(rig.BuildRisers());
            parts.Add(rig.BuildHardware());
            parts.Add(rig.BuildToggles());
        }
        cancellation.ThrowIfCancellationRequested();

        var proxy = ProxyBuilder.Build(shape, rigging);
        var binder = new SkinBinder(proxy, rigging);
        var skin = parts.Select(binder.Bind).ToList();
        cancellation.ThrowIfCancellationRequested();

        TextureImage? baseColor = null, normal = null;
        int size = options.TextureSize == 0 ? meshSettings.TextureSize : options.TextureSize;
        if (size > 0) (baseColor, normal) = CanopyTextureGenerator.Generate(shape, size);

        return new GliderModel
        {
            Design = design,
            Shape = shape,
            Rigging = rigging,
            Parts = parts,
            Proxy = proxy,
            Skin = skin,
            SkinAttachments = binder.Attachments,
            BaseColor = baseColor,
            NormalMap = normal,
            Elapsed = watch.Elapsed,
        };
    }
}
