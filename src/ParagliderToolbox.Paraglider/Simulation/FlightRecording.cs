using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Serialization;
using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Paraglider.Simulation;

/// <summary>The pilot's inputs for one simulation step (see <see cref="SimulationInputs"/>).</summary>
public readonly record struct FlightInputs(
    float BrakeLeft, float BrakeRight, float SpeedBar, float WeightShift,
    float CollapseLeft, float CollapseRight, float Frontal, float BigEars,
    float WindX, float WindY, float WindZ)
{
    /// <summary>Gets the inputs <paramref name="inputs"/> holds now.</summary>
    public static FlightInputs From(SimulationInputs inputs) => new(
        inputs.BrakeLeft, inputs.BrakeRight, inputs.SpeedBar, inputs.WeightShift,
        inputs.CollapseLeft, inputs.CollapseRight, inputs.Frontal, inputs.BigEars,
        inputs.Wind.X, inputs.Wind.Y, inputs.Wind.Z);

    /// <summary>Sets <paramref name="inputs"/> to these.</summary>
    public void ApplyTo(SimulationInputs inputs)
    {
        inputs.BrakeLeft = BrakeLeft;
        inputs.BrakeRight = BrakeRight;
        inputs.SpeedBar = SpeedBar;
        inputs.WeightShift = WeightShift;
        inputs.CollapseLeft = CollapseLeft;
        inputs.CollapseRight = CollapseRight;
        inputs.Frontal = Frontal;
        inputs.BigEars = BigEars;
        inputs.Wind = new Vector3(WindX, WindY, WindZ);
    }
}

/// <summary>The inputs from step <paramref name="Step"/> (counted from the start of the flight) on.</summary>
public readonly record struct FlightInputChange(int Step, FlightInputs Inputs);

/// <summary>What a recorded frame shows besides the node positions (SI units, degrees).</summary>
/// <param name="Time">The simulated time since the start of the flight (s).</param>
/// <param name="Airspeed">The pilot's airspeed.</param>
/// <param name="VerticalSpeed">The pilot's vertical speed (negative when sinking).</param>
/// <param name="LoadFactor">The pilot's G load.</param>
/// <param name="AngleOfAttack">The center strip's angle of attack.</param>
/// <param name="DeflatedCells">How many cells are below 40% pressure.</param>
/// <param name="Inputs">The pilot's inputs.</param>
public readonly record struct FlightTelemetry(
    float Time, float Airspeed, float VerticalSpeed, float LoadFactor, float AngleOfAttack, int DeflatedCells, FlightInputs Inputs);

/// <summary>The decoded frames of a <see cref="FlightRecording"/>.</summary>
/// <param name="Positions">Per frame, every proxy node's position relative to where the recording started (m, glTF axes).</param>
/// <param name="Telemetry">Per frame, the flight data.</param>
public sealed record FlightFrames(Vector3[][] Positions, FlightTelemetry[] Telemetry);

/// <summary>
/// A recorded flight of a paraglider: the design it was flown with, the pilot's inputs for every step since the
/// simulation started (so <see cref="GliderSimulator"/> flies it again exactly), and the proxy's node positions and the
/// telemetry of every frame while recording (so it replays as it was seen, whatever the simulator does by now).
/// </summary>
/// <remarks>
/// The frames are stored compactly: per frame the telemetry, the pilot's position, and every node's offset from the
/// pilot in millimeters (16 bits), each as the difference to the frame before, deflated and base64 encoded (about
/// 2 MB a minute for a Medium proxy).
/// </remarks>
public sealed record FlightRecording
{
    /// <summary>Gets when it was recorded.</summary>
    public DateTime Recorded { get; init; } = DateTime.Now;

    /// <summary>Gets the design it was flown with.</summary>
    public GliderDesign? Design { get; init; }

    /// <summary>Gets the number of proxy nodes (every frame has a position for each).</summary>
    public int ProxyNodes { get; init; }

    /// <summary>Gets the simulation step (s).</summary>
    public float StepTime { get; init; } = 1 / 60f;

    /// <summary>Gets the steps from one frame to the next.</summary>
    public int StepsPerFrame { get; init; } = 2;

    /// <summary>Gets the step (counted from the start of the flight) the first frame was taken after.</summary>
    public int FirstStep { get; init; }

