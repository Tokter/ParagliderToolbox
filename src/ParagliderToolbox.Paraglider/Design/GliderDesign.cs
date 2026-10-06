using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Paraglider.Design;

/// <summary>Where the profile of the ribs comes from.</summary>
public enum AirfoilSource
{
    /// <summary>A reflexed paraglider section built from the camber and thickness parameters.</summary>
    Parametric,
    /// <summary>Coordinates pasted or imported in Selig .dat format (<see cref="GliderDesign.CustomAirfoil"/>).</summary>
    Custom,
}

/// <summary>How the arc (the front view curvature) is given.</summary>
public enum ArcMode
{
    /// <summary>The roll angle of the tip rib is given (<see cref="GliderDesign.TipArcAngle"/>).</summary>
    TipAngle,
    /// <summary>The tip angle is solved so the projected span is <see cref="GliderDesign.ProjectedSpanRatio"/> of the flat span.</summary>
    ProjectedSpanRatio,
}

/// <summary>The color layout painted on the canopy.</summary>
public enum CanopyPattern
{
    Solid,
    LeadingEdgeBand,
    Chevron,
    Stripes,
    Tips,
}

/// <summary>Preset complexities of the physics proxy, from arcade games to high realism simulations.</summary>
public enum ProxyComplexity
{
    /// <summary>A single surface on a few spanwise sections, one line per row and side: dozens of nodes.</summary>
    Arcade,
    /// <summary>Upper and lower surface on every second tab rib, mains only.</summary>
    Low,
    /// <summary>Upper and lower surface on every tab rib, with the cascades.</summary>
    Medium,
    /// <summary>Every tab rib with intermediate ribs, more chord stations and every line segment.</summary>
    High,
    /// <summary>The proxy settings as given.</summary>
    Custom,
}

/// <summary>Preset levels of detail of the generated model (see <see cref="MeshSettings"/>).</summary>
public enum MeshDetail
{
    /// <summary>
    /// A few hundred triangles for games: skin panels spanning several cells without ballooning or inlets, no ribs,
    /// lines as flat ribbons, simple hardware, a small texture.
    /// </summary>
    LowPoly,
    /// <summary>Tens of thousands of triangles for real-time use: ballooned cells, ribs, coarse line tubes.</summary>
    Medium,
    /// <summary>Hundreds of thousands of triangles for renders and close-ups.</summary>
    High,
    /// <summary>The mesh settings as given.</summary>
    Custom,
}

/// <summary>
/// Every parameter of a paraglider design: planform, arc, airfoil, cells, ballooning, rigging, appearance, the physics
/// proxy and the mesh resolution. The generator reads a snapshot of it (see <see cref="Clone"/>).
/// </summary>
/// <remarks>
/// Lengths are in meters, angles in degrees, chord positions as fractions of the local chord (0 at the leading edge,
/// 1 at the trailing edge), span positions as fractions of the half span (0 at the center, 1 at the tip). Defaults
/// describe a typical EN-B wing: 24 m² flat, aspect ratio 5.5, 52 cells.
/// </remarks>
public sealed class GliderDesign
{
    #region Planform

    /// <summary>Gets or sets the flat area S (m²), 18–32 for solo wings.</summary>
    public double FlatArea { get; set; } = 24.0;

    /// <summary>Gets or sets the flat aspect ratio b²/S: about 4.8 for school wings, 5.5 for EN-B, 7+ for competition wings.</summary>
    public double FlatAspectRatio { get; set; } = 5.5;

    /// <summary>Gets the flat span b (m), from the area and the aspect ratio.</summary>
    public double FlatSpan => Math.Sqrt(FlatAspectRatio * FlatArea);

    /// <summary>Gets or sets the chord along the half span, relative to the root chord (1 at the center).</summary>
    public Curve ChordDistribution { get; set; } = DefaultChord;

    /// <summary>
    /// Gets or sets the chord fraction that lies on a straight spanwise line in the planform: 0 keeps the leading edge
    /// straight, 1 the trailing edge. Paragliders use about 0.6–0.8, so the leading edge sweeps back at the tips and the
    /// trailing edge curves forward.
    /// </summary>
    public double StraightChordLine { get; set; } = 0.7;

    /// <summary>Gets or sets additional rearward sweep of the leading edge along the half span, in root chords.</summary>
    public Curve LeadingEdgeSweep { get; set; } = new(0, 0, 0.5, 0.005, 0.8, 0.025, 1, 0.06);

    #endregion

    #region Arc

