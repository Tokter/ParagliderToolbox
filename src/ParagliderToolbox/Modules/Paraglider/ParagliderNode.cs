using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Atelier.Core.Inspection;
using Atelier.Core.Primitives;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// A paraglider design in the project: every parameter of the generator (see <see cref="GliderDesign"/>), edited in the
/// property editor and saved with the project. The detail view previews the generated model and simulates its proxy.
/// </summary>
/// <remarks>
/// The node keeps its parameters in a <see cref="GliderDesign"/>; <see cref="Snapshot"/> copies it for the generator,
/// which runs on another thread. <see cref="DesignVersion"/> counts the changes, so views know when to regenerate.
/// </remarks>
[Inspectable]
public partial class ParagliderNode : ContainerNode
{
    /// <summary>The property categories in the order of the design steps.</summary>
    public static readonly string[] CategoryOrder =
        ["General", "Planform", "Arc", "Airfoil", "Inlet", "Cells", "Ballooning", "Rigging", "Appearance", "Physics proxy", "Mesh", "Dimensions"];

    private readonly GliderDesign _design = new();
    private GliderShape? _shape;
    private int _shapeVersion = -1;

    /// <summary>Initializes a paraglider with the default (EN-B like) design.</summary>
    public ParagliderNode()
    {
        Name = "Paraglider";
    }

    /// <inheritdoc/>
    /// <remarks>A paraglider holds its recorded polars.</remarks>
    public override bool CanContain(Type childType) => childType == typeof(PolarNode);

    /// <summary>Gets the recorded polars, oldest first.</summary>
    [InspectableIgnore]
    public IEnumerable<PolarNode> Polars => Children.OfType<PolarNode>();

    /// <summary>Gets a number that changes whenever a design parameter changes.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public int DesignVersion { get; private set; }

    /// <summary>Returns a copy of the design for the generator.</summary>
    public GliderDesign Snapshot() => _design.Clone();