    /// <summary>Gets the number of frames.</summary>
    public int FrameCount { get; init; }

    /// <summary>Gets the pilot's inputs from the start of the flight to the last frame, as changes.</summary>
    public List<FlightInputChange> Inputs { get; init; } = [];

    /// <summary>Gets the encoded frames (see the remarks).</summary>
    public string Frames { get; init; } = string.Empty;

    /// <summary>Gets the time from one frame to the next (s).</summary>
    [JsonIgnore]
    public float FrameTime => StepTime * StepsPerFrame;

    /// <summary>Gets the recorded time (s).</summary>
    [JsonIgnore]
    public float Duration => Math.Max(0, FrameCount - 1) * FrameTime;

    /// <summary>Gets the inputs of step <paramref name="step"/>.</summary>
    public FlightInputs InputsAt(int step)
    {
        var inputs = default(FlightInputs);
        foreach (var change in Inputs)
        {
            if (change.Step > step) break;
            inputs = change.Inputs;
        }
        return inputs;
    }

    /// <summary>Decodes the frames.</summary>
    /// <exception cref="InvalidDataException">The frame data is damaged.</exception>
    public FlightFrames Decode()
    {
        var positions = new Vector3[FrameCount][];
        var telemetry = new FlightTelemetry[FrameCount];
        if (FrameCount == 0) return new FlightFrames(positions, telemetry);
        using var compressed = new MemoryStream(Convert.FromBase64String(Frames));
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var reader = new BinaryReader(deflate);
        var offsets = new short[ProxyNodes * 3];
        for (int f = 0; f < FrameCount; f++)
        {
            telemetry[f] = ReadTelemetry(reader);
            var pilot = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var frame = positions[f] = new Vector3[ProxyNodes];
            for (int i = 0; i < offsets.Length; i++) offsets[i] += reader.ReadInt16();
            for (int n = 0; n < ProxyNodes; n++)
                frame[n] = pilot + new Vector3(offsets[n * 3], offsets[n * 3 + 1], offsets[n * 3 + 2]) * 0.001f;
        }
        return new FlightFrames(positions, telemetry);
    }

    /// <summary>Encodes frames into <see cref="Frames"/>'s format (node positions relative to the pilot, 1 mm resolution).</summary>
    internal static string Encode(IReadOnlyList<(Vector3 Pilot, Vector3[] Positions, FlightTelemetry Telemetry)> frames)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate))
        {
            short[] previous = [];
            foreach (var (pilot, nodes, data) in frames)
            {
                if (previous.Length != nodes.Length * 3) previous = new short[nodes.Length * 3];
                WriteTelemetry(writer, data);
                writer.Write(pilot.X);
                writer.Write(pilot.Y);
                writer.Write(pilot.Z);
                for (int n = 0; n < nodes.Length; n++)
                {
                    var offset = (nodes[n] - pilot) * 1000;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        short value = (short)Math.Clamp(MathF.Round(axis == 0 ? offset.X : axis == 1 ? offset.Y : offset.Z), short.MinValue, short.MaxValue);
                        writer.Write((short)(value - previous[n * 3 + axis]));
                        previous[n * 3 + axis] = value;
                    }
                }
            }
        }
        return Convert.ToBase64String(compressed.ToArray());
    }

    private static void WriteTelemetry(BinaryWriter writer, FlightTelemetry t)
    {
        writer.Write(t.Time);
        writer.Write(t.Airspeed);
        writer.Write(t.VerticalSpeed);
        writer.Write(t.LoadFactor);
        writer.Write(t.AngleOfAttack);
        writer.Write(t.DeflatedCells);
        var i = t.Inputs;
        foreach (float value in new[] { i.BrakeLeft, i.BrakeRight, i.SpeedBar, i.WeightShift, i.CollapseLeft, i.CollapseRight, i.Frontal, i.BigEars, i.WindX, i.WindY, i.WindZ })
            writer.Write(value);
    }

    private static FlightTelemetry ReadTelemetry(BinaryReader reader)
    {
        float time = reader.ReadSingle(), airspeed = reader.ReadSingle(), vertical = reader.ReadSingle(), load = reader.ReadSingle(), aoa = reader.ReadSingle();
        int deflated = reader.ReadInt32();
        var v = new float[11];
        for (int k = 0; k < v.Length; k++) v[k] = reader.ReadSingle();
        return new FlightTelemetry(time, airspeed, vertical, load, aoa, deflated, new FlightInputs(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10]));
    }
}