    /// <summary>Gets or sets how the arc is given.</summary>
    public ArcMode ArcMode { get; set; } = ArcMode.ProjectedSpanRatio;

    /// <summary>Gets or sets the roll angle of the tip rib (degrees) when <see cref="ArcMode"/> is <see cref="ArcMode.TipAngle"/>.</summary>
    public double TipArcAngle { get; set; } = 82;

    /// <summary>Gets or sets the projected span as a fraction of the flat span (typically 0.82–0.88) when solving the arc.</summary>
    public double ProjectedSpanRatio { get; set; } = 0.85;

    /// <summary>
    /// Gets or sets the shape of the arc: the roll angle along the half span as a fraction of the tip angle. A curve that
    /// rises late keeps the center flat and bends the tips down (an elliptical arc).
    /// </summary>
    public Curve ArcDistribution { get; set; } = new(0, 0, 0.3, 0.12, 0.55, 0.3, 0.75, 0.52, 0.9, 0.75, 1, 1);

    /// <summary>
    /// Gets or sets the extra roll of the outer ribs (degrees, positive turns their tops outward), applied over the outer
    /// <see cref="TipCantSpan"/> of the half span: flattens or steepens the tips for roll damping and tip vortices.
    /// </summary>
    public double TipCant { get; set; } = 0;

    /// <summary>Gets or sets the part of the half span the tip cant blends in over (0.05–0.4).</summary>
    public double TipCantSpan { get; set; } = 0.15;

    /// <summary>Gets or sets the washout at the tip (degrees, nose down) that keeps the tips from stalling first.</summary>
    public double TipWashout { get; set; } = 2.5;

    /// <summary>Gets or sets the washout along the half span as a fraction of <see cref="TipWashout"/>.</summary>
    public Curve WashoutDistribution { get; set; } = new(0, 0, 0.5, 0.15, 0.8, 0.5, 1, 1);

    /// <summary>Gets or sets the chord fraction the profiles twist about.</summary>
    public double TwistAxis { get; set; } = 0.25;

    #endregion

    #region Airfoil

    /// <summary>Gets or sets where the profile comes from.</summary>
    public AirfoilSource AirfoilSource { get; set; } = AirfoilSource.Parametric;

    /// <summary>Gets or sets the profile coordinates in Selig .dat format when <see cref="AirfoilSource"/> is Custom.</summary>
    public string CustomAirfoil { get; set; } = string.Empty;

    /// <summary>Gets or sets the thickness of the center profile (fraction of chord), usually 0.14–0.18.</summary>
    public double RootThickness { get; set; } = 0.165;

    /// <summary>Gets or sets the thickness of the tip profile (fraction of chord).</summary>
    public double TipThickness { get; set; } = 0.125;

    /// <summary>Gets or sets how the thickness changes from the center (0) to the tip (1) along the half span.</summary>
    public Curve ThicknessDistribution { get; set; } = new(0, 0, 0.5, 0.15, 0.8, 0.5, 1, 1);

    /// <summary>Gets or sets the chord position of the maximum thickness (parametric profile), 0.18–0.3 for paragliders.</summary>
    public double MaxThicknessPosition { get; set; } = 0.22;

    /// <summary>Gets or sets the leading edge radius index of the parametric profile (NACA modified four digit: 6 is normal, 8 blunt).</summary>
    public double LeadingEdgeRadiusIndex { get; set; } = 7;

    /// <summary>Gets or sets the maximum camber (fraction of chord).</summary>
    public double Camber { get; set; } = 0.032;

    /// <summary>Gets or sets the chord position of the maximum camber.</summary>
    public double CamberPosition { get; set; } = 0.3;

    /// <summary>Gets or sets the reflex: how far the rear camber line rises (fraction of chord), for pitch stability.</summary>
    public double Reflex { get; set; } = 0.006;

    #endregion

    #region Inlet

    /// <summary>
    /// Gets or sets where the air inlet starts, as a chord fraction: negative values are on the upper surface (e.g.
    /// -0.02), positive ones on the lower surface.
    /// </summary>
    public double InletStart { get; set; } = 0.005;

    /// <summary>Gets or sets where the air inlet ends on the lower surface (chord fraction).</summary>
    public double InletEnd { get; set; } = 0.085;

