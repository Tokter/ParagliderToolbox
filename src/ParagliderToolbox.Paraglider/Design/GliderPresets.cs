using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Paraglider.Design;

/// <summary>The EN 926-2 certification classes the presets describe, from school wings to competition wings.</summary>
public enum WingClass
{
    /// <summary>EN-A: school and leisure wings, the most passive behavior.</summary>
    EnA,
    /// <summary>Low EN-B: the first wing after school, forgiving cross-country.</summary>
    EnBLow,
    /// <summary>High EN-B: performance intermediates for experienced cross-country pilots.</summary>
    EnBHigh,
    /// <summary>EN-C: sport two-liners, demanding active piloting.</summary>
    EnC,
    /// <summary>EN-D: high aspect ratio two-liners for competitions and expert cross-country.</summary>
    EnD,
}

/// <summary>What the new-paraglider choices show about a class.</summary>
/// <param name="Name">The short name, e.g. "Low EN-B".</param>
/// <param name="Title">What the class is for, e.g. "First cross-country wing".</param>
/// <param name="Description">One sentence about its character and performance.</param>
public sealed record WingClassInfo(string Name, string Title, string Description);

/// <summary>
/// Complete designs for the certification classes, sized for about 85–100 kg all up (the "M"/"MS" size), at a chosen
/// level of detail (see <see cref="MeshDetail"/>).
/// </summary>
/// <remarks>
/// <para>
/// The numbers follow current production wings of each class (manufacturer data, e.g. Ozone Mojo 6: EN-A, 40 cells,
/// flat aspect ratio 4.91; Rush 6: EN-B, 62 cells, 5.7; Nova Mentor 7: high EN-B 2.5-liner, 66 cells; Ozone Delta 4:
/// EN-C, 66 cells, 6.05; Zeno 2: EN-D two-liner, 78 cells, 6.9, projected span 0.78 of the flat span) and the trends
/// across the classes: the aspect ratio and cell count rise, profiles get thinner with deeper shark noses and more
/// reflex, the line count and diameters drop (two rows from EN-C on, thin unsheathed aramid lines), brake travel gets
/// shorter, and pilots fly in more aerodynamic harnesses.
/// </para>
/// <para>
/// Real arcs are deeper than the default design's: projected spans of 0.77–0.79 of the flat span.
/// </para>
/// </remarks>
public static class GliderPresets
{
    /// <summary>Gets every class, from EN-A to EN-D.</summary>
    public static IReadOnlyList<WingClass> Classes { get; } = Enum.GetValues<WingClass>();

    /// <summary>Gets the names and descriptions of a class.</summary>
    public static WingClassInfo Info(WingClass wingClass) => wingClass switch
    {
        WingClass.EnA => new("EN-A", "School and leisure",
            "The most passive wing: collapses reopen on their own, big brake travel, about 8.5 glide."),
        WingClass.EnBLow => new("Low EN-B", "First cross-country wing",
            "Forgiving with a sharper handling and about 9.5 glide; the step up after school."),
        WingClass.EnBHigh => new("High EN-B", "Performance intermediate",
            "Higher aspect ratio and thinner lines for long cross-country flights, about 10.5 glide."),
        WingClass.EnC => new("EN-C", "Sport two-liner",
            "Two rows of lines and a deep shark nose: fast and efficient, needs active piloting."),
        WingClass.EnD => new("EN-D", "Competition and expert",
            "A high aspect ratio two-liner with 78 cells: the best glide at speed, demanding when it collapses."),
        _ => throw new ArgumentOutOfRangeException(nameof(wingClass)),
    };

    /// <summary>Creates the design of <paramref name="wingClass"/> with the mesh of <paramref name="detail"/>.</summary>
    public static GliderDesign Create(WingClass wingClass, MeshDetail detail = MeshDetail.High)
    {
        var design = wingClass switch
        {
            WingClass.EnA => EnA(),
            WingClass.EnBLow => EnBLow(),
            WingClass.EnBHigh => EnBHigh(),
            WingClass.EnC => EnC(),
            WingClass.EnD => EnD(),
            _ => throw new ArgumentOutOfRangeException(nameof(wingClass)),
        };
        // Lines are about 0.6–0.65 of the flat span below the canopy, a little shorter relative to the span on higher
        // aspect ratio wings.
        design.CarabinerHeight = Math.Round(design.FlatSpan * LineLengthRatio(wingClass), 2);
        SetMeshDetail(design, detail);
        return design;
    }

    /// <summary>
    /// Sets the design's level of detail and writes its settings into the explicit mesh settings (so switching to
    /// <see cref="MeshDetail.Custom"/> later starts from them).
    /// </summary>
    public static void SetMeshDetail(GliderDesign design, MeshDetail detail)
    {
        design.MeshDetail = detail;
        if (detail != MeshDetail.Custom) MeshSettings.FromDetail(detail, design).ApplyTo(design);
    }

