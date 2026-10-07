using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Platform;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Graphics3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Export;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Simulation;
using ParagliderToolbox.Paraglider.Texturing;
using SkiaSharp;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The live preview of a <see cref="ParagliderNode"/>: regenerates the model in the background whenever the design
/// changes, shows it in a 3D scene (high resolution parts and the physics proxy), and simulates the proxy with the high
/// resolution mesh skinned to it. The commands are the <see cref="Group"/> keybinding group, active while the preview
/// has the focus.
/// </summary>
public sealed partial class ParagliderPreview : ObservableObject, IDisposable
{
    /// <summary>The keybinding group of the preview's commands.</summary>
    public const string Group = "Paraglider preview";

    private const int PreviewTextureSize = 2048;
    private const float StepTime = 1 / 60f;

    private readonly ParagliderNode _node;
    private readonly Func<Modules.Paraglider.ParagliderActions> _actions;
    private readonly GliderScene _scene = new();
    private readonly Mesh3D _forceLines = new(PrimitiveTopology.Lines);
    private readonly MeshInstance3D _forceLinesInstance;
    private Vector3[] _forcePositions = [];
    private bool? _forceLayoutPerStrip; // the layout the force mesh was built for (null: none yet)
    private float _forceUnit; // newtons per meter of arrow at scale 1
    private CancellationTokenSource? _generation;
    private int _generatedVersion = -1;
    private bool _disposed;

    private GliderSimulator? _simulator;
    private FlightRecorder? _recorder;
    private Vector3[] _previousDisplay = [];
    private Vector3[] _currentDisplay = [];
    private Vector3[] _shown = [];
    private (double X, double Y, double Z) _displayOrigin;
    private readonly Stopwatch _clock = new();
    private readonly GamepadPilot _gamepad = new();
    private double _lastFrame;
    private float _pending;
    private readonly Dictionary<string, (float Start, float Until)> _releaseAt = [];

    [ObservableProperty] private GliderModel? _model;
    [ObservableProperty] private string _status = "Generating…";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private bool _showCanopy = true;
    [ObservableProperty] private bool _showRibs = true;
    [ObservableProperty] private bool _showRigging = true;
    [ObservableProperty] private bool _showProxy;
    [ObservableProperty] private bool _isSimulating;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _telemetry = string.Empty;
    [ObservableProperty] private float _brakeLeft;
    [ObservableProperty] private float _brakeRight;
    [ObservableProperty] private float _speedBar;
    [ObservableProperty] private float _weightShift;
    [ObservableProperty] private bool _showForces;
    [ObservableProperty] private bool _forcesPerStrip;
    [ObservableProperty] private bool _exaggerateDrag;
    [ObservableProperty] private float _forceScale = 1;
    [ObservableProperty] private string _forceLegend = string.Empty;

    /// <summary>Initializes the preview of <paramref name="node"/>.</summary>
    public ParagliderPreview(ParagliderNode node, Func<ParagliderActions> actions)
    {
        _node = node;
        _actions = actions;
        _forceLinesInstance = new MeshInstance3D(_forceLines, new Material3D { BaseColor = Color.FromRgb(255, 255, 255), Unlit = true }) { Name = "Forces", DrawOnTop = true, IsVisible = false };
        _scene.AddOverlay(_forceLinesInstance);
        _node.PropertyChanged += OnNodePropertyChanged;
        Regenerate();
    }

    /// <summary>Gets the node.</summary>
    public ParagliderNode Node => _node;

    /// <summary>Gets the scene the viewport shows.</summary>
    public Scene3D Scene => _scene.Scene;

    /// <summary>Occurs when the scene changed shape (a new model): the view frames it the first time.</summary>
    public event EventHandler? ModelApplied;

    /// <summary>Occurs when the simulation stopped and the glider is back at rest (the view frames it again).</summary>
    public event EventHandler? SimulationStopped;

    /// <summary>Occurs every simulation frame with the point the camera should follow.</summary>
    public event EventHandler<Vector3>? Simulated;

    /// <summary>Gets the simulator, while simulating.</summary>
    public GliderSimulator? Simulator => _simulator;