    /// <summary>Gets or sets how far the inlet's rims sag into the cell between the ribs (fraction of the inlet height).</summary>
    public double InletRimSag { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the shark nose depth (fraction of profile thickness): the lower surface around the inlet is pulled in
    /// to a concave scoop, so the inlet faces forward and stays open over a wide angle of attack range. 0 is a classic
    /// nose.
    /// </summary>
    public double SharknoseDepth { get; set; } = 0.12;

    /// <summary>Gets or sets whether the leading edge has rods (nylon/nitinol): keeps the nose crisp, with less ballooning there.</summary>
    public bool LeadingEdgeRods { get; set; } = true;

    #endregion

    #region Cells

    /// <summary>Gets or sets the number of cells: about 40 for school wings, 50–60 for EN-B, 70–100 for competition wings.</summary>
    public int CellCount { get; set; } = 52;

    /// <summary>
    /// Gets or sets how the cell widths follow the chord: 0 makes all cells equally wide, 1 makes each cell's width
    /// proportional to its chord (narrow cells at the tips).
    /// </summary>
    public double CellWidthChordFactor { get; set; } = 0.55;

    /// <summary>Gets or sets the number of mini-ribs per cell along the trailing edge (0–2).</summary>
    public int MiniRibsPerCell { get; set; } = 1;

    /// <summary>Gets or sets the length of the mini-ribs (fraction of chord from the trailing edge).</summary>
    public double MiniRibLength { get; set; } = 0.18;

    /// <summary>Gets or sets the number of cross-vent holes in each internal rib.</summary>
    public int CrossVentCount { get; set; } = 3;

    /// <summary>Gets or sets the cross-vent hole radius as a fraction of the local profile height.</summary>
    public double CrossVentRadius { get; set; } = 0.22;

    /// <summary>Gets or sets whether diagonal (V) ribs run from the tab ribs to their neighbors, as on most modern wings.</summary>
    public bool DiagonalRibs { get; set; } = true;

    #endregion

    #region Ballooning

    /// <summary>
    /// Gets or sets the cell ballooning: the largest bulge of the skin between two ribs, as a fraction of the cell width
    /// (0.05–0.12).
    /// </summary>
    public double Ballooning { get; set; } = 0.075;

    /// <summary>Gets or sets how the ballooning changes along the chord (0 leading edge, 1 trailing edge), relative to the maximum.</summary>
    public Curve BallooningDistribution { get; set; } = new(0, 0.15, 0.04, 0.45, 0.12, 0.85, 0.3, 1, 0.65, 0.95, 0.85, 0.65, 0.95, 0.3, 1, 0);

    /// <summary>Gets or sets how much the lower surface balloons compared to the upper surface.</summary>
    public double LowerBallooningFactor { get; set; } = 0.8;

    /// <summary>Gets or sets the amplitude of the creases along the rib seams (fraction of the cell width).</summary>
    public double SeamCreasing { get; set; } = 0.006;

    /// <summary>Gets or sets the wrinkles fanning out from the line tabs (fraction of the cell width).</summary>
    public double TabWrinkles { get; set; } = 0.004;

    /// <summary>Gets or sets the gathering of the trailing edge between the brake tabs (fraction of the cell width).</summary>
    public double TrailingEdgePucker { get; set; } = 0.02;

    #endregion

    #region Rigging

    /// <summary>Gets or sets the number of line rows (2 = A, B for two-liners; 3 = A, B, C; 4 = A, B, C, D).</summary>
    public int RowCount { get; set; } = 3;

    /// <summary>Gets or sets the chord position of the A row.</summary>
    public double RowA { get; set; } = 0.13;

    /// <summary>Gets or sets the chord position of the B row.</summary>
    public double RowB { get; set; } = 0.36;

    /// <summary>Gets or sets the chord position of the C row (with three or four rows).</summary>
    public double RowC { get; set; } = 0.62;

    /// <summary>Gets or sets the chord position of the D row (only with four rows).</summary>
    public double RowD { get; set; } = 0.82;

    /// <summary>Gets or sets which ribs carry line tabs: every second (2) or third (3) rib.</summary>
    public int TabRibInterval { get; set; } = 2;

    /// <summary>Gets or sets which ribs carry brake tabs on the trailing edge.</summary>
    public int BrakeTabInterval { get; set; } = 2;

    /// <summary>Gets or sets how many upper (gallery) lines join into one middle line.</summary>
    public int UpperLinesPerMiddle { get; set; } = 2;

    /// <summary>Gets or sets how many middle lines join into one main line.</summary>
    public int MiddleLinesPerMain { get; set; } = 2;

    /// <summary>Gets or sets where the upper lines join, as a fraction of the way from the canopy to the risers.</summary>
    public double UpperCascadeHeight { get; set; } = 0.3;

    /// <summary>Gets or sets where the middle lines join, as a fraction of the way from the canopy to the risers.</summary>
    public double MiddleCascadeHeight { get; set; } = 0.62;

    /// <summary>Gets or sets the height of the carabiners below the center chord (m); sets the line length.</summary>
    public double CarabinerHeight { get; set; } = 7.4;

    /// <summary>Gets or sets the chord fraction of the center chord the carabiners hang below (0.25–0.4): the pendulum balance.</summary>
    public double CarabinerChordPosition { get; set; } = 0.25;

    /// <summary>Gets or sets the lateral distance between the left and right carabiners (m).</summary>
    public double CarabinerSpacing { get; set; } = 0.44;

    /// <summary>Gets or sets the length of the risers (m).</summary>
    public double RiserLength { get; set; } = 0.5;

    /// <summary>Gets or sets how far the brake toggles can be pulled before the trailing edge moves (m).</summary>
    public double BrakeSlack { get; set; } = 0.1;

    /// <summary>Gets or sets the brake travel from the end of the slack to full brakes (m), about 0.6–0.7 for EN-B wings.</summary>
    public double BrakeTravel { get; set; } = 0.65;

    /// <summary>
    /// Gets or sets how much the speed bar shortens the A risers at full travel (m); the rows behind follow
    /// proportionally less, the last row stays. The simulator's profile is rigid between the line rows, so a centimeter
    /// of riser turns into more angle of attack than on a real wing: the presets use about 0.08 (EN-A) to 0.13 (EN-D),
    /// which gives the classes' real top speeds (about +10 to +19 km/h); real speed systems travel 10–18 cm.
    /// </summary>
    public double SpeedBarTravel { get; set; } = 0.085;

    /// <summary>Gets or sets the angle of attack of the center chord in trim flight (degrees).</summary>
    public double TrimAngleOfAttack { get; set; } = 8.0;

    /// <summary>Gets or sets the trim glide ratio, which tilts the flight path (and so the canopy) below the horizon.</summary>
    public double TrimGlideRatio { get; set; } = 9.5;

    /// <summary>Gets or sets the diameter of the upper (gallery) lines (mm).</summary>
    public double UpperLineDiameter { get; set; } = 0.7;

    /// <summary>Gets or sets the diameter of the middle lines (mm).</summary>
    public double MiddleLineDiameter { get; set; } = 1.1;

    /// <summary>Gets or sets the diameter of the main lines (mm).</summary>
    public double MainLineDiameter { get; set; } = 1.7;

    /// <summary>Gets or sets how much thicker lines are drawn and exported than they are, so they show at a distance (1 = real).</summary>
    public double LineDisplayScale { get; set; } = 1.5;

    #endregion

    #region Appearance

    /// <summary>Gets or sets the color layout of the canopy.</summary>
    public CanopyPattern Pattern { get; set; } = CanopyPattern.Chevron;

    /// <summary>Gets or sets the main color of the upper surface (hex, e.g. #1E88E5).</summary>
    public string PrimaryColor { get; set; } = "#1565C0";

    /// <summary>Gets or sets the second color of the pattern.</summary>
    public string SecondaryColor { get; set; } = "#F5F5F5";

    /// <summary>Gets or sets the accent color of the pattern.</summary>
    public string AccentColor { get; set; } = "#FF6F00";

    /// <summary>Gets or sets the color of the lower surface.</summary>
    public string LowerSurfaceColor { get; set; } = "#ECEFF1";

    /// <summary>Gets or sets the color of the internal ribs.</summary>
    public string RibColor { get; set; } = "#CFD8DC";

    /// <summary>Gets or sets the text printed on the lower surface, empty for none.</summary>
    public string BrandText { get; set; } = "TOOLBOX";

    /// <summary>Gets or sets the size of the canopy textures (pixels along the span) with <see cref="MeshDetail.Custom"/>.</summary>
    public int TextureSize { get; set; } = 4096;

    /// <summary>Gets or sets how translucent the fabric is when backlit (0–1).</summary>
    public double FabricTranslucency { get; set; } = 0.35;

    #endregion

    #region Mesh

    /// <summary>
    /// Gets or sets the level of detail of the generated model; <see cref="MeshDetail.Custom"/> (the default of a new
    /// design, whose settings below are the <see cref="MeshDetail.High"/> ones) uses the settings below. See
    /// <see cref="MeshSettings.FromDesign"/>.
    /// </summary>
    public MeshDetail MeshDetail { get; set; } = MeshDetail.Custom;

    /// <summary>Gets or sets the number of mesh segments across each cell (Custom, with one cell per segment).</summary>
    public int SpanwiseSegmentsPerCell { get; set; } = 10;

    /// <summary>
    /// Gets or sets how many cells one skin segment spans (Custom): 1 builds every cell with its ballooning; more builds a
    /// low poly skin between every n-th rib, without ballooning and inlets.
    /// </summary>
    public int CellsPerSegment { get; set; } = 1;

    /// <summary>Gets or sets the number of mesh segments along each of the upper and lower surface (Custom).</summary>
    public int ChordwiseSegments { get; set; } = 110;

    /// <summary>Gets or sets whether the internal ribs (with cross-vents, mini and diagonal ribs) are generated (Custom).</summary>
    public bool GenerateRibs { get; set; } = true;

    /// <summary>Gets or sets whether the suspension lines, risers, brake lines and toggles are generated.</summary>
    public bool GenerateRigging { get; set; } = true;

    /// <summary>Gets or sets the number of sides of the line tubes (Custom); 2 draws each line as a flat ribbon.</summary>
    public int LineSides { get; set; } = 6;

    /// <summary>Gets or sets the length of the segments along the lines (m, Custom); 0 draws each line as one straight segment.</summary>
    public double LineSegmentLength { get; set; } = 0.5;

    /// <summary>Gets or sets the segments around the maillons, pulleys and carabiners (Custom); below 6 draws simple low poly shapes.</summary>
    public int HardwareSegments { get; set; } = 24;

    #endregion

    #region Physics proxy

    /// <summary>Gets or sets the complexity preset of the physics proxy; Custom uses the settings below.</summary>
    public ProxyComplexity ProxyComplexity { get; set; } = ProxyComplexity.Medium;

    /// <summary>Gets or sets which tab ribs become proxy sections (Custom): every tab rib (1), every second (2), ...</summary>
    public int ProxySectionStride { get; set; } = 1;

    /// <summary>Gets or sets the number of proxy sections between two tab ribs (Custom).</summary>
    public int ProxyIntermediateSections { get; set; } = 0;

    /// <summary>Gets or sets the extra chord stations between the line rows on each surface (Custom).</summary>
    public int ProxyExtraChordStations { get; set; } = 1;

    /// <summary>Gets or sets whether the proxy has an upper and a lower surface (Custom); otherwise a single camber surface.</summary>
    public bool ProxyDoubleSurface { get; set; } = true;

    /// <summary>Gets or sets whether the line cascades are proxy nodes (Custom); otherwise each tab connects to the riser.</summary>
    public bool ProxyCascades { get; set; } = true;

    /// <summary>Gets or sets the canopy mass including the lines (kg).</summary>
    public double CanopyMass { get; set; } = 4.8;

    /// <summary>Gets or sets the mass of the pilot and harness (kg).</summary>
    public double PilotMass { get; set; } = 85;

    /// <summary>Gets or sets the drag area of the pilot and harness (m², Cd·A): about 0.5 seated upright, 0.3 in a pod harness.</summary>
    public double PilotDragArea { get; set; } = 0.33;

    /// <summary>Gets or sets the drag coefficient of the lines on their frontal area (around 1).</summary>
    public double LineDragCoefficient { get; set; } = 0.95;

    /// <summary>
    /// Gets or sets the span efficiency of the lifting line (with the aspect ratio between flat and projected): about
    /// 1.2–1.3. An arched wing induces less drag than a planar one of its projected span (nonplanar lifting line, Cone
    /// 1962), while the strips also charge induced drag on the tips' sideways lift; 1.25 matches measured EN-B polars.
    /// </summary>
    public double SpanEfficiency { get; set; } = 1.25;

    #endregion

    /// <summary>Returns a deep copy (curves are immutable and shared).</summary>
    public GliderDesign Clone() => (GliderDesign)MemberwiseClone();

    /// <summary>Gets the line row chord positions in use (A first).</summary>
    public double[] RowPositions => RowCount switch
    {
        <= 2 => [RowA, RowB],
        3 => [RowA, RowB, RowC],
        _ => [RowA, RowB, RowC, RowD],
    };

    /// <summary>The default chord distribution: elliptical-like with a 0.3 tip chord.</summary>
    public static Curve DefaultChord { get; } = new(0, 1, 0.3, 0.975, 0.5, 0.925, 0.7, 0.82, 0.85, 0.67, 0.95, 0.49, 1, 0.3);
}