    private static double LineLengthRatio(WingClass wingClass) => wingClass switch
    {
        WingClass.EnA => 0.65,
        WingClass.EnBLow => 0.64,
        WingClass.EnBHigh => 0.62,
        WingClass.EnC => 0.61,
        _ => 0.60,
    };

    // A chord distribution like the default (nearly elliptical) ending in the given tip chord.
    private static Curve Chord(double tip) =>
        new(0, 1, 0.3, 0.975, 0.5, 0.925, 0.7, 0.82, 0.85, 0.67, 0.95, 0.31 + 0.55 * tip, 1, tip);

    // EN-A (e.g. Ozone Mojo 6, Advance Alpha, Gin Bolero): 40 cells, aspect ratio 4.9, thick forgiving profile, a mild
    // shark nose, three rows with sheathed lines, long brake travel, an open school harness.
    private static GliderDesign EnA() => new()
    {
        FlatArea = 26.0,
        FlatAspectRatio = 4.9,
        ChordDistribution = Chord(0.36),
        ProjectedSpanRatio = 0.775,
        TipWashout = 3.0,
        RootThickness = 0.18,
        TipThickness = 0.14,
        MaxThicknessPosition = 0.24,
        Camber = 0.036,
        Reflex = 0.004,
        InletEnd = 0.09,
        SharknoseDepth = 0.05,
        CellCount = 40,
        CellWidthChordFactor = 0.5,
        Ballooning = 0.085,
        RowCount = 3,
        RowA = 0.12, RowB = 0.36, RowC = 0.62,
        TabRibInterval = 2,
        BrakeTabInterval = 2,
        UpperLinesPerMiddle = 2,
        MiddleLinesPerMain = 2,
        UpperLineDiameter = 1.1, MiddleLineDiameter = 1.4, MainLineDiameter = 2.2,
        RiserLength = 0.52,
        BrakeSlack = 0.1,
        BrakeTravel = 0.7,
        SpeedBarTravel = 0.10,
        TrimAngleOfAttack = 8.0,
        TrimGlideRatio = 8.6,
        CanopyMass = 5.4,
        PilotDragArea = 0.45,
        Pattern = CanopyPattern.LeadingEdgeBand,
        PrimaryColor = "#2E7D32", SecondaryColor = "#F1F8E9", AccentColor = "#FBC02D", LowerSurfaceColor = "#F1F8E9",
    };

    // Low EN-B (e.g. Ozone Buzz, Advance Epsilon, Gin Explorer): 48 cells, aspect ratio 5.3.
    private static GliderDesign EnBLow() => new()
    {
        FlatArea = 24.5,
        FlatAspectRatio = 5.3,
        ChordDistribution = Chord(0.33),
        ProjectedSpanRatio = 0.78,
        TipWashout = 2.6,
        RootThickness = 0.17,
        TipThickness = 0.13,
        MaxThicknessPosition = 0.23,
        Camber = 0.034,
        Reflex = 0.005,
        InletEnd = 0.085,
        SharknoseDepth = 0.1,
        CellCount = 48,
        CellWidthChordFactor = 0.55,
        Ballooning = 0.08,
        RowCount = 3,
        RowA = 0.13, RowB = 0.36, RowC = 0.62,
        TabRibInterval = 2,
        BrakeTabInterval = 2,
        UpperLinesPerMiddle = 2,
        MiddleLinesPerMain = 2,
        UpperLineDiameter = 0.9, MiddleLineDiameter = 1.2, MainLineDiameter = 1.9,
        RiserLength = 0.5,
        BrakeSlack = 0.1,
        BrakeTravel = 0.65,
        SpeedBarTravel = 0.12,
        TrimAngleOfAttack = 7.6,
        TrimGlideRatio = 9.4,
        CanopyMass = 4.9,
        PilotDragArea = 0.45,
        Pattern = CanopyPattern.Chevron,
        PrimaryColor = "#00838F", SecondaryColor = "#FAFAFA", AccentColor = "#FF7043", LowerSurfaceColor = "#E0F7FA",
    };