/// <summary>
/// Logs a simulated flight for a <see cref="FlightRecording"/>: the inputs of every step since the simulator started,
/// and frames while recording. Call <see cref="BeforeStep"/> right before and <see cref="AfterStep"/> right after every
/// <see cref="GliderSimulator.Step"/>.
/// </summary>
public sealed class FlightRecorder
{
    private readonly GliderSimulator _simulator;
    private readonly GliderDesign _design;
    private readonly float _stepTime;
    private readonly List<FlightInputChange> _inputs = [];
    private readonly List<(Vector3 Pilot, Vector3[] Positions, FlightTelemetry Telemetry)> _frames = [];
    private int _step;
    private int _firstStep;
    private (double X, double Y, double Z) _origin;

    /// <summary>Initializes a recorder for a simulator that is about to fly its first step.</summary>
    /// <param name="simulator">The simulator, just created or reset.</param>
    /// <param name="design">The design its proxy was built from.</param>
    /// <param name="stepTime">The fixed step it is flown with (s).</param>
    public FlightRecorder(GliderSimulator simulator, GliderDesign design, float stepTime = 1 / 60f)
    {
        _simulator = simulator;
        _design = design;
        _stepTime = stepTime;
    }

    /// <summary>Gets the steps from one frame to the next (2: 30 frames a second at 60 steps).</summary>
    public int StepsPerFrame { get; init; } = 2;

    /// <summary>Gets whether frames are being recorded.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>Gets the time recorded so far (s).</summary>
    public float RecordedSeconds => Math.Max(0, _frames.Count - 1) * _stepTime * StepsPerFrame;

    /// <summary>Logs the simulator's inputs for the step it is about to take.</summary>
    public void BeforeStep()
    {
        var inputs = FlightInputs.From(_simulator.Inputs);
        if (_inputs.Count == 0 || _inputs[^1].Inputs != inputs) _inputs.Add(new FlightInputChange(_step, inputs));
    }

    /// <summary>Counts the step and, while recording, takes a frame every <see cref="StepsPerFrame"/> steps.</summary>
    public void AfterStep()
    {
        if (IsRecording && (_step - _firstStep) % StepsPerFrame == 0) Capture();
        _step++;
    }

    /// <summary>Starts recording frames (from the next step on).</summary>
    public void Start()
    {
        _frames.Clear();
        _firstStep = _step;
        _origin = _simulator.Origin;
        IsRecording = true;
    }

    /// <summary>Stops recording and returns the recording (its inputs from the start of the flight).</summary>
    public FlightRecording Stop()
    {
        IsRecording = false;
        int lastStep = _firstStep + Math.Max(0, _frames.Count - 1) * StepsPerFrame;
        return new FlightRecording
        {
            Design = _design.Clone(),
            ProxyNodes = _simulator.Model.Nodes.Count,
            StepTime = _stepTime,
            StepsPerFrame = StepsPerFrame,
            FirstStep = _firstStep,
            FrameCount = _frames.Count,
            Inputs = _inputs.Where(c => c.Step <= lastStep).ToList(),
            Frames = FlightRecording.Encode(_frames),
        };
    }

    // The node positions relative to where the recording started (the simulator moves its origin along).
    private void Capture()
    {
        var shift = new Vector3((float)(_simulator.Origin.X - _origin.X), (float)(_simulator.Origin.Y - _origin.Y), (float)(_simulator.Origin.Z - _origin.Z));
        var positions = _simulator.Positions.ToArray();
        for (int i = 0; i < positions.Length; i++) positions[i] += shift;
        int deflated = 0;
        foreach (float p in _simulator.CellPressure) if (p < 0.4f) deflated++;
        var telemetry = new FlightTelemetry(_simulator.Time, _simulator.Airspeed, _simulator.VerticalSpeed, _simulator.LoadFactor,
            _simulator.CenterAngleOfAttack, deflated, FlightInputs.From(_simulator.Inputs));
        _frames.Add((positions[_simulator.Model.Pilot], positions, telemetry));
    }
}
