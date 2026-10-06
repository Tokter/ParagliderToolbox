namespace ParagliderToolbox.Paraglider.Design;

/// <summary>
/// The mesh settings a model is generated with, resolved from <see cref="GliderDesign.MeshDetail"/>: the preset levels
/// override the design's explicit mesh settings, <see cref="MeshDetail.Custom"/> uses them.
/// </summary>
/// <param name="SpanwiseSegmentsPerCell">Segments across each cell (when <paramref name="CellsPerSegment"/> is 1).</param>
/// <param name="CellsPerSegment">Cells one skin segment spans; more than 1 builds the low poly skin.</param>
/// <param name="ChordwiseSegments">Segments along each of the upper and lower surface.</param>
/// <param name="Ribs">Whether the internal ribs are generated.</param>
/// <param name="LineSides">Sides of the line tubes; 2 draws flat ribbons.</param>
/// <param name="LineSegmentLength">Length of the segments along the lines (m); 0 draws one straight segment per line.</param>
/// <param name="HardwareSegments">Segments around the maillons, pulleys and carabiners; below 6 draws simple shapes.</param>
/// <param name="TextureSize">The canopy texture size (pixels along the span).</param>
public readonly record struct MeshSettings(
    int SpanwiseSegmentsPerCell,
    int CellsPerSegment,
    int ChordwiseSegments,
    bool Ribs,
    int LineSides,
    double LineSegmentLength,
    int HardwareSegments,
    int TextureSize)
{
    /// <summary>Gets whether the skin spans several cells per segment (no ballooning, inlets or ribs between).</summary>
    public bool IsLowPoly => CellsPerSegment > 1;

    /// <summary>Gets whether the hardware is drawn as simple low poly shapes.</summary>
    public bool SimpleHardware => HardwareSegments < 6;

    /// <summary>Gets the settings a design is generated with.</summary>
    public static MeshSettings FromDesign(GliderDesign design) => FromDetail(design.MeshDetail, design);

    /// <summary>Gets the settings of <paramref name="detail"/> for <paramref name="design"/> (its explicit settings for Custom).</summary>
    public static MeshSettings FromDetail(MeshDetail detail, GliderDesign design) => detail switch
    {
        // About 12–16 skin segments along the span, whatever the cell count: a few hundred triangles in all.
        MeshDetail.LowPoly => new(1, Math.Max(2, (int)Math.Round(Math.Max(2, design.CellCount) / 14.0)), 5, false, 2, 0, 0, 1024),
        MeshDetail.Medium => new(2, 1, 28, true, 3, 2.5, 12, 2048),
        MeshDetail.High => new(10, 1, 110, true, 6, 0.5, 24, 4096),
        _ => new(Math.Max(1, design.SpanwiseSegmentsPerCell), Math.Max(1, design.CellsPerSegment), Math.Max(2, design.ChordwiseSegments),
            design.GenerateRibs, Math.Clamp(design.LineSides, 2, 16), Math.Max(0, design.LineSegmentLength), Math.Max(0, design.HardwareSegments),
            design.TextureSize),
    };

    /// <summary>Writes the settings into the design's explicit mesh settings (so switching to Custom starts from them).</summary>
    public void ApplyTo(GliderDesign design)
    {
        design.SpanwiseSegmentsPerCell = SpanwiseSegmentsPerCell;
        design.CellsPerSegment = CellsPerSegment;
        design.ChordwiseSegments = ChordwiseSegments;
        design.GenerateRibs = Ribs;
        design.LineSides = LineSides;
        design.LineSegmentLength = LineSegmentLength;
        design.HardwareSegments = HardwareSegments;
        design.TextureSize = TextureSize;
    }
}