    // High EN-B (e.g. Ozone Rush 6: 62 cells, 5.7; Nova Mentor 7: 66 cells): 58 cells, aspect ratio 5.9, thinner
    // unsheathed upper lines.
    private static GliderDesign EnBHigh() => new()
    {
        FlatArea = 23.5,
        FlatAspectRatio = 5.9,
        ChordDistribution = Chord(0.3),
        ProjectedSpanRatio = 0.785,
        TipWashout = 2.3,
        RootThickness = 0.16,
        TipThickness = 0.125,
        MaxThicknessPosition = 0.22,
        Camber = 0.032,
        Reflex = 0.006,
        InletEnd = 0.08,
        SharknoseDepth = 0.14,
        CellCount = 58,
        CellWidthChordFactor = 0.55,
        Ballooning = 0.075,
        RowCount = 3,
        RowA = 0.12, RowB = 0.34, RowC = 0.6,
        TabRibInterval = 2,
        BrakeTabInterval = 2,
        UpperLinesPerMiddle = 2,
        MiddleLinesPerMain = 2,
        UpperLineDiameter = 0.7, MiddleLineDiameter = 1.0, MainLineDiameter = 1.6,
        RiserLength = 0.5,
        BrakeSlack = 0.1,
        BrakeTravel = 0.6,
        SpeedBarTravel = 0.14,
        TrimAngleOfAttack = 7.3,
        TrimGlideRatio = 10.2,
        CanopyMass = 4.7,
        PilotDragArea = 0.42,
        Pattern = CanopyPattern.Stripes,
        PrimaryColor = "#1565C0", SecondaryColor = "#FAFAFA", AccentColor = "#FF6F00", LowerSurfaceColor = "#ECEFF1",
    };

    // EN-C (e.g. Ozone Delta 4: 66 cells, 6.05, two-liner control): two rows, tabs on every third rib with diagonal ribs
    // between, three upper lines per middle line, a deep shark nose and a pod harness.
    private static GliderDesign EnC() => new()
    {
        FlatArea = 23.0,
        FlatAspectRatio = 6.3,
        ChordDistribution = Chord(0.28),
        ProjectedSpanRatio = 0.785,
        TipWashout = 2.0,
        RootThickness = 0.155,
        TipThickness = 0.12,
        MaxThicknessPosition = 0.21,
        Camber = 0.03,
        Reflex = 0.008,
        InletStart = 0.004,
        InletEnd = 0.075,
        SharknoseDepth = 0.18,
        CellCount = 66,
        CellWidthChordFactor = 0.6,
        MiniRibsPerCell = 2,
        Ballooning = 0.07,
        RowCount = 2,
        RowA = 0.12, RowB = 0.55,
        TabRibInterval = 3,
        BrakeTabInterval = 2,
        UpperLinesPerMiddle = 3,
        MiddleLinesPerMain = 2,
        UpperLineDiameter = 0.55, MiddleLineDiameter = 0.8, MainLineDiameter = 1.3,
        RiserLength = 0.48,
        BrakeSlack = 0.08,
        BrakeTravel = 0.55,
        SpeedBarTravel = 0.16,
        TrimAngleOfAttack = 7.0,
        TrimGlideRatio = 10.8,
        CanopyMass = 4.6,
        PilotDragArea = 0.35,
        Pattern = CanopyPattern.Tips,
        PrimaryColor = "#E65100", SecondaryColor = "#FFF3E0", AccentColor = "#263238", LowerSurfaceColor = "#FFF3E0",
    };

    // EN-D (e.g. Ozone Zeno 2: 78 cells, 6.9, root chord 2.26 m at 22.5 m²): a thin, strongly reflexed profile with a
    // deep shark nose, two rows of thin aramid lines, short brake travel, a pod harness.
    private static GliderDesign EnD() => new()
    {
        FlatArea = 22.5,
        FlatAspectRatio = 6.9,
        ChordDistribution = Chord(0.26),
        ProjectedSpanRatio = 0.785,
        TipWashout = 1.6,
        RootThickness = 0.145,
        TipThickness = 0.115,
        MaxThicknessPosition = 0.2,
        Camber = 0.028,
        Reflex = 0.011,
        InletStart = 0.003,
        InletEnd = 0.07,
        SharknoseDepth = 0.22,
        CellCount = 78,
        CellWidthChordFactor = 0.6,
        MiniRibsPerCell = 2,
        Ballooning = 0.065,
        RowCount = 2,
        RowA = 0.11, RowB = 0.56,
        TabRibInterval = 3,
        BrakeTabInterval = 3,
        UpperLinesPerMiddle = 3,
        MiddleLinesPerMain = 2,
        UpperLineDiameter = 0.45, MiddleLineDiameter = 0.7, MainLineDiameter = 1.2,
        RiserLength = 0.46,
        BrakeSlack = 0.08,
        BrakeTravel = 0.5,
        SpeedBarTravel = 0.18,
        TrimAngleOfAttack = 6.8,
        TrimGlideRatio = 11.8,
        CanopyMass = 5.0,
        PilotDragArea = 0.28,
        Pattern = CanopyPattern.Chevron,
        PrimaryColor = "#212121", SecondaryColor = "#FAFAFA", AccentColor = "#D50000", LowerSurfaceColor = "#ECEFF1",
    };
}
