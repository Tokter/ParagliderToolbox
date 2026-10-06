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
/// </remarks>
public sealed class GliderSimulator
{
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);

    private readonly ProxyModel _model;
    private readonly SimulatorSettings _settings;
    private readonly int _count;
    private readonly float[] _inverseMass;
    private readonly Vector3[] _positions;
    private readonly Vector3[] _previous;
    private readonly Vector3[] _velocities;
    private readonly Vector3[] _forces;

    private readonly int[] _ca, _cb;
    private readonly float[] _rest, _effectiveRest, _compliance, _compression;
    private readonly bool[] _tensionOnly;
    private readonly float[] _dragDiameter;
    private readonly int[] _lineConstraints;
    private readonly (string Name, int[] Constraints, float[] Travel)[] _controls;
    private readonly int[] _pilotHarness = new int[2];

    private readonly StripData[] _strips;
    private readonly float[] _pressure;
    private readonly double _liftSlope;
    private readonly double _stallAlpha;

    private sealed class StripData
    {
        public int LeA, TeA, LeB, TeB, MidA, MidB;
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
        _dragDiameter = new float[constraints];
        for (int i = 0; i < constraints; i++)
        {
            var c = model.Constraints[i];
            _ca[i] = c.A;
            _cb[i] = c.B;
            _rest[i] = c.RestLength;
            _compliance[i] = c.Compliance;
            _compression[i] = c.CompressionCompliance;
            _tensionOnly[i] = c.TensionOnly;
            _dragDiameter[i] = c.DragDiameter;
        }
        _lineConstraints = Enumerable.Range(0, constraints).Where(i => model.Constraints[i].Kind is ConstraintKind.Line or ConstraintKind.Riser).ToArray();
        _controls = model.Controls.Select(c => (c.Name, c.Constraints.ToArray(), c.Travel.ToArray())).ToArray();

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
        for (int s = 0; s < _strips.Length; s++) _strips[s].Index = s;
        _pressure = new float[_strips.Length];
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

    /// <summary>Gets the power (W) the canopy aerodynamics and the cell pressure did in the last substep (diagnostics).</summary>
    public float AeroPower { get; private set; }

    /// <inheritdoc cref="AeroPower"/>
    public float PressurePower { get; private set; }

    /// <summary>Gets the angle of attack (degrees) of the center strip.</summary>
    public float CenterAngleOfAttack => _strips.Length > 0 ? _strips[_strips.Length / 2].Alpha : 0;

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
        Time = 0;
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds (one step; call with a fixed step for stable results).</summary>
    public void Step(float dt)
    {
        if (dt <= 0) return;
        ApplyControls();

        int substeps = Math.Max(1, _settings.Substeps);
        float h = dt / substeps;
        float damping = MathF.Max(0, 1 - _settings.Damping * h);
        for (int s = 0; s < substeps; s++)
        {
            // Forces every substep: the aerodynamic damping of the light canopy nodes is too stiff for a frame-long step.
            ComputeForces(h);
            for (int i = 0; i < _count; i++)
            {
                _previous[i] = _positions[i];
                if (_inverseMass[i] == 0) continue;
                _velocities[i] += _forces[i] * (_inverseMass[i] * h);
                _positions[i] += _velocities[i] * h;
            }
            SolveConstraints(h);
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

    private void ApplyControls()
    {
        Array.Copy(_rest, _effectiveRest, _rest.Length);
        foreach (var (name, constraints, travel) in _controls)
        {
            float input = name switch
            {
                "BrakeLeft" => Inputs.BrakeLeft,
                "BrakeRight" => Inputs.BrakeRight,
                "SpeedBar" => Inputs.SpeedBar,
                "CollapseLeft" => Inputs.CollapseLeft,
                "CollapseRight" => Inputs.CollapseRight,
                "Frontal" => Inputs.Frontal,
                "BigEarsLeft" or "BigEarsRight" => Inputs.BigEars,
                _ => 0,
            };
            if (input <= 0) continue;
            input = Math.Clamp(input, 0, 1);
            for (int k = 0; k < constraints.Length; k++)
            {
                int c = constraints[k];
                _effectiveRest[c] = MathF.Max(_rest[c] * 0.05f, _effectiveRest[c] - travel[k] * input);
            }
        }
        float shift = Math.Clamp(Inputs.WeightShift, -1, 1);
        if (shift != 0 && _pilotHarness[0] != _pilotHarness[1])
        {
            _effectiveRest[_pilotHarness[0]] = _rest[_pilotHarness[0]] * (1 - 0.12f * shift);
            _effectiveRest[_pilotHarness[1]] = _rest[_pilotHarness[1]] * (1 + 0.12f * shift);
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
            float alpha = (c < 0 ? _compression[i] : _compliance[i]) * invH2;
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
        for (int i = 0; i < _count; i++) _forces[i] = _inverseMass[i] > 0 ? Gravity / _inverseMass[i] : Vector3.Zero;

        _canopyLift = _canopyDrag = _lineDrag = _pilotDrag = Vector3.Zero;
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
        cl += flapLift;
        // A deflated cell is a crumpled bag, not an airfoil: it loses most of its lift and drags more.
        double inflation = Math.Clamp(_pressure[strip.Index], 0, 1);
        cl *= 0.25 + 0.75 * inflation;
        cd += 0.3 * (1 - inflation);
        cm -= 0.3 * flapLift;
        cd += 0.6 * flap * flap + cl * induced;
        strip.Alpha = alphaDegrees;

        var liftDir = up - flowDir * Vector3.Dot(up, flowDir);
        float liftLength = liftDir.Length();
        if (liftLength < 1e-6f) return;
        liftDir /= liftLength;

        float q = 0.5f * rho * speed * speed;
        float area = chordLength * spanLength;
        var force = (liftDir * (float)cl + flowDir * (float)cd) * (q * area);
        _canopyLift += liftDir * (float)(cl * q * area);
        _canopyDrag += flowDir * (float)(cd * q * area);
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
        float tau = MathF.Max(0.05f, _model.CellPressureTimeConstant);
        float relax = MathF.Min(1, dt / tau);
        for (int s = 0; s < _strips.Length; s++)
        {
            var strip = _strips[s];
            float target;
            if (strip.HasInlet)
            {
                // Ram air while the flow comes from the front and below the inlet; from above or behind it deflates.
                target = strip.Alpha > _model.InletClosingAlpha && strip.Alpha < 100 && strip.Airspeed > 2 ? 1 : -0.25f;
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
            _pressure[s] += (target - _pressure[s]) * relax;
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