    #region Generation

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_node.DesignVersion != _generatedVersion) Regenerate();
    }

    /// <summary>Regenerates the model from the node's design (debounced: rapid edits regenerate once).</summary>
    public void Regenerate()
    {
        _generation?.Cancel();
        var cancellation = _generation = new CancellationTokenSource();
        int version = _node.DesignVersion;
        var design = _node.Snapshot();
        IsGenerating = true;
        Task.Run(async () =>
        {
            await Task.Delay(120, cancellation.Token);
            var model = GliderGenerator.Generate(design, new GenerateOptions(Math.Min(PreviewTextureSize, MeshSettings.FromDesign(design).TextureSize)), cancellation.Token);
            Dispatcher.Post(() =>
            {
                if (cancellation.IsCancellationRequested || _disposed) return;
                _generatedVersion = version;
                Apply(model);
                IsGenerating = false;
            });
        }, cancellation.Token).ContinueWith(task =>
        {
            if (task.Exception?.GetBaseException() is { } error && !cancellation.IsCancellationRequested)
            {
                Dispatcher.Post(() =>
                {
                    IsGenerating = false;
                    Status = error is FormatException ? $"Can't generate: {error.Message}" : $"Generation failed: {error.Message}";
                });
            }
        }, TaskScheduler.Default);
    }

    private void Apply(GliderModel model)
    {
        bool first = Model is null;
        StopSimulation();
        Model = model;
        _scene.Show(model);
        UpdateVisibility();

        var proxy = model.Proxy.Model;
        Status = $"{model.TriangleCount / 1000.0:0.#}k triangles · {model.Shape.CellCount} cells · proxy: {proxy.Nodes.Count} nodes, " +
                 $"{proxy.Constraints.Count} constraints ({model.Design.ProxyComplexity}) · generated in {model.Elapsed.TotalMilliseconds:0} ms";
        if (first) ModelApplied?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowCanopyChanged(bool value) => UpdateVisibility();
    partial void OnShowRibsChanged(bool value) => UpdateVisibility();
    partial void OnShowRiggingChanged(bool value) => UpdateVisibility();
    partial void OnShowProxyChanged(bool value) => UpdateVisibility();

    private void UpdateVisibility()
    {
        _scene.ShowCanopy = ShowCanopy;
        _scene.ShowRibs = ShowRibs;
        _scene.ShowRigging = ShowRigging;
        _scene.ShowProxy = ShowProxy;
        _forceLinesInstance.IsVisible = ShowForces && _simulator != null && _forceLayoutPerStrip != null;
    }

    #endregion

    #region Commands

    [RelayCommand]
    [property: Command("ToggleCanopy", Group, Label = "Show canopy", Icon = MaterialIcons.Paragliding, Description = "Show or hide the canopy and its ribs", DefaultKeybinding = "1")]
    private void ToggleCanopy() => ShowCanopy = !ShowCanopy;

    [RelayCommand]
    [property: Command("ToggleRigging", Group, Label = "Show rigging", Icon = MaterialIcons.Timeline, Description = "Show or hide the lines, risers and toggles", DefaultKeybinding = "2")]
    private void ToggleRigging() => ShowRigging = !ShowRigging;

    [RelayCommand]
    [property: Command("ToggleProxy", Group, Label = "Show physics proxy", Icon = MaterialIcons.Hub, Description = "Show or hide the simulation proxy's nodes and constraints", DefaultKeybinding = "3")]
    private void ToggleProxy() => ShowProxy = !ShowProxy;

    [RelayCommand]
    [property: Command("ToggleRibs", Group, Label = "Show internal ribs", Icon = MaterialIcons.ViewColumn, Description = "Show or hide the internal ribs", DefaultKeybinding = "4")]
    private void ToggleRibs() => ShowRibs = !ShowRibs;

    [RelayCommand]
    [property: Command("ToggleForces", Group, Label = "Show aerodynamic forces", Icon = MaterialIcons.ArrowUpward,
        Description = "Show or hide the lift and drag where they act on the proxy while simulating: arrows as long as the forces are strong", DefaultKeybinding = "5")]
    private void ToggleForces() => ShowForces = !ShowForces;

    [RelayCommand]
    [property: Command("ToggleForcesPerStrip", Group, Label = "Forces per strip", Icon = MaterialIcons.ViewColumn,
        Description = "Show the canopy's lift and drag per node (where the simulation applies them) or summed per strip at its center of pressure", DefaultKeybinding = "6")]
    private void ToggleForcesPerStrip() => ForcesPerStrip = !ForcesPerStrip;

    [RelayCommand]
    [property: Command("ExaggerateDrag", Group, Label = "Exaggerate drag", Icon = MaterialIcons.Air,
        Description = "Draw the drag arrows five times longer than the lift arrows (drag is about a ninth of the lift)", DefaultKeybinding = "D")]
    private void ToggleExaggerateDrag() => ExaggerateDrag = !ExaggerateDrag;

    [RelayCommand]
    [property: Command("LongerForceArrows", Group, Label = "Longer force arrows", Icon = MaterialIcons.ZoomIn, Description = "Draw the force arrows longer", DefaultKeybinding = "Equal")]
    private void LongerForceArrows() => ForceScale = Math.Min(50, ForceScale * 1.5f);

    [RelayCommand]
    [property: Command("ShorterForceArrows", Group, Label = "Shorter force arrows", Icon = MaterialIcons.ZoomOut, Description = "Draw the force arrows shorter", DefaultKeybinding = "Minus")]
    private void ShorterForceArrows() => ForceScale = Math.Max(0.02f, ForceScale / 1.5f);

    [RelayCommand]
    [property: Command("Simulate", Group, Label = "Simulate", Icon = MaterialIcons.PlayArrow, Description = "Start or pause the flight simulation of the proxy", DefaultKeybinding = "P")]
    private void ToggleSimulation()
    {
        if (IsSimulating) PauseSimulation();
        else StartSimulation();
    }

    [RelayCommand]
    [property: Command("ResetSimulation", Group, Label = "Reset simulation", Icon = MaterialIcons.RestartAlt, Description = "Put the glider back in its trim glide", DefaultKeybinding = "R")]
    private void ResetSimulation()
    {
        bool wasRunning = IsSimulating;
        StopSimulation();
        if (wasRunning) StartSimulation();
    }

    [RelayCommand]
    [property: Command("CollapseLeft", Group, Label = "Collapse left", Icon = MaterialIcons.SwipeLeft, Description = "Pull the outer left A lines down for a moment: an asymmetric collapse", DefaultKeybinding = "Q")]
    private void CollapseLeft() => Pulse("CollapseLeft", 0.8f);

    [RelayCommand]
    [property: Command("CollapseRight", Group, Label = "Collapse right", Icon = MaterialIcons.SwipeRight, Description = "Pull the outer right A lines down for a moment", DefaultKeybinding = "E")]
    private void CollapseRight() => Pulse("CollapseRight", 0.8f);

    [RelayCommand]
    [property: Command("FrontalCollapse", Group, Label = "Frontal collapse", Icon = MaterialIcons.SwipeDown, Description = "Pull all A lines down for a moment", DefaultKeybinding = "F")]
    private void FrontalCollapse() => Pulse("Frontal", 0.6f);

    [RelayCommand]
    [property: Command("BigEars", Group, Label = "Big ears", Icon = MaterialIcons.UnfoldLess, Description = "Pull the outermost A lines in (hold for 4 seconds)", DefaultKeybinding = "B")]
    private void BigEars() => Pulse("BigEars", 4f);

    [RelayCommand]
    [property: Command("Gust", Group, Label = "Gust", Icon = MaterialIcons.Air, Description = "A 4 m/s thermal gust from below for two seconds", DefaultKeybinding = "G")]
    private void Gust() => Pulse("Gust", 2f);

    [RelayCommand]
    [property: Command("Record", Group, Label = "Record", Icon = MaterialIcons.FiberManualRecord,
        Description = "Record the flight; stopping adds it to the paraglider, to replay, inspect and export it", DefaultKeybinding = "Ctrl+R")]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            SaveRecording();
            return;
        }
        if (!IsSimulating) StartSimulation();
        if (_recorder is null) return;
        _recorder.Start();
        IsRecording = true;
    }

    // Adds what was recorded to the paraglider (a moment is too short to keep).
    private void SaveRecording()
    {
        IsRecording = false;
        if (_recorder is not { IsRecording: true } recorder) return;
        var recording = recorder.Stop();
        if (recording.FrameCount > 1) _actions().AddRecording(_node, recording);
    }

    [ObservableProperty] private bool _isRecordingPolar;
    [ObservableProperty] private double _polarProgress;
    [ObservableProperty] private string _polarPhase = string.Empty;
    private CancellationTokenSource? _polarCancellation;

    /// <summary>Occurs when a polar recording starts (the view clears its live chart).</summary>
    public event EventHandler? PolarStarted;

    /// <summary>Occurs on the UI thread for every continuous sample of the polar being recorded.</summary>
    public event EventHandler<ParagliderToolbox.Paraglider.Polar.PolarSample>? PolarSampleRecorded;

    /// <summary>Occurs on the UI thread for every steady point of the polar being recorded.</summary>
    public event EventHandler<ParagliderToolbox.Paraglider.Polar.PolarPoint>? PolarPointRecorded;

    [RelayCommand(AllowConcurrentExecutions = true)]
    [property: Command("RecordPolar", Group, Label = "Record polar", Icon = MaterialIcons.ShowChart,
        Description = "Choose the settings, then fly through the whole speed range (speed bar, then brakes until the stall), record the polar and store it with the paraglider; again to cancel",
        DefaultKeybinding = "Ctrl+P")]
    private async Task RecordPolarAsync()
    {
        if (IsRecordingPolar)
        {
            _polarCancellation?.Cancel();
            return;
        }
        if (Model is not { } model) return;
        PauseSimulation();
        var settings = await _actions().AskPolarSettingsAsync(_node);
        // The design may have changed while the dialog was open, and only one recording runs at a time.
        if (settings is null || IsRecordingPolar || _disposed || Model is not { } current) return;
        model = current;
        StopSimulation();
        var cancellation = _polarCancellation = new CancellationTokenSource();
        IsRecordingPolar = true;
        PolarProgress = 0;
        PolarPhase = "Settling in trim";
        PolarStarted?.Invoke(this, EventArgs.Empty);
        var progress = new UiProgress(p =>
        {
            if (cancellation.IsCancellationRequested) return;
            PolarProgress = p.Fraction;
            if (p.Phase.Length > 0) PolarPhase = p.Phase;
            if (p.Point is { } point) PolarPointRecorded?.Invoke(this, point);
            else PolarSampleRecorded?.Invoke(this, p.Sample);
        });
        try
        {
            var recording = await Task.Run(() =>
                ParagliderToolbox.Paraglider.Polar.PolarRecorder.Record(model.Proxy.Model, model.Design, settings, progress, cancellation.Token));
            // A canceled recording is kept when it measured something besides trim.
            if (!_disposed && recording.Points.Count > (recording.IsComplete ? 0 : 1)) _actions().AddPolar(_node, recording);
        }
        finally
        {
            IsRecordingPolar = false;
            PolarPhase = string.Empty;
        }
    }

    // Reports on the UI thread (the recorder runs on a worker thread).
    private sealed class UiProgress(Action<ParagliderToolbox.Paraglider.Polar.PolarProgress> report) : IProgress<ParagliderToolbox.Paraglider.Polar.PolarProgress>
    {
        public void Report(ParagliderToolbox.Paraglider.Polar.PolarProgress value) => Dispatcher.Post(() => report(value));
    }

    private void Pulse(string input, float seconds)
    {
        if (!IsSimulating) StartSimulation();
        if (_simulator is null) return;
        _releaseAt[input] = (_simulator.Time, _simulator.Time + seconds);
    }

    #endregion

    #region Simulation

    /// <summary>Starts (or resumes) the simulation.</summary>
    public void StartSimulation()
    {
        if (Model is not { } model) return;
        if (_simulator is null)
        {
            // Recording the forces costs next to nothing, and they can be shown while paused.
            _simulator = new GliderSimulator(model.Proxy.Model) { RecordForces = true };
            // Logs the inputs from the first step, so a recording started later can be flown again exactly.
            _recorder = new FlightRecorder(_simulator, model.Design, StepTime);
            _forceLayoutPerStrip = null;
            int nodes = model.Proxy.Model.Nodes.Count;
            _displayOrigin = _simulator.Origin;
            _previousDisplay = new Vector3[nodes];
            _currentDisplay = new Vector3[nodes];
            _shown = new Vector3[nodes];
            CaptureDisplay(_simulator, _currentDisplay);
            CaptureDisplay(_simulator, _previousDisplay);
        }
        IsSimulating = true;
        _clock.Restart();
        _lastFrame = 0;
        _pending = 0;
        ToggleSimulationCommandLabel = "Pause";
    }

    /// <summary>Pauses the simulation (the glider stays where it is).</summary>
    public void PauseSimulation()
    {
        IsSimulating = false;
        ToggleSimulationCommandLabel = "Simulate";
    }

    /// <summary>Stops the simulation and shows the model at rest.</summary>
    public void StopSimulation()
    {
        IsSimulating = false;
        // A recording ends with its flight (a reset, or a new design), and is kept.
        if (IsRecording) SaveRecording();
        ToggleSimulationCommandLabel = "Simulate";
        if (_simulator is null) return;
        _simulator = null;
        _recorder = null;
        _releaseAt.Clear();
        Telemetry = string.Empty;
        ForceLegend = string.Empty;
        UpdateVisibility();
        _scene.ShowRest();
        SimulationStopped?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty] private string _toggleSimulationCommandLabel = "Simulate";

    /// <summary>Advances the simulation to the current time and updates the scene; the view calls it every rendered frame.</summary>
    /// <returns>Whether another frame is needed.</returns>
    public bool Advance()
    {
        if (!IsSimulating || _simulator is not { } sim || Model is null) return false;
        double now = _clock.Elapsed.TotalSeconds;
        _pending += (float)Math.Min(0.1, now - _lastFrame);
        _lastFrame = now;

        // The game controller: its moved controls also move the sliders; A may have paused the simulation.
        PollGamepad();
        if (!IsSimulating) return false;

        var inputs = sim.Inputs;
        inputs.BrakeLeft = BrakeLeft;
        inputs.BrakeRight = BrakeRight;
        inputs.SpeedBar = SpeedBar;
        inputs.WeightShift = WeightShift;
        int steps = 0;
        while (_pending >= StepTime && steps < 4)
        {
            float t = sim.Time;
            inputs.CollapseLeft = Active("CollapseLeft", t);
            inputs.CollapseRight = Active("CollapseRight", t);
            inputs.Frontal = Active("Frontal", t);
            inputs.BigEars = Active("BigEars", t);
            inputs.Wind = Active("Gust", t) > 0 ? new Vector3(0, 4, 0) : Vector3.Zero;
            (_previousDisplay, _currentDisplay) = (_currentDisplay, _previousDisplay);
            _recorder?.BeforeStep();
            sim.Step(StepTime);
            _recorder?.AfterStep();
            CaptureDisplay(sim, _currentDisplay);
            _pending -= StepTime;
            steps++;
        }
        // Too slow to keep up: drop the time rather than fall further behind.
        if (_pending > StepTime) _pending = StepTime;

        // Frames don't line up with the fixed steps: show the glider between the last two steps, so it moves smoothly.
        float alpha = Math.Clamp(_pending / StepTime, 0, 1);
        for (int i = 0; i < _shown.Length; i++) _shown[i] = Vector3.Lerp(_previousDisplay[i], _currentDisplay[i], alpha);

        _scene.Pose(_shown);
        if (ShowForces) UpdateForces();

        var v = sim.PilotVelocity;
        float horizontal = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        var pressure = sim.CellPressure;
        int deflated = 0;
        foreach (float p in pressure) if (p < 0.4f) deflated++;
        Telemetry = $"{sim.Time,5:0.0} s   airspeed {sim.Airspeed * 3.6f:0} km/h   vario {v.Y:+0.0;-0.0} m/s   glide {(v.Y < -0.05f ? horizontal / -v.Y : 99):0.0}   " +
                    $"G {sim.LoadFactor:0.0}   AoA {sim.CenterAngleOfAttack:0}°   deflated cells {deflated}/{pressure.Length}{(IsRecording && _recorder is { } recorder ? $"   ● REC {recorder.RecordedSeconds:0.0} s" : "")}" +
                    (_gamepad.GamepadName is { } padName ? $"   gamepad: {padName}" : "");

        // The camera follows the point between the pilot and the canopy.
        Simulated?.Invoke(this, _scene.FollowPoint(_shown));
        return true;
    }

    /// <summary>
    /// Reads the game controller (see <see cref="GamepadPilot"/>): triggers and left stick set the brakes, speed bar and
    /// weight shift; A starts or pauses the simulation; the left and right bumpers collapse the left and right side.
    /// The simulation calls it every frame; the view calls it regularly while the simulation doesn't run.
    /// </summary>
    public void PollGamepad()
    {
        var pad = _gamepad.Read(Gamepads.First);
        if (pad.BrakeLeft is { } brakeLeft) BrakeLeft = brakeLeft;
        if (pad.BrakeRight is { } brakeRight) BrakeRight = brakeRight;
        if (pad.SpeedBar is { } speedBar) SpeedBar = speedBar;
        if (pad.WeightShift is { } weightShift) WeightShift = weightShift;
        if ((pad.Pressed & GamepadButtons.A) != 0) ToggleSimulation();
        if ((pad.Pressed & GamepadButtons.LeftBumper) != 0) CollapseLeft();
        if ((pad.Pressed & GamepadButtons.RightBumper) != 0) CollapseRight();
    }

    // The node positions where they are shown: the simulator keeps the glider near its origin and moves the origin
    // along, so add how far the origin moved since the start. The glider flies on continuously through the scene.
    private void CaptureDisplay(GliderSimulator sim, Vector3[] target)
    {
        var offset = new Vector3(
            (float)(sim.Origin.X - _displayOrigin.X), (float)(sim.Origin.Y - _displayOrigin.Y), (float)(sim.Origin.Z - _displayOrigin.Z));
        var positions = sim.Positions;
        for (int i = 0; i < target.Length; i++) target[i] = positions[i] + offset;
    }

    // The pilot pulls lines in over a third of a second (a step would yank the canopy around), and lets go at once.
    private float Active(string input, float time) =>
        _releaseAt.TryGetValue(input, out var pulse) && time < pulse.Until ? Math.Clamp((time - pulse.Start) / 0.3f + 0.05f, 0, 1) : 0;

    #endregion

    #region Forces

    /// <summary>The color of the lift arrows (and the legend's).</summary>
    public static readonly Color LiftColor = Color.FromRgb(0x5C, 0xE0, 0x6A);

    /// <summary>The color of the canopy drag arrows.</summary>
    public static readonly Color CanopyDragColor = Color.FromRgb(0xFF, 0x4D, 0x4D);

    /// <summary>The color of the line and riser drag arrows.</summary>
    public static readonly Color LineDragColor = Color.FromRgb(0xFF, 0xA7, 0x26);

    /// <summary>The color of the pilot drag arrow.</summary>
    public static readonly Color PilotDragColor = Color.FromRgb(0xD0, 0x6B, 0xFF);

    /// <summary>The color of the pilot load arrow (weight and inertia, the G load).</summary>
    public static readonly Color PilotLoadColor = Color.FromRgb(0x4F, 0xC3, 0xF7);

    /// <summary>How long the pilot load arrow is per G (m): the load is much larger than the aerodynamic forces.</summary>
    public const float PilotLoadMetersPerG = 1.5f;

    partial void OnShowForcesChanged(bool value)
    {
        UpdateForces();
        UpdateVisibility();
        if (!value) ForceLegend = string.Empty;
    }

    partial void OnForcesPerStripChanged(bool value) => UpdateForces();
    partial void OnExaggerateDragChanged(bool value) => UpdateForces();
    partial void OnForceScaleChanged(float value) => UpdateForces();

    // Arrows from where the simulator applies the aerodynamic forces, as long as they are strong (also while paused):
    // per node the canopy's lift and drag shares, or per strip their sums at its center of pressure; and the line,
    // riser and pilot drag on the nodes; and the pilot's load (weight and inertia) on its own scale.
    private void UpdateForces()
    {
        if (!ShowForces || _simulator is not { } sim || Model is not { } model || sim.NodeLift.Length == 0) return;
        var proxy = model.Proxy.Model;
        bool perStrip = ForcesPerStrip;
        if (_forceLayoutPerStrip != perStrip) BuildForceMesh(proxy, perStrip);

        float scale = ForceScale / _forceUnit; // meters per newton
        float dragScale = scale * (ExaggerateDrag ? 5 : 1);
        int k = 0;
        void Arrow(Vector3 from, Vector3 force, float metersPerNewton)
        {
            _forcePositions[k++] = from;
            _forcePositions[k++] = from + force * metersPerNewton;
        }

        var lift = Vector3.Zero;
        var canopyDrag = Vector3.Zero;
        if (perStrip)
        {
            for (int s = 0; s < proxy.Strips.Count; s++)
            {
                var a = proxy.Sections[proxy.Strips[s].SectionA];
                var b = proxy.Sections[proxy.Strips[s].SectionB];
                var leadingEdge = (_shown[a.LeadingEdge] + _shown[b.LeadingEdge]) * 0.5f;
                var trailingEdge = (_shown[a.TrailingEdge] + _shown[b.TrailingEdge]) * 0.5f;
                var center = Vector3.Lerp(leadingEdge, trailingEdge, sim.StripCenterOfPressure(s));
                Arrow(center, sim.StripLift[s], scale);
                Arrow(center, sim.StripDrag[s], dragScale);
            }
        }
        else
        {
            for (int i = 0; i < proxy.Nodes.Count; i++)
            {
                Arrow(_shown[i], sim.NodeLift[i], scale);
                Arrow(_shown[i], sim.NodeDrag[i], dragScale);
            }
        }
        foreach (var force in sim.StripLift) lift += force;
        foreach (var force in sim.StripDrag) canopyDrag += force;
        var lineDrag = Vector3.Zero;
        for (int i = 0; i < proxy.Nodes.Count; i++)
        {
            Arrow(_shown[i], sim.NodeParasiticDrag[i], dragScale);
            if (i != proxy.Pilot) lineDrag += sim.NodeParasiticDrag[i];
        }
        var pilotDrag = sim.NodeParasiticDrag[proxy.Pilot];
        // The load the pilot hangs in the harness with: down, and out of a turn.
        float weight = MathF.Max(1e-3f, proxy.Nodes[proxy.Pilot].Mass * 9.81f);
        Arrow(_shown[proxy.Pilot], sim.PilotLoad, PilotLoadMetersPerG / weight);
        _forceLines.UpdatePositions(_forcePositions);

        float drag = (canopyDrag + lineDrag + pilotDrag).Length();
        ForceLegend = $"{(perStrip ? "per strip, at its center of pressure" : "per node")}   1 m = {_forceUnit / ForceScale:0.##} N" +
                      (ExaggerateDrag ? $" (drag {_forceUnit / ForceScale / 5:0.##} N)" : "") +
                      $"   lift {lift.Length():0} N, drag: canopy {canopyDrag.Length():0} N, lines {lineDrag.Length():0} N, pilot {pilotDrag.Length():0} N   L/D {(drag > 0 ? lift.Length() / drag : 0):0.0}   pilot load {sim.LoadFactor:0.0} G (1 G = {PilotLoadMetersPerG:0.#} m)";
    }

    // The arrows' vertices (two each, colored by the force) for a layout; and the scale: the mean lift arrow is 0.4 m
    // per node or 1.2 m per strip (the glider's weight shared out).
    private void BuildForceMesh(ProxyModel proxy, bool perStrip)
    {
        int nodes = proxy.Nodes.Count, strips = proxy.Strips.Count;
        int arrows = (perStrip ? strips : nodes) * 2 + nodes + 1;
        _forcePositions = new Vector3[arrows * 2];
        var colors = new Vector4[arrows * 2];
        int k = 0;
        void Paint(Color color)
        {
            // Vertex colors are linear.
            var linear = new Vector4(MathF.Pow(color.Rf, 2.2f), MathF.Pow(color.Gf, 2.2f), MathF.Pow(color.Bf, 2.2f), 1);
            colors[k++] = linear;
            colors[k++] = linear;
        }
        for (int i = 0; i < (perStrip ? strips : nodes); i++)
        {
            Paint(LiftColor);
            Paint(CanopyDragColor);
        }
        for (int i = 0; i < nodes; i++) Paint(i == proxy.Pilot ? PilotDragColor : LineDragColor);
        Paint(PilotLoadColor);
        var indices = new uint[arrows * 2];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)i;
        _forceLines.SetGeometry(new Vector3[arrows * 2], indices, colors: colors);

        float weight = proxy.Nodes.Sum(n => MathF.Max(0, n.Mass)) * 9.81f;
        int canopyNodes = proxy.Nodes.Count(n => n.Kind is ProxyNodeKind.Upper or ProxyNodeKind.Lower or ProxyNodeKind.Camber);
        _forceUnit = perStrip ? weight / Math.Max(1, strips) / 1.2f : weight / Math.Max(1, canopyNodes) / 0.4f;
        _forceLayoutPerStrip = perStrip;
        UpdateVisibility();
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _polarCancellation?.Cancel();
        _generation?.Cancel();
        _node.PropertyChanged -= OnNodePropertyChanged;
    }
}