    private bool Set<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        DesignVersion++;
        OnPropertyChanged(name);
        return true;
    }

    // The shape of the current design, for the computed dimensions (cheap: no mesh).
    private GliderShape? Shape
    {
        get
        {
            if (_shapeVersion == DesignVersion) return _shape;
            try
            {
                _shape = new GliderShape(_design.Clone());
            }
            catch (FormatException)
            {
                _shape = null;
            }
            _shapeVersion = DesignVersion;
            return _shape;
        }
    }

    #region Planform

    [InspectableProperty("Flat area (m²)", "Planform", Order = 1, Description = "The flat canopy area S: 18–32 m² for solo wings.")]
    public double FlatArea { get => _design.FlatArea; set => SetSizing(() => _design.FlatArea = Math.Clamp(value, 5, 60)); }

    [InspectableProperty("Flat aspect ratio", "Planform", Order = 2, Description = "b²/S: about 4.8 for school wings, 5.5 for EN-B, 7+ for competition wings.")]
    public double FlatAspectRatio { get => _design.FlatAspectRatio; set => SetSizing(() => _design.FlatAspectRatio = Math.Clamp(value, 2.5, 10)); }

    [InspectableProperty("Flat span (m)", "Planform", Order = 3, Description = "Tip to tip, laid out flat. Changing it keeps the area and adjusts the aspect ratio.")]
    [JsonIgnore]
    public double FlatSpan
    {
        get => Math.Round(_design.FlatSpan, 3);
        set => SetSizing(() => _design.FlatAspectRatio = Math.Clamp(value * value / _design.FlatArea, 2.5, 10));
    }

    private void SetSizing(Action apply)
    {
        double area = _design.FlatArea, aspect = _design.FlatAspectRatio;
        apply();
        if (area == _design.FlatArea && aspect == _design.FlatAspectRatio) return;
        DesignVersion++;
        OnPropertyChanged(nameof(FlatArea));
        OnPropertyChanged(nameof(FlatAspectRatio));
        OnPropertyChanged(nameof(FlatSpan));
    }

    [InspectableProperty("Chord distribution", "Planform", Order = 4, Description = "Chord along the half span (0 center … 1 tip), relative to the root chord.")]
    public Curve ChordDistribution { get => _design.ChordDistribution; set => Set(_design.ChordDistribution, value, v => _design.ChordDistribution = v); }

    [InspectableProperty("Straight chord line", "Planform", Order = 5, Description = "The chord fraction lying on a straight line: 0 keeps the leading edge straight, 1 the trailing edge; 0.6–0.8 is typical.")]
    public double StraightChordLine { get => _design.StraightChordLine; set => Set(_design.StraightChordLine, Math.Clamp(value, 0, 1), v => _design.StraightChordLine = v); }

    [InspectableProperty("Leading edge sweep", "Planform", Order = 6, Description = "Extra rearward sweep of the leading edge along the half span, in root chords.")]
    public Curve LeadingEdgeSweep { get => _design.LeadingEdgeSweep; set => Set(_design.LeadingEdgeSweep, value, v => _design.LeadingEdgeSweep = v); }

    #endregion

    #region Arc

    [InspectableProperty("Arc from", "Arc", Order = 10, Description = "Give the tip rib's roll angle, or solve it for a projected span ratio.")]
    public ArcMode ArcMode { get => _design.ArcMode; set => Set(_design.ArcMode, value, v => _design.ArcMode = v); }

    [InspectableProperty("Projected span ratio", "Arc", Order = 11, Description = "Projected span / flat span, typically 0.82–0.88 (when solving the arc).")]
    public double ProjectedSpanRatio { get => _design.ProjectedSpanRatio; set => Set(_design.ProjectedSpanRatio, Math.Clamp(value, 0.5, 0.99), v => _design.ProjectedSpanRatio = v); }

    [InspectableProperty("Tip arc angle (°)", "Arc", Order = 12, Description = "The tip rib's roll angle (when the arc is given by its tip angle).")]
    public double TipArcAngle { get => _design.TipArcAngle; set => Set(_design.TipArcAngle, Math.Clamp(value, 0, 170), v => _design.TipArcAngle = v); }

    [InspectableProperty("Arc distribution", "Arc", Order = 13, Description = "Roll angle along the half span as a fraction of the tip angle: a late rise keeps the center flat.")]
    public Curve ArcDistribution { get => _design.ArcDistribution; set => Set(_design.ArcDistribution, value, v => _design.ArcDistribution = v); }

    [InspectableProperty("Tip cant (°)", "Arc", Order = 14, Description = "Extra roll of the outer ribs (positive turns their tops outward).")]
    public double TipCant { get => _design.TipCant; set => Set(_design.TipCant, Math.Clamp(value, -45, 45), v => _design.TipCant = v); }

    [InspectableProperty("Tip cant span", "Arc", Order = 15, Description = "The outer part of the half span the tip cant blends in over.")]
    public double TipCantSpan { get => _design.TipCantSpan; set => Set(_design.TipCantSpan, Math.Clamp(value, 0.02, 0.6), v => _design.TipCantSpan = v); }

    [InspectableProperty("Tip washout (°)", "Arc", Order = 16, Description = "Nose-down twist of the tip, so the tips stall after the center.")]
    public double TipWashout { get => _design.TipWashout; set => Set(_design.TipWashout, Math.Clamp(value, -10, 15), v => _design.TipWashout = v); }

    [InspectableProperty("Washout distribution", "Arc", Order = 17, Description = "Twist along the half span as a fraction of the tip washout.")]
    public Curve WashoutDistribution { get => _design.WashoutDistribution; set => Set(_design.WashoutDistribution, value, v => _design.WashoutDistribution = v); }

    [InspectableProperty("Twist axis", "Arc", Order = 18, Description = "The chord fraction the profiles twist about.")]
    public double TwistAxis { get => _design.TwistAxis; set => Set(_design.TwistAxis, Math.Clamp(value, 0, 1), v => _design.TwistAxis = v); }

    #endregion

    #region Airfoil

    [InspectableProperty("Profile", "Airfoil", Order = 20, Description = "A parametric reflexed section, or custom coordinates (Selig .dat).")]
    public AirfoilSource AirfoilSource { get => _design.AirfoilSource; set => Set(_design.AirfoilSource, value, v => _design.AirfoilSource = v); }

    [MultilineText(MinLines = 3, MaxLines = 10)]
    [InspectableProperty("Custom coordinates", "Airfoil", Order = 21, Description = "Selig .dat coordinates (x y per line, upper trailing edge over the nose to the lower trailing edge); import them with the Import airfoil command.")]
    public string CustomAirfoil { get => _design.CustomAirfoil; set => Set(_design.CustomAirfoil, value ?? string.Empty, v => _design.CustomAirfoil = v); }

    [InspectableProperty("Root thickness", "Airfoil", Order = 22, Description = "Thickness of the center profile (fraction of chord), usually 0.14–0.18.")]
    public double RootThickness { get => _design.RootThickness; set => Set(_design.RootThickness, Math.Clamp(value, 0.05, 0.3), v => _design.RootThickness = v); }

    [InspectableProperty("Tip thickness", "Airfoil", Order = 23, Description = "Thickness of the tip profile (fraction of chord).")]
    public double TipThickness { get => _design.TipThickness; set => Set(_design.TipThickness, Math.Clamp(value, 0.05, 0.3), v => _design.TipThickness = v); }

    [InspectableProperty("Thickness distribution", "Airfoil", Order = 24, Description = "How the thickness goes from the root (0) to the tip (1) along the half span.")]
    public Curve ThicknessDistribution { get => _design.ThicknessDistribution; set => Set(_design.ThicknessDistribution, value, v => _design.ThicknessDistribution = v); }

    [InspectableProperty("Max thickness at", "Airfoil", Order = 25, Description = "Chord position of the maximum thickness (parametric profile), 0.18–0.3 for paragliders.")]
    public double MaxThicknessPosition { get => _design.MaxThicknessPosition; set => Set(_design.MaxThicknessPosition, Math.Clamp(value, 0.15, 0.6), v => _design.MaxThicknessPosition = v); }

    [InspectableProperty("Nose radius index", "Airfoil", Order = 26, Description = "NACA modified four-digit leading edge radius index: 6 normal, 8 blunt.")]
    public double LeadingEdgeRadiusIndex { get => _design.LeadingEdgeRadiusIndex; set => Set(_design.LeadingEdgeRadiusIndex, Math.Clamp(value, 0.5, 9), v => _design.LeadingEdgeRadiusIndex = v); }

    [InspectableProperty("Camber", "Airfoil", Order = 27, Description = "Maximum camber (fraction of chord).")]
    public double Camber { get => _design.Camber; set => Set(_design.Camber, Math.Clamp(value, 0, 0.1), v => _design.Camber = v); }

    [InspectableProperty("Camber at", "Airfoil", Order = 28, Description = "Chord position of the maximum camber.")]
    public double CamberPosition { get => _design.CamberPosition; set => Set(_design.CamberPosition, Math.Clamp(value, 0.1, 0.7), v => _design.CamberPosition = v); }

    [InspectableProperty("Reflex", "Airfoil", Order = 29, Description = "How far the rear camber line bends up (fraction of chord), for pitch stability.")]
    public double Reflex { get => _design.Reflex; set => Set(_design.Reflex, Math.Clamp(value, 0, 0.05), v => _design.Reflex = v); }

    #endregion

    #region Inlet

    [InspectableProperty("Inlet start", "Inlet", Order = 30, Description = "Where the air inlet starts (chord fraction); negative values are on the upper surface.")]
    public double InletStart { get => _design.InletStart; set => Set(_design.InletStart, Math.Clamp(value, -0.05, 0.1), v => _design.InletStart = v); }

    [InspectableProperty("Inlet end", "Inlet", Order = 31, Description = "Where the air inlet ends on the lower surface (chord fraction).")]
    public double InletEnd { get => _design.InletEnd; set => Set(_design.InletEnd, Math.Clamp(value, 0.005, 0.25), v => _design.InletEnd = v); }

    [InspectableProperty("Inlet rim sag", "Inlet", Order = 32, Description = "How far the inlet's rims hang into the cell between the ribs (fraction of the inlet height).")]
    public double InletRimSag { get => _design.InletRimSag; set => Set(_design.InletRimSag, Math.Clamp(value, 0, 0.8), v => _design.InletRimSag = v); }

    [InspectableProperty("Shark nose depth", "Inlet", Order = 33, Description = "Pulls the lower surface around the inlet into a concave scoop (fraction of the thickness); 0 is a classic nose.")]
    public double SharknoseDepth { get => _design.SharknoseDepth; set => Set(_design.SharknoseDepth, Math.Clamp(value, 0, 0.4), v => _design.SharknoseDepth = v); }

    [InspectableProperty("Leading edge rods", "Inlet", Order = 34, Description = "Nylon/nitinol rods keep the nose crisp, with less ballooning there.")]
    public bool LeadingEdgeRods { get => _design.LeadingEdgeRods; set => Set(_design.LeadingEdgeRods, value, v => _design.LeadingEdgeRods = v); }

    #endregion

    #region Cells

    [InspectableProperty("Cells", "Cells", Order = 40, Description = "Number of cells: about 40 for school wings, 50–60 for EN-B, 70–100 for competition wings.")]
    public int CellCount { get => _design.CellCount; set => Set(_design.CellCount, Math.Clamp(value, 6, 140), v => _design.CellCount = v); }

    [InspectableProperty("Cell width follows chord", "Cells", Order = 41, Description = "0 makes all cells equally wide, 1 makes each cell's width proportional to its chord.")]
    public double CellWidthChordFactor { get => _design.CellWidthChordFactor; set => Set(_design.CellWidthChordFactor, Math.Clamp(value, 0, 1), v => _design.CellWidthChordFactor = v); }

    [InspectableProperty("Mini-ribs per cell", "Cells", Order = 42, Description = "Short ribs along the trailing edge between the main ribs (0–3).")]
    public int MiniRibsPerCell { get => _design.MiniRibsPerCell; set => Set(_design.MiniRibsPerCell, Math.Clamp(value, 0, 3), v => _design.MiniRibsPerCell = v); }

    [InspectableProperty("Mini-rib length", "Cells", Order = 43, Description = "Length of the mini-ribs (fraction of chord from the trailing edge).")]
    public double MiniRibLength { get => _design.MiniRibLength; set => Set(_design.MiniRibLength, Math.Clamp(value, 0.02, 0.5), v => _design.MiniRibLength = v); }

    [InspectableProperty("Cross-vents per rib", "Cells", Order = 44, Description = "Number of ventilation holes in each internal rib.")]
    public int CrossVentCount { get => _design.CrossVentCount; set => Set(_design.CrossVentCount, Math.Clamp(value, 0, 8), v => _design.CrossVentCount = v); }

    [InspectableProperty("Cross-vent radius", "Cells", Order = 45, Description = "Hole radius as a fraction of the local profile height.")]
    public double CrossVentRadius { get => _design.CrossVentRadius; set => Set(_design.CrossVentRadius, Math.Clamp(value, 0, 0.45), v => _design.CrossVentRadius = v); }

    [InspectableProperty("Diagonal ribs", "Cells", Order = 46, Description = "V-ribs from the tab ribs to their neighbors.")]
    public bool DiagonalRibs { get => _design.DiagonalRibs; set => Set(_design.DiagonalRibs, value, v => _design.DiagonalRibs = v); }

    #endregion

    #region Ballooning

    [InspectableProperty("Ballooning", "Ballooning", Order = 50, Description = "The largest bulge of the skin between two ribs, as a fraction of the cell width (0.05–0.12).")]
    public double Ballooning { get => _design.Ballooning; set => Set(_design.Ballooning, Math.Clamp(value, 0, 0.3), v => _design.Ballooning = v); }

    [InspectableProperty("Ballooning distribution", "Ballooning", Order = 51, Description = "Ballooning along the chord (0 nose, 1 trailing edge) relative to the maximum: small at the reinforced nose, zero at the trailing edge seam.")]
    public Curve BallooningDistribution { get => _design.BallooningDistribution; set => Set(_design.BallooningDistribution, value, v => _design.BallooningDistribution = v); }

    [InspectableProperty("Lower surface ballooning", "Ballooning", Order = 52, Description = "How much the lower surface balloons compared to the upper one.")]
    public double LowerBallooningFactor { get => _design.LowerBallooningFactor; set => Set(_design.LowerBallooningFactor, Math.Clamp(value, 0, 2), v => _design.LowerBallooningFactor = v); }

    [InspectableProperty("Seam creasing", "Ballooning", Order = 53, Description = "Creases along the rib seams (fraction of the cell width).")]
    public double SeamCreasing { get => _design.SeamCreasing; set => Set(_design.SeamCreasing, Math.Clamp(value, 0, 0.05), v => _design.SeamCreasing = v); }

    [InspectableProperty("Tab wrinkles", "Ballooning", Order = 54, Description = "Wrinkles fanning out from the line tabs (fraction of the cell width).")]
    public double TabWrinkles { get => _design.TabWrinkles; set => Set(_design.TabWrinkles, Math.Clamp(value, 0, 0.05), v => _design.TabWrinkles = v); }

    [InspectableProperty("Trailing edge pucker", "Ballooning", Order = 55, Description = "Gathering of the trailing edge between the brake tabs (fraction of their spacing).")]
    public double TrailingEdgePucker { get => _design.TrailingEdgePucker; set => Set(_design.TrailingEdgePucker, Math.Clamp(value, 0, 0.1), v => _design.TrailingEdgePucker = v); }

    #endregion

    #region Rigging

    [InspectableProperty("Line rows", "Rigging", Order = 60, Description = "3 (A, B, C) or 4 (A, B, C, D).")]
    public int RowCount { get => _design.RowCount; set => Set(_design.RowCount, Math.Clamp(value, 3, 4), v => _design.RowCount = v); }

    [InspectableProperty("A row", "Rigging", Order = 61, Description = "Chord position of the A tabs.")]
    public double RowA { get => _design.RowA; set => Set(_design.RowA, Math.Clamp(value, 0.03, 0.4), v => _design.RowA = v); }

    [InspectableProperty("B row", "Rigging", Order = 62, Description = "Chord position of the B tabs.")]
    public double RowB { get => _design.RowB; set => Set(_design.RowB, Math.Clamp(value, 0.1, 0.7), v => _design.RowB = v); }

    [InspectableProperty("C row", "Rigging", Order = 63, Description = "Chord position of the C tabs.")]
    public double RowC { get => _design.RowC; set => Set(_design.RowC, Math.Clamp(value, 0.2, 0.9), v => _design.RowC = v); }

    [InspectableProperty("D row", "Rigging", Order = 64, Description = "Chord position of the D tabs (with four rows).")]
    public double RowD { get => _design.RowD; set => Set(_design.RowD, Math.Clamp(value, 0.3, 0.95), v => _design.RowD = v); }

    [InspectableProperty("Tabs every n-th rib", "Rigging", Order = 65, Description = "Which ribs carry line tabs (counted from the tips).")]
    public int TabRibInterval { get => _design.TabRibInterval; set => Set(_design.TabRibInterval, Math.Clamp(value, 1, 4), v => _design.TabRibInterval = v); }

    [InspectableProperty("Brake tabs every n-th rib", "Rigging", Order = 66, Description = "Which ribs carry brake tabs on the trailing edge.")]
    public int BrakeTabInterval { get => _design.BrakeTabInterval; set => Set(_design.BrakeTabInterval, Math.Clamp(value, 1, 4), v => _design.BrakeTabInterval = v); }

    [InspectableProperty("Upper lines per middle line", "Rigging", Order = 67, Description = "How many gallery lines join into one middle line.")]
    public int UpperLinesPerMiddle { get => _design.UpperLinesPerMiddle; set => Set(_design.UpperLinesPerMiddle, Math.Clamp(value, 1, 5), v => _design.UpperLinesPerMiddle = v); }

    [InspectableProperty("Middle lines per main line", "Rigging", Order = 68, Description = "How many middle lines join into one main line.")]
    public int MiddleLinesPerMain { get => _design.MiddleLinesPerMain; set => Set(_design.MiddleLinesPerMain, Math.Clamp(value, 1, 5), v => _design.MiddleLinesPerMain = v); }

    [InspectableProperty("Upper cascade height", "Rigging", Order = 69, Description = "Where the upper lines join, as a fraction of the way from the canopy to the risers.")]
    public double UpperCascadeHeight { get => _design.UpperCascadeHeight; set => Set(_design.UpperCascadeHeight, Math.Clamp(value, 0.05, 0.9), v => _design.UpperCascadeHeight = v); }

    [InspectableProperty("Middle cascade height", "Rigging", Order = 70, Description = "Where the middle lines join, as a fraction of the way from the canopy to the risers.")]
    public double MiddleCascadeHeight { get => _design.MiddleCascadeHeight; set => Set(_design.MiddleCascadeHeight, Math.Clamp(value, 0.1, 0.95), v => _design.MiddleCascadeHeight = v); }

    [InspectableProperty("Carabiner height (m)", "Rigging", Order = 71, Description = "Height of the carabiners below the center chord: sets the line length.")]
    public double CarabinerHeight { get => _design.CarabinerHeight; set => Set(_design.CarabinerHeight, Math.Clamp(value, 2, 15), v => _design.CarabinerHeight = v); }

    [InspectableProperty("Carabiner chord position", "Rigging", Order = 72, Description = "The center chord fraction the pilot hangs below (pitch balance, 0.2–0.35).")]
    public double CarabinerChordPosition { get => _design.CarabinerChordPosition; set => Set(_design.CarabinerChordPosition, Math.Clamp(value, 0, 0.7), v => _design.CarabinerChordPosition = v); }

    [InspectableProperty("Carabiner spacing (m)", "Rigging", Order = 73, Description = "Lateral distance between the carabiners.")]
    public double CarabinerSpacing { get => _design.CarabinerSpacing; set => Set(_design.CarabinerSpacing, Math.Clamp(value, 0.2, 0.8), v => _design.CarabinerSpacing = v); }

    [InspectableProperty("Riser length (m)", "Rigging", Order = 74, Description = "Length of the risers.")]
    public double RiserLength { get => _design.RiserLength; set => Set(_design.RiserLength, Math.Clamp(value, 0.2, 1), v => _design.RiserLength = v); }

    [InspectableProperty("Brake slack (m)", "Rigging", Order = 75, Description = "How far the brakes can be pulled before the trailing edge moves.")]
    public double BrakeSlack { get => _design.BrakeSlack; set => Set(_design.BrakeSlack, Math.Clamp(value, 0, 0.4), v => _design.BrakeSlack = v); }

    [InspectableProperty("Brake travel (m)", "Rigging", Order = 76, Description = "Brake travel from the end of the slack to full brakes.")]
    public double BrakeTravel { get => _design.BrakeTravel; set => Set(_design.BrakeTravel, Math.Clamp(value, 0.2, 1.2), v => _design.BrakeTravel = v); }

    [InspectableProperty("Trim angle of attack (°)", "Rigging", Order = 77, Description = "Angle of attack of the center chord in trim flight; with the glide ratio it sets the canopy's pitch.")]
    public double TrimAngleOfAttack { get => _design.TrimAngleOfAttack; set => Set(_design.TrimAngleOfAttack, Math.Clamp(value, 0, 20), v => _design.TrimAngleOfAttack = v); }

    [InspectableProperty("Trim glide ratio", "Rigging", Order = 78, Description = "The glide ratio the canopy's pitch is drawn for.")]
    public double TrimGlideRatio { get => _design.TrimGlideRatio; set => Set(_design.TrimGlideRatio, Math.Clamp(value, 2, 20), v => _design.TrimGlideRatio = v); }

    [InspectableProperty("Upper line diameter (mm)", "Rigging", Order = 79, Description = "Diameter of the gallery lines.")]
    public double UpperLineDiameter { get => _design.UpperLineDiameter; set => Set(_design.UpperLineDiameter, Math.Clamp(value, 0.3, 5), v => _design.UpperLineDiameter = v); }

    [InspectableProperty("Middle line diameter (mm)", "Rigging", Order = 80, Description = "Diameter of the middle lines.")]
    public double MiddleLineDiameter { get => _design.MiddleLineDiameter; set => Set(_design.MiddleLineDiameter, Math.Clamp(value, 0.3, 5), v => _design.MiddleLineDiameter = v); }

    [InspectableProperty("Main line diameter (mm)", "Rigging", Order = 81, Description = "Diameter of the main lines.")]
    public double MainLineDiameter { get => _design.MainLineDiameter; set => Set(_design.MainLineDiameter, Math.Clamp(value, 0.3, 5), v => _design.MainLineDiameter = v); }

    [InspectableProperty("Line display scale", "Rigging", Order = 82, Description = "How much thicker lines are drawn and exported than they are, so they show at a distance (1 = real).")]
    public double LineDisplayScale { get => _design.LineDisplayScale; set => Set(_design.LineDisplayScale, Math.Clamp(value, 1, 10), v => _design.LineDisplayScale = v); }

    #endregion

    #region Appearance

    [InspectableProperty("Pattern", "Appearance", Order = 90, Description = "The color layout of the upper surface.")]
    public CanopyPattern Pattern { get => _design.Pattern; set => Set(_design.Pattern, value, v => _design.Pattern = v); }

    [InspectableProperty("Primary color", "Appearance", Order = 91)]
    [JsonIgnore]
    public Color PrimaryColor { get => ToColor(_design.PrimaryColor); set => PrimaryColorHex = ToHex(value); }

    [InspectableProperty("Secondary color", "Appearance", Order = 92)]
    [JsonIgnore]
    public Color SecondaryColor { get => ToColor(_design.SecondaryColor); set => SecondaryColorHex = ToHex(value); }

    [InspectableProperty("Accent color", "Appearance", Order = 93)]
    [JsonIgnore]
    public Color AccentColor { get => ToColor(_design.AccentColor); set => AccentColorHex = ToHex(value); }

    [InspectableProperty("Lower surface color", "Appearance", Order = 94)]
    [JsonIgnore]
    public Color LowerSurfaceColor { get => ToColor(_design.LowerSurfaceColor); set => LowerSurfaceColorHex = ToHex(value); }

    [InspectableProperty("Rib color", "Appearance", Order = 95)]
    [JsonIgnore]
    public Color RibColor { get => ToColor(_design.RibColor); set => RibColorHex = ToHex(value); }

    [InspectableProperty("Brand text", "Appearance", Order = 96, Description = "Printed on the lower surface; empty for none.")]
    public string BrandText { get => _design.BrandText; set => Set(_design.BrandText, value ?? string.Empty, v => _design.BrandText = v); }

    [InspectableProperty("Texture size", "Appearance", Order = 97, Description = "Pixels of the exported canopy textures (the preview uses 2048).")]
    public int TextureSize { get => _design.TextureSize; set => Set(_design.TextureSize, Math.Clamp(value, 512, 8192), v => _design.TextureSize = v); }

    [InspectableProperty("Fabric translucency", "Appearance", Order = 98, Description = "How much light shows through the fabric when it's backlit (0–1).")]
    public double FabricTranslucency { get => _design.FabricTranslucency; set => Set(_design.FabricTranslucency, Math.Clamp(value, 0, 1), v => _design.FabricTranslucency = v); }

    // The colors are saved as hex text.
    [InspectableIgnore] public string PrimaryColorHex { get => _design.PrimaryColor; set => SetColor(_design.PrimaryColor, value, v => _design.PrimaryColor = v, nameof(PrimaryColor)); }
    [InspectableIgnore] public string SecondaryColorHex { get => _design.SecondaryColor; set => SetColor(_design.SecondaryColor, value, v => _design.SecondaryColor = v, nameof(SecondaryColor)); }
    [InspectableIgnore] public string AccentColorHex { get => _design.AccentColor; set => SetColor(_design.AccentColor, value, v => _design.AccentColor = v, nameof(AccentColor)); }
    [InspectableIgnore] public string LowerSurfaceColorHex { get => _design.LowerSurfaceColor; set => SetColor(_design.LowerSurfaceColor, value, v => _design.LowerSurfaceColor = v, nameof(LowerSurfaceColor)); }
    [InspectableIgnore] public string RibColorHex { get => _design.RibColor; set => SetColor(_design.RibColor, value, v => _design.RibColor = v, nameof(RibColor)); }

    private void SetColor(string current, string? value, Action<string> apply, string colorProperty)
    {
        if (Set(current, value ?? string.Empty, apply, colorProperty)) OnPropertyChanged(colorProperty + "Hex");
    }

    private static Color ToColor(string hex)
    {
        try
        {
            return Color.FromHex(hex);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return Color.FromRgb(128, 128, 128);
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    #endregion

    #region Physics proxy

    [InspectableProperty("Complexity", "Physics proxy", Order = 100, Description = "From arcade (dozens of nodes) to high realism (over a thousand); Custom uses the settings below.")]
    public ProxyComplexity ProxyComplexity { get => _design.ProxyComplexity; set => Set(_design.ProxyComplexity, value, v => _design.ProxyComplexity = v); }

    [InspectableProperty("Section every n-th tab rib", "Physics proxy", Order = 101, Description = "Custom: which tab ribs become proxy sections.")]
    public int ProxySectionStride { get => _design.ProxySectionStride; set => Set(_design.ProxySectionStride, Math.Clamp(value, 1, 6), v => _design.ProxySectionStride = v); }

    [InspectableProperty("Sections between tab ribs", "Physics proxy", Order = 102, Description = "Custom: extra sections between two chosen tab ribs (held by diagonal ribs).")]
    public int ProxyIntermediateSections { get => _design.ProxyIntermediateSections; set => Set(_design.ProxyIntermediateSections, Math.Clamp(value, 0, 3), v => _design.ProxyIntermediateSections = v); }

    [InspectableProperty("Extra chord stations", "Physics proxy", Order = 103, Description = "Custom: chord stations between the line rows on each surface (0–2).")]
    public int ProxyExtraChordStations { get => _design.ProxyExtraChordStations; set => Set(_design.ProxyExtraChordStations, Math.Clamp(value, 0, 2), v => _design.ProxyExtraChordStations = v); }

    [InspectableProperty("Upper and lower surface", "Physics proxy", Order = 104, Description = "Custom: a double surface with pressurized cells, or a single camber surface.")]
    public bool ProxyDoubleSurface { get => _design.ProxyDoubleSurface; set => Set(_design.ProxyDoubleSurface, value, v => _design.ProxyDoubleSurface = v); }

    [InspectableProperty("Cascade knots", "Physics proxy", Order = 105, Description = "Custom: the line cascades as nodes, or every tab straight to its riser.")]
    public bool ProxyCascades { get => _design.ProxyCascades; set => Set(_design.ProxyCascades, value, v => _design.ProxyCascades = v); }

    [InspectableProperty("Canopy mass (kg)", "Physics proxy", Order = 106, Description = "Canopy with lines and risers.")]
    public double CanopyMass { get => _design.CanopyMass; set => Set(_design.CanopyMass, Math.Clamp(value, 0.5, 20), v => _design.CanopyMass = v); }

    [InspectableProperty("Pilot mass (kg)", "Physics proxy", Order = 107, Description = "Pilot with harness and equipment.")]
    public double PilotMass { get => _design.PilotMass; set => Set(_design.PilotMass, Math.Clamp(value, 20, 250), v => _design.PilotMass = v); }

    [InspectableProperty("Pilot drag area (m²)", "Physics proxy", Order = 108, Description = "Cd·A of pilot and harness: about 0.5 seated upright, 0.3 in a pod.")]
    public double PilotDragArea { get => _design.PilotDragArea; set => Set(_design.PilotDragArea, Math.Clamp(value, 0, 2), v => _design.PilotDragArea = v); }

    [InspectableProperty("Line drag coefficient", "Physics proxy", Order = 109, Description = "Drag coefficient of the lines on their frontal area (around 1).")]
    public double LineDragCoefficient { get => _design.LineDragCoefficient; set => Set(_design.LineDragCoefficient, Math.Clamp(value, 0, 2), v => _design.LineDragCoefficient = v); }

    [InspectableProperty("Span efficiency", "Physics proxy", Order = 110, Description = "Lifting line efficiency (about 1: the arc helps).")]
    public double SpanEfficiency { get => _design.SpanEfficiency; set => Set(_design.SpanEfficiency, Math.Clamp(value, 0.3, 1.5), v => _design.SpanEfficiency = v); }

    #endregion

    #region Mesh

    [InspectableProperty("Segments per cell", "Mesh", Order = 120, Description = "Mesh segments across each cell (rounded so mini-ribs land on vertices).")]
    public int SpanwiseSegmentsPerCell { get => _design.SpanwiseSegmentsPerCell; set => Set(_design.SpanwiseSegmentsPerCell, Math.Clamp(value, 2, 40), v => _design.SpanwiseSegmentsPerCell = v); }

    [InspectableProperty("Chordwise segments", "Mesh", Order = 121, Description = "Mesh segments along each of the upper and lower surface.")]
    public int ChordwiseSegments { get => _design.ChordwiseSegments; set => Set(_design.ChordwiseSegments, Math.Clamp(value, 10, 400), v => _design.ChordwiseSegments = v); }

    [InspectableProperty("Internal ribs", "Mesh", Order = 122, Description = "Generate the internal ribs with cross-vents, mini-ribs and diagonal ribs.")]
    public bool GenerateRibs { get => _design.GenerateRibs; set => Set(_design.GenerateRibs, value, v => _design.GenerateRibs = v); }

    [InspectableProperty("Rigging", "Mesh", Order = 123, Description = "Generate the lines, risers, brake toggles and hardware.")]
    public bool GenerateRigging { get => _design.GenerateRigging; set => Set(_design.GenerateRigging, value, v => _design.GenerateRigging = v); }

    [InspectableProperty("Line sides", "Mesh", Order = 124, Description = "Number of sides of the line tubes.")]
    public int LineSides { get => _design.LineSides; set => Set(_design.LineSides, Math.Clamp(value, 3, 16), v => _design.LineSides = v); }

    #endregion

    #region Dimensions (computed)

    [InspectableProperty("Projected span (m)", "Dimensions", Order = 130, IsReadOnly = true, Description = "Distance between the tips in the front view.")]
    public double ProjectedSpan => Math.Round(Shape?.ProjectedSpan ?? 0, 3);

    [InspectableProperty("Projected area (m²)", "Dimensions", Order = 131, IsReadOnly = true, Description = "The canopy's area seen from above.")]
    public double ProjectedArea => Math.Round(Shape?.ProjectedArea ?? 0, 3);

    [InspectableProperty("Projected aspect ratio", "Dimensions", Order = 132, IsReadOnly = true)]
    public double ProjectedAspectRatio => Math.Round(Shape?.ProjectedAspectRatio ?? 0, 3);

    [InspectableProperty("Root chord (m)", "Dimensions", Order = 133, IsReadOnly = true)]
    public double RootChord => Math.Round(Shape?.RootChord ?? 0, 3);

    [InspectableProperty("Tip chord (m)", "Dimensions", Order = 134, IsReadOnly = true)]
    public double TipChord => Math.Round(Shape?.Chord(1) ?? 0, 3);

    [InspectableProperty("Tip roll angle (°)", "Dimensions", Order = 135, IsReadOnly = true, Description = "The tip rib's roll angle, solved or given.")]
    public double TipRollAngle => Math.Round((Shape?.TipArcAngle ?? 0) * 180 / Math.PI, 2);

    #endregion

    /// <inheritdoc/>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // The computed dimensions follow the design.
        if (e.PropertyName is not (nameof(ProjectedSpan) or nameof(ProjectedArea) or nameof(ProjectedAspectRatio) or nameof(RootChord)
            or nameof(TipChord) or nameof(TipRollAngle) or nameof(Name) or nameof(Parent) or nameof(DesignVersion)) && DesignVersion != _notifiedVersion)
        {
            _notifiedVersion = DesignVersion;
            foreach (string name in new[] { nameof(ProjectedSpan), nameof(ProjectedArea), nameof(ProjectedAspectRatio), nameof(RootChord), nameof(TipChord), nameof(TipRollAngle) })
            {
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }
    }

    private int _notifiedVersion;
}
