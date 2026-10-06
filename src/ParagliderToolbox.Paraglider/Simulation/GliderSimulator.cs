using System.Numerics;
using ParagliderToolbox.Paraglider.Proxy;

namespace ParagliderToolbox.Paraglider.Simulation;

/// <summary>The pilot's inputs, each from 0 to 1 (weight shift from −1 right to +1 left).</summary>
public sealed class SimulationInputs
{
    public float BrakeLeft { get; set; }
    public float BrakeRight { get; set; }
    public float SpeedBar { get; set; }
    public float WeightShift { get; set; }

    /// <summary>Pulls the outer left A lines down: an asymmetric collapse.</summary>
    public float CollapseLeft { get; set; }

    /// <summary>Pulls the outer right A lines down.</summary>
    public float CollapseRight { get; set; }

    /// <summary>Pulls all A lines down: a frontal collapse.</summary>
    public float Frontal { get; set; }

    /// <summary>Pulls the outermost A lines on both sides in: big ears.</summary>
    public float BigEars { get; set; }

    /// <summary>Gets or sets the wind (m/s), e.g. a gust from below.</summary>
    public Vector3 Wind { get; set; }
}

/// <summary>Tuning of the simulator.</summary>
public sealed record SimulatorSettings
{
    /// <summary>Gets the substeps per step (XPBD small steps with one iteration each).</summary>
    public int Substeps { get; init; } = 16;

    /// <summary>Gets the air density (kg/m³).</summary>
    public float AirDensity { get; init; } = 1.225f;

    /// <summary>Gets the velocity damping per second (numerical, small).</summary>
    public float Damping { get; init; } = 0.05f;

    /// <summary>Gets the lift a deflected trailing edge (brakes) adds, as a fraction of the lift slope per radian of deflection.</summary>
    public float FlapEffectiveness { get; init; } = 0.5f;

    /// <summary>Gets whether aerodynamic forces (lift and drag of the strips, lines and pilot) are applied.</summary>
    public bool Aerodynamics { get; init; } = true;

    /// <summary>Gets whether the ram-air pressure inflates the cells.</summary>
    public bool Pressure { get; init; } = true;

    /// <summary>Gets the share of the full cell pressure a deeply stalled wing keeps (the flow from below still enters the inlets).</summary>
    public float StalledInletPressure { get; init; } = 0.35f;

    /// <summary>
    /// Gets how much softer the cells get at low airspeeds, as the exponent of the dynamic pressure relative to trim (0:
    /// not at all; the pressure inside follows the dynamic pressure while the loads from the pilot's weight don't).
    /// </summary>
    public float FirmnessAirspeedExponent { get; init; } = 0.25f;

    /// <summary>
    /// Gets whether the air inside the cells adds to the canopy's inertia (its weight is carried by the surrounding air, so
    /// it adds no weight): about 3 kg more canopy mass. Physical, but off by default: it changes little (the apparent mass
    /// of the air around the canopy, about 40 kg, is what would matter), costs some glide, and on the High proxy it needs
    /// <see cref="FabricDamping"/>.
    /// </summary>
    public bool EnclosedAir { get; init; }

    /// <summary>
    /// Gets the gap (fraction of the rest profile height) the upper skin keeps above the lower skin at every chord
    /// station, measured across the local fabric; 0 turns the separation off. Empty cells are limp, and without it a skin
    /// can pass through the other and the pressure on the inverted pocket keeps it there.
    /// </summary>
    public float SurfaceSeparation { get; init; } = 0.15f;

    /// <summary>
    /// Gets the aerodynamic pitch damping of the strips as a multiple of thin-airfoil theory (quasi-steady
    /// Cm = −π/8 · ωc/V about the quarter chord, applied as a couple on the leading and trailing edge, so it only removes
    /// energy); 0 turns it off. The strip flow is sampled at the center of pressure, which doesn't see the rotation.
    /// </summary>
    public float PitchDamping { get; init; } = 1;

    /// <summary>
    /// Gets how fast the pilot's inputs can change (full travel per second; letting go is twice as fast): hands and feet
    /// don't move instantly, and a step input yanks the light canopy around. 0 applies the inputs at once.
    /// </summary>
    public float HandSpeed { get; init; } = 2.5f;

    /// <summary>
    /// Gets the share (0–1) of the stretching velocity a taut line or riser loses every substep, after the positions are
    /// solved (stiff lines barely bounce back). Off by default: it changes nothing measurable in flight and costs time.
    /// </summary>
    public float LineDamping { get; init; }

    /// <summary>
    /// Gets the share (0–1) of the stretching velocity stretched canopy fabric and ribs lose every substep. Off by default;
    /// turn it on (1) with <see cref="EnclosedAir"/> on the High proxy, which otherwise flutters and tucks at 16 substeps.
    /// </summary>
    public float FabricDamping { get; init; }

    /// <summary>Gets the extra drag coefficient of an empty cell (a crumpled bag of fabric).</summary>
    public float DeflatedDrag { get; init; } = 0.3f;
}

/// <summary>
/// Simulates a paraglider from its physics proxy: XPBD (extended position based dynamics) on the nodes and
/// constraints, with aerodynamic forces per strip from the section polar, ram-air pressure per cell, line and pilot
/// drag, and the pilot's controls (brakes, speed bar, weight shift, collapse lines).
/// </summary>
/// <remarks>
/// <para>
/// Each step runs <see cref="SimulatorSettings.Substeps"/> substeps of: compute the external forces (gravity,
/// aerodynamics, pressure, drag), integrate velocities and positions, one pass over the constraints (tension-only lines skip when slack; fabric resists compression only weakly), and update velocities from
/// the positions.
/// </para>
/// <para>
/// Aerodynamics per strip: the flow perpendicular to the span gives the angle of attack against the strip's chord
/// (nose to tail). A trailing edge pulled down (brakes) adds a flap term. The lift slope is reduced with the induced
/// angle of attack of a wing of the proxy's aspect ratio. Lift and drag are spread over the strip's nodes around the
/// center of pressure (from the moment coefficient).
/// </para>
/// <para>
/// Ram air: each cell's pressure follows a target with <see cref="ProxyModel.CellPressureTimeConstant"/>: the internal
/// pressure coefficient while the inlet sees the flow (angle of attack above <see cref="ProxyModel.InletClosingAlpha"/>),
/// slight suction otherwise; closed tip cells and neighbors share pressure through the cross-vents. Pressure pushes the
/// cell's surfaces out; without it the cell folds under the line and air loads, which is how tucks and collapses happen.
/// </para>
/// <para>
/// Fabric firmness: canopy constraints with a <see cref="ProxyConstraint.DeflatedCompliance"/> are stiff against
/// compression and bending while their cells are inflated and limp when they are empty (a geometric blend by the cells'
/// inflation), so an inflated wing keeps its shape and a deflated part folds like fabric. The inlets see the angle of
/// attack of the nose itself and close as it flattens; pulled A lines close the cells they hold
/// (<see cref="ProxyControl.Strips"/>). See docs/ProxyFormat.md for the formulas.
/// </para>
/// </remarks>
public sealed class GliderSimulator
{
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);

    private readonly ProxyModel _model;
    private readonly SimulatorSettings _settings;
    private readonly int _count;
    private readonly float[] _inverseMass;
    private readonly Vector3[] _weight;
    private readonly Vector3[] _positions;
    private readonly Vector3[] _previous;
    private readonly Vector3[] _velocities;
    private readonly Vector3[] _forces;

    private readonly int[] _ca, _cb;
    private readonly float[] _rest, _effectiveRest, _compliance, _compression, _deflated;
    private readonly bool[] _bend;
    private readonly float[] _damping;
    private readonly int[] _nodeStripA, _nodeStripB;
    private readonly float[] _stripFirmness, _nodeFirmness, _stripStiffening, _nodeStiffening;
    private readonly float _trimDynamicPressure;
    private readonly bool[] _tensionOnly;
    private readonly SurfacePair[] _pairs;
    private readonly List<int> _crossed = [];
    private readonly float[] _dragDiameter;
    private readonly int[] _lineConstraints;
    private readonly (string Name, int[] Constraints, float[] Travel, int[] Strips)[] _controls;
    private float[] _closure = [];
    private readonly int[] _pilotHarness = new int[2];

    private readonly StripData[] _strips;
    private readonly float[] _pressure;
    private readonly double _liftSlope;
    private readonly double _stallAlpha;

    private sealed class StripData
    {
        public int LeA, TeA, LeB, TeB, MidA, MidB;
        public int ThroatUpperA, ThroatLowerA, ThroatUpperB, ThroatLowerB;
        public float RestNoseAngle;
        public float InletAlpha;
        public float RestThroatHeight;
        public float InletOpen = 1;
        public int[] Nodes = [];
        public float[] X = [];
        public float[] BaseWeight = [];
        public int[] Surface = [];
        public bool HasInlet;
        public float RestFlap;
        public float Alpha;
        public float Airspeed;
        public float CenterOfPressure = 0.3f;
        public int Index;
    }

    /// <summary>Initializes a simulator for <paramref name="model"/>, at rest in its trim glide.</summary>
    public GliderSimulator(ProxyModel model, SimulatorSettings? settings = null)
    {
        _model = model;
        _settings = settings ?? new SimulatorSettings();
        _count = model.Nodes.Count;
        _inverseMass = model.Nodes.Select(n => n.Mass > 0 ? 1 / n.Mass : 0).ToArray();
        _weight = model.Nodes.Select(n => Gravity * MathF.Max(0, n.Mass)).ToArray();
        _positions = new Vector3[_count];
        _previous = new Vector3[_count];
        _velocities = new Vector3[_count];
        _forces = new Vector3[_count];

        int constraints = model.Constraints.Count;
        _ca = new int[constraints];
        _cb = new int[constraints];
        _rest = new float[constraints];
        _effectiveRest = new float[constraints];
        _compliance = new float[constraints];
        _compression = new float[constraints];
        _tensionOnly = new bool[constraints];
        _deflated = new float[constraints];
        _bend = new bool[constraints];
        _damping = new float[constraints];
        _dragDiameter = new float[constraints];
        for (int i = 0; i < constraints; i++)
        {
            var c = model.Constraints[i];
            _ca[i] = c.A;
            _cb[i] = c.B;
            _rest[i] = c.RestLength;
            _compliance[i] = c.Compliance;
            _compression[i] = c.CompressionCompliance;
            _deflated[i] = c.DeflatedCompliance;
            _bend[i] = c.Kind == ConstraintKind.Bend;
            _damping[i] = c.Kind switch
            {
                ConstraintKind.Line or ConstraintKind.Riser => _settings.LineDamping,
                ConstraintKind.Chordwise or ConstraintKind.Spanwise or ConstraintKind.Shear or ConstraintKind.Rib => _settings.FabricDamping,
                _ => 0,
            };
            _tensionOnly[i] = c.TensionOnly;
            _dragDiameter[i] = c.DragDiameter;
        }
        _lineConstraints = Enumerable.Range(0, constraints).Where(i => model.Constraints[i].Kind is ConstraintKind.Line or ConstraintKind.Riser).ToArray();
        _controls = model.Controls.Select(c => (c.Name, c.Constraints.ToArray(), c.Travel.ToArray(), c.Strips.ToArray())).ToArray();

        // The pilot's two harness constraints to the carabiners (left first), for weight shift.
        var harness = Enumerable.Range(0, constraints)
            .Where(i => model.Constraints[i].Kind == ConstraintKind.Harness && (_ca[i] == model.Pilot || _cb[i] == model.Pilot))
            .OrderByDescending(i => model.Nodes[_ca[i] == model.Pilot ? _cb[i] : _ca[i]].Position.X)
            .ToArray();
        if (harness.Length >= 2)
        {
            _pilotHarness[0] = harness[0];
            _pilotHarness[1] = harness[1];
        }

        // The lift slope (per radian) and the stall angle of the polar, for the flap lift.
        _liftSlope = (Polar(5).Lift - Polar(0).Lift) / (5 * Math.PI / 180);
        _stallAlpha = Enumerable.Range(0, 40).MaxBy(a => Polar(a).Lift);
        _strips = model.Strips.Select(BuildStrip).ToArray();
        _pairs = BuildSurfacePairs(model);
        for (int s = 0; s < _strips.Length; s++) _strips[s].Index = s;
        _pressure = new float[_strips.Length];

        // The air inside the cells moves with the canopy: inertia (several kilograms, more than the fabric) but no
        // weight, spread over each cell's nodes.
        if (_settings.EnclosedAir)
        {
            var mass = model.Nodes.Select(n => (double)n.Mass).ToArray();
            foreach (var strip in model.Strips)
            {
                var a = model.Sections[strip.SectionA];
                var b = model.Sections[strip.SectionB];
                if (a.Upper.Count == 0) continue; // a single surface holds no air
                var (areaA, centerA) = SectionArea(a);
                var (areaB, centerB) = SectionArea(b);
                double volume = 0.5 * (areaA + areaB) * Vector3.Distance(centerA, centerB);
                var nodes = ProxyBuilder.Chain(a, true).Concat(ProxyBuilder.Chain(a, false)).Concat(ProxyBuilder.Chain(b, true)).Concat(ProxyBuilder.Chain(b, false)).Distinct().ToList();
                foreach (int n in nodes) mass[n] += _settings.AirDensity * volume / nodes.Count;
            }
            for (int i = 0; i < _count; i++) _inverseMass[i] = mass[i] > 0 ? (float)(1 / mass[i]) : 0;
        }

        // The strips on either side of each canopy node's section, whose pressure makes its fabric firm.
        _nodeStripA = new int[_count];
        _nodeStripB = new int[_count];
        for (int i = 0; i < _count; i++)
        {
            int section = model.Nodes[i].Section;
            bool canopy = section >= 0 && model.Nodes[i].Kind is ProxyNodeKind.Upper or ProxyNodeKind.Lower or ProxyNodeKind.Camber;
            _nodeStripA[i] = canopy ? Math.Clamp(section - 1, 0, Math.Max(0, _strips.Length - 1)) : -1;
            _nodeStripB[i] = canopy ? Math.Clamp(section, 0, Math.Max(0, _strips.Length - 1)) : -1;
        }
        _stripFirmness = new float[_strips.Length];
        _nodeFirmness = new float[_count];
        _stripStiffening = new float[_strips.Length];
        _nodeStiffening = new float[_count];
        float trimSpeed = model.TrimAirspeed > 0 ? model.TrimAirspeed : 10f;
        _trimDynamicPressure = 0.5f * _settings.AirDensity * trimSpeed * trimSpeed;
        Reset();
    }

    /// <summary>Gets the proxy.</summary>
    public ProxyModel Model => _model;

    /// <summary>Gets the pilot's inputs.</summary>
    public SimulationInputs Inputs { get; } = new();

    /// <summary>
    /// Gets the node positions relative to <see cref="Origin"/>. The simulation keeps the glider near the origin (floats
    /// lose the precision the stiff constraints need hundreds of meters away), so world positions are
    /// <see cref="Origin"/> plus these.
    /// </summary>
    public ReadOnlySpan<Vector3> Positions => _positions;

    /// <summary>Gets the world position (m, double precision) the node positions are relative to.</summary>
    public (double X, double Y, double Z) Origin { get; private set; }

    /// <summary>Gets the node velocities.</summary>
    public ReadOnlySpan<Vector3> Velocities => _velocities;

    /// <summary>Gets the simulated time (s).</summary>
    public float Time { get; private set; }

    /// <summary>Gets the pressure of each strip's cell, from −0.25 (sucked in) to 1 (fully inflated).</summary>
    public ReadOnlySpan<float> CellPressure => _pressure;

    /// <summary>Gets the pilot's position.</summary>
    public Vector3 PilotPosition => _positions[_model.Pilot];

    /// <summary>Gets the pilot's velocity.</summary>
    public Vector3 PilotVelocity => _velocities[_model.Pilot];

    /// <summary>Gets the pilot's airspeed (m/s).</summary>
    public float Airspeed => (Inputs.Wind - PilotVelocity).Length();

    /// <summary>Gets the vertical speed (m/s, negative when sinking).</summary>
    public float VerticalSpeed => PilotVelocity.Y;

    /// <summary>Gets the glide ratio over the ground (horizontal speed / sink).</summary>
    public float GlideRatio
    {
        get
        {
            var v = PilotVelocity;
            float horizontal = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            return v.Y < -0.01f ? horizontal / -v.Y : float.PositiveInfinity;
        }
    }

    /// <summary>Gets the total aerodynamic forces of the last substep (N): canopy lift and drag, line drag, pilot drag.</summary>
    public (Vector3 CanopyLift, Vector3 CanopyDrag, Vector3 LineDrag, Vector3 PilotDrag) Forces => (_canopyLift, _canopyDrag, _lineDrag, _pilotDrag);

    private Vector3 _canopyLift, _canopyDrag, _lineDrag, _pilotDrag;
    private (float Profile, float Induced, float Flap, float Deflated, float LiftSum) _dragTerms;

    /// <summary>Gets the canopy drag of the last substep by term (N, sums of magnitudes) and the sum of the strips' lift magnitudes (diagnostics).</summary>
    public (float Profile, float Induced, float Flap, float Deflated, float LiftSum) CanopyDragTerms => _dragTerms;

    /// <summary>Gets the power (W) the canopy aerodynamics and the cell pressure did in the last substep (diagnostics).</summary>
    public float AeroPower { get; private set; }

    /// <inheritdoc cref="AeroPower"/>
    public float PressurePower { get; private set; }

    /// <summary>Gets the power (W) of all forces but gravity on all nodes in the last substep, relative to the air (diagnostics).</summary>
    public float ForcePower { get; private set; }

    /// <summary>Gets the angle of attack (degrees) of the center strip.</summary>
    public float CenterAngleOfAttack => _strips.Length > 0 ? _strips[_strips.Length / 2].Alpha : 0;

    /// <summary>Gets the angle of attack (degrees) of strip <paramref name="strip"/> in the last substep.</summary>
    public float StripAngleOfAttack(int strip) => _strips[strip].Alpha;

    /// <summary>Gets the angle of attack (degrees) of strip <paramref name="strip"/>'s inlet: of the nose, which differs when it is folded.</summary>
    public float StripInletAngle(int strip) => _strips[strip].InletAlpha;

    /// <summary>
    /// Gets how many chord stations had the upper skin below the lower one (across the local fabric) in the last substep,
    /// before the separation corrected them.
    /// </summary>
    public int SurfaceCrossings { get; private set; }

    /// <summary>Gets the upper skin nodes of the stations counted in <see cref="SurfaceCrossings"/>.</summary>
    public IReadOnlyList<int> CrossedNodes => _crossed;

    /// <summary>Puts the glider back in its rest shape, flying at the trim airspeed along the trim glide path.</summary>
    public void Reset()
    {
        float speed = _model.TrimAirspeed > 0 ? _model.TrimAirspeed : 10f;
        float gamma = _model.TrimFlightPathAngle * MathF.PI / 180;
        var velocity = new Vector3(0, -MathF.Sin(gamma), MathF.Cos(gamma)) * speed;
        for (int i = 0; i < _count; i++)
        {
            _positions[i] = _model.Nodes[i].Position;
            _velocities[i] = velocity;
        }
        Array.Fill(_pressure, 1f);
        Origin = default;
        _brakeLeft = _brakeRight = _speedBar = _collapseLeft = _collapseRight = _frontal = _bigEars = _weightShift = 0;
        Time = 0;
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds (one step; call with a fixed step for stable results).</summary>
    public void Step(float dt)
    {
        if (dt <= 0) return;
        ApplyControls(dt);

        int substeps = Math.Max(1, _settings.Substeps);
        float h = dt / substeps;
        float damping = MathF.Max(0, 1 - _settings.Damping * h);
        for (int s = 0; s < substeps; s++)
        {
            // Forces every substep: the aerodynamic damping of the light canopy nodes is too stiff for a frame-long step.
            ComputeForces(h);
            UpdateFirmness();
            for (int i = 0; i < _count; i++)
            {
                _previous[i] = _positions[i];
                if (_inverseMass[i] == 0) continue;
                _velocities[i] += _forces[i] * (_inverseMass[i] * h);
                _positions[i] += _velocities[i] * h;
            }
            SolveConstraints(h);
            SeparateSurfaces();
            float invH = 1 / h;
            float invMass = 0;
            var momentum = Vector3.Zero;
            for (int i = 0; i < _count; i++)
            {
                _velocities[i] = (_positions[i] - _previous[i]) * invH;
                float mass = _inverseMass[i] > 0 ? 1 / _inverseMass[i] : 0;
                momentum += _velocities[i] * mass;
                invMass += mass;
            }
            // Damping only of the motion relative to the center of mass, so it doesn't act like drag.
            var center = invMass > 0 ? momentum / invMass : Vector3.Zero;
            for (int i = 0; i < _count; i++) _velocities[i] = center + (_velocities[i] - center) * damping;
            if (_settings.LineDamping > 0 || _settings.FabricDamping > 0) DampConstraints();
        }
        Time += dt;
        Rebase();
    }

    // Moves the glider back to the origin once the pilot is more than a few meters away, keeping float precision.
    private void Rebase()
    {
        var pilot = _positions[_model.Pilot];
        if (MathF.Abs(pilot.X) + MathF.Abs(pilot.Y) + MathF.Abs(pilot.Z) < 16) return;
        var shift = new Vector3(MathF.Round(pilot.X), MathF.Round(pilot.Y), MathF.Round(pilot.Z));
        for (int i = 0; i < _count; i++) _positions[i] -= shift;
        Origin = (Origin.X + shift.X, Origin.Y + shift.Y, Origin.Z + shift.Z);
    }

    // The inputs as applied: they follow the pilot's at hand speed.
    private float _brakeLeft, _brakeRight, _speedBar, _collapseLeft, _collapseRight, _frontal, _bigEars, _weightShift;

    private float Follow(float applied, float target, float dt)
    {
        float speed = _settings.HandSpeed;
        if (speed <= 0) return target;
        // Letting go is twice as fast as pulling; for weight shift, moving toward the center is.
        float rate = (MathF.Abs(target) < MathF.Abs(applied) ? 2 : 1) * speed * dt;
        return applied + Math.Clamp(target - applied, -rate, rate);
    }

    private void ApplyControls(float dt)
    {
        _brakeLeft = Follow(_brakeLeft, Math.Clamp(Inputs.BrakeLeft, 0, 1), dt);
        _brakeRight = Follow(_brakeRight, Math.Clamp(Inputs.BrakeRight, 0, 1), dt);
        _speedBar = Follow(_speedBar, Math.Clamp(Inputs.SpeedBar, 0, 1), dt);
        _collapseLeft = Follow(_collapseLeft, Math.Clamp(Inputs.CollapseLeft, 0, 1), dt);
        _collapseRight = Follow(_collapseRight, Math.Clamp(Inputs.CollapseRight, 0, 1), dt);
        _frontal = Follow(_frontal, Math.Clamp(Inputs.Frontal, 0, 1), dt);
        _bigEars = Follow(_bigEars, Math.Clamp(Inputs.BigEars, 0, 1), dt);
        _weightShift = Follow(_weightShift, Math.Clamp(Inputs.WeightShift, -1, 1), dt);
        Array.Copy(_rest, _effectiveRest, _rest.Length);
        if (_closure.Length != _strips.Length) _closure = new float[_strips.Length];
        Array.Clear(_closure);
        foreach (var (name, constraints, travel, strips) in _controls)
        {
            float input = name switch
            {
                "BrakeLeft" => _brakeLeft,
                "BrakeRight" => _brakeRight,
                "SpeedBar" => _speedBar,
                "CollapseLeft" => _collapseLeft,
                "CollapseRight" => _collapseRight,
                "Frontal" => _frontal,
                "BigEarsLeft" or "BigEarsRight" => _bigEars,
                _ => 0,
            };
            if (input <= 0) continue;
            input = Math.Clamp(input, 0, 1);
            for (int k = 0; k < constraints.Length; k++)
            {
                int c = constraints[k];
                _effectiveRest[c] = MathF.Max(_rest[c] * 0.05f, _effectiveRest[c] - travel[k] * input);
            }
            foreach (int s in strips)
            {
                if (s < _closure.Length) _closure[s] = MathF.Max(_closure[s], input);
            }
        }
        float shift = _weightShift;
        if (shift != 0 && _pilotHarness[0] != _pilotHarness[1])
        {
            _effectiveRest[_pilotHarness[0]] = _rest[_pilotHarness[0]] * (1 - 0.12f * shift);
            _effectiveRest[_pilotHarness[1]] = _rest[_pilotHarness[1]] * (1 + 0.12f * shift);
        }
    }

    // The area (m²) and centroid of a section's profile at rest: the polygon of its upper chain, then its lower chain back.
    private (double Area, Vector3 Center) SectionArea(ProxySection section)
    {
        var outline = ProxyBuilder.Chain(section, true).Concat(Enumerable.Reverse(ProxyBuilder.Chain(section, false))).Select(i => _model.Nodes[i].Position).ToList();
        var center = Vector3.Zero;
        foreach (var p in outline) center += p;
        center /= outline.Count;
        var sum = Vector3.Zero;
        for (int i = 0; i < outline.Count; i++) sum += Vector3.Cross(outline[i] - center, outline[(i + 1) % outline.Count] - center);
        return (0.5 * sum.Length(), center);
    }

    // How firm each cell is: its pressure (the share of full inflation) times its dynamic pressure relative to trim (a
    // slow, stalled wing is soft); the fabric of a node gets the mean of the cells on either side.
    private void UpdateFirmness()
    {
        float rho = _settings.AirDensity;
        for (int s = 0; s < _strips.Length; s++)
        {
            float q = 0.5f * rho * _strips[s].Airspeed * _strips[s].Airspeed;
            // Below about a sixth of its pressure a cell is limp; from about 85 % it holds its full shape.
            float inflation = Math.Clamp((_pressure[s] - 0.15f) / 0.7f, 0, 1);
            inflation = inflation * inflation * (3 - 2 * inflation);
            _stripFirmness[s] = inflation;
            // An inflated cell is as stiff as its pressure, which follows the dynamic pressure (not below half of trim's, so a
            // wing that stops for a moment keeps some shape).
            float exponent = _settings.FirmnessAirspeedExponent;
            _stripStiffening[s] = exponent > 0 ? Math.Clamp(MathF.Pow(q / _trimDynamicPressure, exponent), 0.5f, 4f) : 1;
        }
        for (int i = 0; i < _count; i++)
        {
            if (_nodeStripA[i] < 0)
            {
                _nodeFirmness[i] = _nodeStiffening[i] = 1;
                continue;
            }
            _nodeFirmness[i] = 0.5f * (_stripFirmness[_nodeStripA[i]] + _stripFirmness[_nodeStripB[i]]);
            _nodeStiffening[i] = 0.5f * (_stripStiffening[_nodeStripA[i]] + _stripStiffening[_nodeStripB[i]]);
        }
    }

    // An upper and a lower skin node at the same chord station of a section, and the nodes whose midpoints give the local
    // frame of the fabric there: the chord direction (the stations before and after) and the span direction (the same
    // station on the neighboring sections). The frame folds with the fabric, so a folded cell isn't mistaken for a
    // crossing.
    private readonly record struct SurfacePair(
        int U, int L,
        int ChordA0, int ChordA1, int ChordB0, int ChordB1,
        int SpanA0, int SpanA1, int SpanB0, int SpanB1,
        float Sign, float RestGap, float RestFrame);

    private static SurfacePair[] BuildSurfacePairs(ProxyModel model)
    {
        var pairs = new List<SurfacePair>();
        var rest = model.Nodes.Select(n => n.Position).ToArray();
        var sections = model.Sections;
        for (int s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            if (section.Upper.Count == 0 || section.Upper.Count != section.Lower.Count) continue;
            var before = sections[Math.Max(0, s - 1)];
            var after = sections[Math.Min(sections.Count - 1, s + 1)];
            if (before.Upper.Count != section.Upper.Count || after.Upper.Count != section.Upper.Count) continue;
            int last = section.Upper.Count - 1;
            for (int k = 0; k <= last; k++)
            {
                var pair = new SurfacePair(
                    section.Upper[k], section.Lower[k],
                    k > 0 ? section.Upper[k - 1] : section.LeadingEdge, k > 0 ? section.Lower[k - 1] : section.LeadingEdge,
                    k < last ? section.Upper[k + 1] : section.TrailingEdge, k < last ? section.Lower[k + 1] : section.TrailingEdge,
                    before.Upper[k], before.Lower[k], after.Upper[k], after.Lower[k],
                    1, 0, 0);
                var (up, frame) = FabricNormal(pair, rest);
                float gap = frame > 0 ? Vector3.Dot(rest[pair.U] - rest[pair.L], up) : 0;
                if (Math.Abs(gap) < 1e-4f) continue;
                pairs.Add(pair with { Sign = Math.Sign(gap), RestGap = Math.Abs(gap), RestFrame = frame });
            }
        }
        return [.. pairs];
    }

    // The unit normal of the fabric at a pair (span × chord between the neighbors' midpoints) and the length of the
    // cross product (small when the fabric around it is crumpled and the frame is unreliable).
    private static (Vector3 Up, float Frame) FabricNormal(in SurfacePair pair, ReadOnlySpan<Vector3> p)
    {
        var chord = (p[pair.ChordB0] + p[pair.ChordB1] - p[pair.ChordA0] - p[pair.ChordA1]) * 0.5f;
        var span = (p[pair.SpanB0] + p[pair.SpanB1] - p[pair.SpanA0] - p[pair.SpanA1]) * 0.5f;
        var up = Vector3.Cross(span, chord);
        float frame = up.Length();
        return frame > 1e-9f ? (up / frame, frame) : (Vector3.Zero, 0);
    }

    // Keeps the upper skin above the lower one at every chord station (a one-sided constraint along the fabric's normal,
    // after the distance constraints): limp, empty cells would otherwise let a skin pass through the other, and the
    // pressure on the inverted pocket would keep it there.
    private void SeparateSurfaces()
    {
        float fraction = _settings.SurfaceSeparation;
        int crossings = 0;
        _crossed.Clear();
        for (int i = 0; i < _pairs.Length; i++)
        {
            ref readonly var pair = ref _pairs[i];
            var (up, frame) = FabricNormal(pair, _positions);
            // Where the fabric around the station is crumpled, the frame says nothing about which side is up.
            if (frame < 0.25f * pair.RestFrame) continue;
            float gap = pair.Sign * Vector3.Dot(_positions[pair.U] - _positions[pair.L], up);
            if (gap < 0)
            {
                crossings++;
                _crossed.Add(pair.U);
            }
            float minimum = fraction * pair.RestGap;
            if (gap >= minimum || fraction <= 0) continue;
            float wu = _inverseMass[pair.U], wl = _inverseMass[pair.L];
            float w = wu + wl;
            if (w <= 0) continue;
            var push = up * (pair.Sign * (minimum - gap) / w);
            _positions[pair.U] += push * wu;
            _positions[pair.L] -= push * wl;
        }
        SurfaceCrossings = crossings;
    }

    // Damps taut lines and stretched fabric on the velocities, after the positions are solved: the relative velocity along
    // each such constraint loses the configured share (stiff lines and ripstop are nearly inelastic). Damping the positions
    // instead (XPBD damping) fights the solver's own corrections running down the line chains in its single pass, which
    // softens the lines and pitches the canopy back.
    private void DampConstraints()
    {
        for (int i = 0; i < _ca.Length; i++)
        {
            float share = _damping[i];
            if (share <= 0) continue;
            int a = _ca[i], b = _cb[i];
            float wa = _inverseMass[a], wb = _inverseMass[b];
            float w = wa + wb;
            if (w == 0) continue;
            var d = _positions[a] - _positions[b];
            float length = d.Length();
            // Only while taut (lines) or stretched (fabric).
            if (length < 1e-9f || length < _effectiveRest[i]) continue;
            var n = d / length;
            float relative = Vector3.Dot(_velocities[a] - _velocities[b], n);
            float fraction = Math.Clamp(share, 0, 1);
            var impulse = n * (relative * fraction / w);
            _velocities[a] -= impulse * wa;
            _velocities[b] += impulse * wb;
        }
    }

    // One Gauss-Seidel pass in a fixed order (alternating the direction every substep makes stiff networks oscillate).
    private void SolveConstraints(float h)
    {
        float invH2 = 1 / (h * h);
        int n = _ca.Length;
        for (int k = 0; k < n; k++)
        {
            int i = k;
            int a = _ca[i], b = _cb[i];
            float wa = _inverseMass[a], wb = _inverseMass[b];
            float w = wa + wb;
            if (w == 0) continue;
            var d = _positions[a] - _positions[b];
            float length = d.Length();
            if (length < 1e-9f) continue;
            float c = length - _effectiveRest[i];
            if (c < 0 && _tensionOnly[i]) continue;
            float alpha = c < 0 ? _compression[i] : _compliance[i];
            if (_deflated[i] > 0 && (c < 0 || _bend[i]))
            {
                // Inflated fabric holds its shape, empty fabric folds: blend the compliance (geometrically) by the cells' firmness.
                float firm = 0.5f * (_nodeFirmness[a] + _nodeFirmness[b]);
                alpha /= 0.5f * (_nodeStiffening[a] + _nodeStiffening[b]);
                alpha = firm >= 1 ? alpha : firm <= 0 ? _deflated[i] : MathF.Exp(firm * MathF.Log(alpha) + (1 - firm) * MathF.Log(_deflated[i]));
            }
            alpha *= invH2;
            float lambda = -c / (w + alpha);
            var correction = d * (lambda / length);
            _positions[a] += correction * wa;
            _positions[b] -= correction * wb;
        }
    }

    private void ComputeForces(float dt)
    {
        float rho = _settings.AirDensity;
        var wind = Inputs.Wind;
        Array.Copy(_weight, _forces, _count);

        _canopyLift = _canopyDrag = _lineDrag = _pilotDrag = Vector3.Zero;
        _dragTerms = default;
        AeroPower = PressurePower = 0;
        if (!_settings.Aerodynamics) { UpdatePressure(dt); return; }

        // Line and riser drag, perpendicular to each line.
        foreach (int c in _lineConstraints)
        {
            int a = _ca[c], b = _cb[c];
            var axis = _positions[b] - _positions[a];
            float length = axis.Length();
            if (length < 1e-6f || _dragDiameter[c] <= 0) continue;
            axis /= length;
            var air = wind - (_velocities[a] + _velocities[b]) * 0.5f;
            var normal = air - axis * Vector3.Dot(air, axis);
            var force = normal * (0.5f * rho * normal.Length() * _model.LineDragCoefficient * _dragDiameter[c] * length);
            _lineDrag += force;
            _forces[a] += force * 0.5f;
            _forces[b] += force * 0.5f;
        }

        // The pilot.
        var pilotAir = wind - _velocities[_model.Pilot];
        _forces[_model.Pilot] += pilotAir * (0.5f * rho * pilotAir.Length() * _model.PilotDragArea);
        _pilotDrag = pilotAir * (0.5f * rho * pilotAir.Length() * _model.PilotDragArea);

        float aspect = MathF.Max(1, _model.InducedAspectRatio > 0 ? _model.InducedAspectRatio : _model.ProjectedAspectRatio);
        if (_settings.Aerodynamics) for (int s = 0; s < _strips.Length; s++) ApplyStripAerodynamics(_strips[s], rho, wind, aspect);
        UpdatePressure(dt);
        if (_settings.Pressure) for (int s = 0; s < _strips.Length; s++) ApplyPressure(_strips[s], _pressure[s], rho);
        float power = 0;
        for (int i = 0; i < _count; i++) power += Vector3.Dot(_forces[i] - _weight[i], _velocities[i] - wind);
        ForcePower = power;
    }


    private void ApplyStripAerodynamics(StripData strip, float rho, Vector3 wind, float aspect)
    {
        var p = _positions;
        var leA = p[strip.LeA]; var teA = p[strip.TeA];
        var leB = p[strip.LeB]; var teB = p[strip.TeB];
        var span = (leB + (teB - leB) * 0.25f) - (leA + (teA - leA) * 0.25f);
        float spanLength = span.Length();
        if (spanLength < 1e-4f) return;
        var spanDir = span / spanLength;
        var le = (leA + leB) * 0.5f;
        var te = (teA + teB) * 0.5f;
        var chordVec = te - le;
        var chordInPlane = chordVec - spanDir * Vector3.Dot(chordVec, spanDir);
        float chordLength = chordInPlane.Length();
        if (chordLength < 1e-4f) return;
        var chordDir = chordInPlane / chordLength;
        // Sections run from the right tip to the left: span × chord points to the upper surface.
        var up = Vector3.Cross(spanDir, chordDir);

        // The load weights over the strip's nodes (around the last center of pressure). The flow velocity is averaged
        // with the same weights the force is spread with, so lift does no work on the strip's own motion (a flow
        // averaged otherwise lets lift pump energy into rotations).
        Span<float> weights = stackalloc float[strip.Nodes.Length];
        if (!LoadWeights(strip, strip.CenterOfPressure, weights)) return;
        var velocity = Vector3.Zero;
        for (int k = 0; k < weights.Length; k++) velocity += _velocities[strip.Nodes[k]] * weights[k];
        var air = wind - velocity;
        var flow = air - spanDir * Vector3.Dot(air, spanDir);
        float speed = flow.Length();
        strip.Airspeed = speed;
        if (speed < 0.1f) return;
        var flowDir = flow / speed;

        float alpha = MathF.Atan2(Vector3.Dot(flow, up), Vector3.Dot(flow, chordDir));
        float alphaDegrees = alpha * 180 / MathF.PI;
        // A trailing edge pulled down (brakes) acts like a plain flap: more lift while the flow is attached, more drag,
        // and the center of pressure moves back. (The chord from nose to tail already turns with the trailing edge.)
        var mid = (p[strip.MidA] + p[strip.MidB]) * 0.5f;
        float flap = FlapAngle(le, mid, te, chordDir, up) - strip.RestFlap;

        // Lifting line: the induced angle follows from the lift it leaves, αi = Cl(α − αi) / (π·AR·e). A few damped
        // fixed-point iterations (one step from Cl(α) would overestimate the induced drag by half).
        double inducedFactor = 1 / (Math.PI * aspect * Math.Max(0.3, _model.SpanEfficiency));
        float induced = 0;
        double flapLift = 0;
        for (int iteration = 0; iteration < 4; iteration++)
        {
            double effective = alphaDegrees - induced * 180 / MathF.PI;
            var (clIteration, _, _) = Polar(effective);
            flapLift = FlapLift(effective, flap);
            induced = 0.5f * induced + 0.5f * (float)((clIteration + flapLift) * inducedFactor);
        }
        var (cl, cd, cm) = Polar(alphaDegrees - induced * 180 / MathF.PI);
        double profileCd = cd;
        cl += flapLift;
        // A deflated cell is a crumpled bag, not an airfoil: it loses most of its lift and drags more.
        double inflation = Math.Clamp(_pressure[strip.Index], 0, 1);
        cl *= 0.25 + 0.75 * inflation;
        cd += _settings.DeflatedDrag * (1 - inflation);
        cm -= 0.3 * flapLift;
        cd += 0.6 * flap * flap + cl * induced;
        strip.Alpha = alphaDegrees;
        // The inlet faces along the nose (from the throat behind the inlet to the leading edge): a nose folded down or
        // under closes it even when the rest of the cell still looks like a profile.
        var throat = (p[strip.ThroatUpperA] + p[strip.ThroatLowerA] + p[strip.ThroatUpperB] + p[strip.ThroatLowerB]) * 0.25f;
        var noseAxis = throat - le;
        float noseAngle = MathF.Atan2(Vector3.Dot(noseAxis, up), Vector3.Dot(noseAxis, chordDir));
        strip.InletAlpha = alphaDegrees - (noseAngle - strip.RestNoseAngle) * 180 / MathF.PI;
        // A flattened nose (a crumpled cell) closes the inlet: it opens again as the flow lifts the lips apart.
        if (strip.RestThroatHeight > 0)
        {
            float height = 0.5f * (Vector3.Distance(p[strip.ThroatUpperA], p[strip.ThroatLowerA]) + Vector3.Distance(p[strip.ThroatUpperB], p[strip.ThroatLowerB]));
            float open = Math.Clamp((height / strip.RestThroatHeight - 0.25f) / 0.5f, 0, 1);
            strip.InletOpen = open * open * (3 - 2 * open);
        }

        var liftDir = up - flowDir * Vector3.Dot(up, flowDir);
        float liftLength = liftDir.Length();
        if (liftLength < 1e-6f) return;
        liftDir /= liftLength;

        float q = 0.5f * rho * speed * speed;
        float area = chordLength * spanLength;
        var force = (liftDir * (float)cl + flowDir * (float)cd) * (q * area);
        _canopyLift += liftDir * (float)(cl * q * area);
        _canopyDrag += flowDir * (float)(cd * q * area);
        _dragTerms.Profile += (float)(profileCd * q * area);
        _dragTerms.Induced += (float)(cl * induced * q * area);
        _dragTerms.Flap += 0.6f * flap * flap * q * area;
        _dragTerms.Deflated += (float)(_settings.DeflatedDrag * (1 - inflation) * q * area);
        _dragTerms.LiftSum += (float)(Math.Abs(cl) * q * area);

        // Pitch damping: a strip rotating nose-up (its leading edge rising against its trailing edge) meets a nose-down
        // moment, −π/8·ωc/V of q·S·c (thin airfoil, quasi-steady), as a couple ±M/c on the leading and trailing edge.
        if (_settings.PitchDamping > 0)
        {
            var leVelocity = (_velocities[strip.LeA] + _velocities[strip.LeB]) * 0.5f;
            var teVelocity = (_velocities[strip.TeA] + _velocities[strip.TeB]) * 0.5f;
            float pitchRate = Vector3.Dot(leVelocity - teVelocity, up) / chordLength;
            float moment = _settings.PitchDamping * MathF.PI / 16 * rho * speed * area * chordLength * chordLength * pitchRate;
            var couple = up * (moment / chordLength * 0.5f);
            _forces[strip.LeA] -= couple;
            _forces[strip.LeB] -= couple;
            _forces[strip.TeA] += couple;
            _forces[strip.TeB] += couple;
            AeroPower -= moment * pitchRate;
        }
        for (int k = 0; k < weights.Length; k++) { _forces[strip.Nodes[k]] += force * weights[k]; AeroPower += Vector3.Dot(force * weights[k], _velocities[strip.Nodes[k]] - wind); }

        // The center of pressure for the next substep, from the moment coefficient.
        float cp = Math.Abs(cl) > 0.05 ? (float)Math.Clamp(0.25 - cm / cl, 0.05, 0.9) : 0.4f;
        strip.CenterOfPressure += (cp - strip.CenterOfPressure) * 0.2f;
    }

    // Weights (summing to 1) that spread a strip's force over its nodes with the given center of pressure: the
    // area-share base loading, tilted linearly along the chord to move its centroid.
    private static bool LoadWeights(StripData strip, float centerOfPressure, Span<float> w)
    {
        var x = strip.X;
        var b = strip.BaseWeight;
        float sum = 0, mean = 0;
        for (int k = 0; k < x.Length; k++)
        {
            sum += b[k];
            mean += b[k] * x[k];
        }
        if (sum <= 0) return false;
        mean /= sum;
        float variance = 0;
        for (int k = 0; k < x.Length; k++) variance += b[k] * (x[k] - mean) * (x[k] - mean);
        variance /= sum;
        float beta = variance > 1e-6f ? (centerOfPressure - mean) / variance : 0;
        float total = 0;
        for (int k = 0; k < x.Length; k++)
        {
            w[k] = MathF.Max(0, b[k] * (1 + beta * (x[k] - mean)));
            total += w[k];
        }
        if (total <= 0) return false;
        for (int k = 0; k < x.Length; k++) w[k] /= total;
        return true;
    }

    private void UpdatePressure(float dt)
    {
        float inflate = MathF.Max(0.05f, _model.CellPressureTimeConstant);
        float deflate = MathF.Max(0.05f, _model.CellDeflationTimeConstant);
        for (int s = 0; s < _strips.Length; s++)
        {
            var strip = _strips[s];
            float target;
            if (strip.HasInlet)
            {
                float ram = strip.Airspeed > 2 ? InletPressure(strip.InletAlpha) : 0;
                // Pulled A lines fold the leading edge under and close the inlets.
                if (_closure[s] > 0) ram += (-0.25f - ram) * _closure[s];
                target = ram > 0 ? ram * strip.InletOpen : ram;
            }
            else
            {
                // Closed cells fill through the cross-vents from their neighbors.
                float neighbors = 0;
                int count = 0;
                if (s > 0) { neighbors += _pressure[s - 1]; count++; }
                if (s + 1 < _strips.Length) { neighbors += _pressure[s + 1]; count++; }
                target = count > 0 ? neighbors / count : 0;
            }
            // Cells empty quickly and refill with the air the inlet takes in (faster at higher airspeeds).
            float tau = target < _pressure[s] ? deflate : inflate * Math.Clamp(10 / MathF.Max(strip.Airspeed, 1), 0.5f, 3f);
            _pressure[s] += (target - _pressure[s]) * MathF.Min(1, dt / tau);
        }
        // Cross-vents even out neighboring cells.
        float vent = MathF.Min(0.5f, dt / 0.8f);
        for (int s = 1; s < _strips.Length; s++)
        {
            float exchange = (_pressure[s] - _pressure[s - 1]) * vent * 0.5f;
            _pressure[s] -= exchange;
            _pressure[s - 1] += exchange;
        }
    }

    /// <summary>
    /// The pressure an inlet takes in at an angle of attack (fraction of the full internal pressure): full while the flow
    /// meets the inlet from the front and below; it closes as the flow comes over the nose (below
    /// <see cref="ProxyModel.InletClosingAlpha"/>, the cells are sucked empty), and with the flow from below a stalled wing
    /// keeps only part of its pressure; from behind it empties.
    /// </summary>
    public float InletPressure(float alpha)
    {
        float closing = _model.InletClosingAlpha;
        float stalled = _settings.StalledInletPressure;
        return alpha switch
        {
            _ when alpha <= closing - 3 => -0.25f,
            _ when alpha < closing => -0.25f + 1.25f * (alpha - (closing - 3)) / 3,
            <= 35 => 1,
            <= 60 => 1 - (1 - stalled) * (alpha - 35) / 25,
            <= 110 => stalled,
            <= 150 => stalled - (stalled + 0.25f) * (alpha - 110) / 40,
            _ => -0.25f,
        };
    }

    private void ApplyPressure(StripData strip, float pressure, float rho)
    {
        if (strip.Surface.Length == 0 || pressure == 0) return;
        float q = 0.5f * rho * strip.Airspeed * strip.Airspeed;
        float p = pressure * _model.InternalPressureCoefficient * q;
        var surface = strip.Surface;
        for (int t = 0; t < surface.Length; t += 3)
        {
            int a = surface[t], b = surface[t + 1], c = surface[t + 2];
            // Half the cross product is the area-weighted outward normal.
            var force = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]) * (0.5f * p / 3);
            _forces[a] += force;
            PressurePower += Vector3.Dot(force, _velocities[a] + _velocities[b] + _velocities[c]);
            _forces[b] += force;
            _forces[c] += force;
        }
    }

    private StripData BuildStrip(ProxyStrip strip)
    {
        var a = _model.Sections[strip.SectionA];
        var b = _model.Sections[strip.SectionB];
        var nodes = new List<int>();
        var xs = new List<float>();
        var weights = new List<float>();
        foreach (var section in new[] { a, b })
        {
            foreach (bool upper in section.Upper.Count > 0 ? new[] { true, false } : new[] { false })
            {
                var chain = ProxyBuilder.Chain(section, upper);
                var stations = new List<float> { 0 };
                stations.AddRange(section.Stations);
                stations.Add(1);
                for (int k = 0; k < chain.Count; k++)
                {
                    // The nose and tail are in both chains: count them once.
                    if (!upper && section.Upper.Count > 0 && (k == 0 || k == chain.Count - 1)) continue;
                    float x = stations[k];
                    float coverage = ((k + 1 < stations.Count ? stations[k + 1] : 1) - (k > 0 ? stations[k - 1] : 0)) / 2;
                    nodes.Add(chain[k]);
                    xs.Add(x);
                    // The node's share of the strip's area (proportional to its mass, so light nodes near a densely
                    // sampled nose aren't overloaded); the linear tilt then puts the center of pressure in place.
                    weights.Add(coverage);
                }
            }
        }
        // The first node behind the nose on a surface (the camber surface of a single-surface proxy).
        static int Throat(ProxySection s, bool upper)
        {
            var chain = ProxyBuilder.Chain(s, upper && s.Upper.Count > 0);
            return chain[Math.Min(1, chain.Count - 1)];
        }
        int Mid(ProxySection s)
        {
            var chain = ProxyBuilder.Chain(s, false);
            int best = 1;
            for (int k = 1; k < chain.Count - 1; k++)
            {
                if (Math.Abs(s.Stations[k - 1] - 0.6f) < Math.Abs(s.Stations[best - 1] - 0.6f)) best = k;
            }
            return chain[Math.Clamp(best, 0, chain.Count - 1)];
        }
        var data = new StripData
        {
            LeA = a.LeadingEdge, TeA = a.TrailingEdge, LeB = b.LeadingEdge, TeB = b.TrailingEdge,
            MidA = Mid(a), MidB = Mid(b),
            ThroatUpperA = Throat(a, true), ThroatLowerA = Throat(a, false), ThroatUpperB = Throat(b, true), ThroatLowerB = Throat(b, false),
            Nodes = nodes.ToArray(), X = xs.ToArray(), BaseWeight = weights.ToArray(),
            Surface = strip.Surface.ToArray(), HasInlet = strip.HasInlet,
        };
        // The rest flap angle (the profile's own camber line kink).
        var p = _model.Nodes;
        var le = (p[data.LeA].Position + p[data.LeB].Position) * 0.5f;
        var te = (p[data.TeA].Position + p[data.TeB].Position) * 0.5f;
        var mid = (p[data.MidA].Position + p[data.MidB].Position) * 0.5f;
        var span = p[data.LeB].Position - p[data.LeA].Position;
        var spanDir = span.LengthSquared() > 0 ? Vector3.Normalize(span) : Vector3.UnitX;
        var chord = te - le;
        var chordDir = Vector3.Normalize(chord - spanDir * Vector3.Dot(chord, spanDir));
        data.RestFlap = FlapAngle(le, mid, te, chordDir, Vector3.Cross(spanDir, chordDir));
        var restThroat = (p[data.ThroatUpperA].Position + p[data.ThroatLowerA].Position + p[data.ThroatUpperB].Position + p[data.ThroatLowerB].Position) * 0.25f - le;
        data.RestNoseAngle = MathF.Atan2(Vector3.Dot(restThroat, Vector3.Cross(spanDir, chordDir)), Vector3.Dot(restThroat, chordDir));
        if (a.Upper.Count > 0) data.RestThroatHeight = 0.5f * (Vector3.Distance(p[data.ThroatUpperA].Position, p[data.ThroatLowerA].Position) + Vector3.Distance(p[data.ThroatUpperB].Position, p[data.ThroatLowerB].Position));
        return data;
    }

    private static float FlapAngle(Vector3 le, Vector3 mid, Vector3 te, Vector3 chordDir, Vector3 up)
    {
        var front = mid - le;
        var rear = te - mid;
        return MathF.Atan2(Vector3.Dot(front, up), Vector3.Dot(front, chordDir)) - MathF.Atan2(Vector3.Dot(rear, up), Vector3.Dot(rear, chordDir));
    }

    // The extra lift of a deflected trailing edge, fading out as the flow separates past the stall.
    private double FlapLift(double alphaDegrees, float flap)
    {
        if (flap == 0) return 0;
        double attached = 1 / (1 + Math.Exp((alphaDegrees - _stallAlpha) / 2.5));
        return _liftSlope * _settings.FlapEffectiveness * flap * attached;
    }

    // The sampled polar of the proxy file, interpolated linearly, as a game would use it.
    private (double Lift, double Drag, double Moment) Polar(double alpha)
    {
        var table = _model.Polar;
        var a = table.AlphaDegrees;
        if (a.Count < 2) return (0, 0, 0);
        alpha = ((alpha + 180) % 360 + 360) % 360 - 180;
        double position = (alpha - a[0]) / (a[^1] - a[0]) * (a.Count - 1);
        int i = Math.Clamp((int)Math.Floor(position), 0, a.Count - 2);
        double f = Math.Clamp(position - i, 0, 1);
        return (table.Lift[i] + (table.Lift[i + 1] - table.Lift[i]) * f,
            table.Drag[i] + (table.Drag[i + 1] - table.Drag[i]) * f,
            table.Moment[i] + (table.Moment[i + 1] - table.Moment[i]) * f);
    }
}
