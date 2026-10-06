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
    private readonly Dictionary<GliderMaterial, Material3D> _materials = [];
    private readonly List<(MeshPart Part, Mesh3D Mesh, MeshInstance3D Instance, SkinWeights[] Weights)> _parts = [];
    private readonly Mesh3D _proxyLines = new(PrimitiveTopology.Lines);
    private readonly Mesh3D _proxyPoints = new(PrimitiveTopology.Points);
    private readonly MeshInstance3D _proxyLinesInstance;
    private readonly MeshInstance3D _proxyPointsInstance;
    private CancellationTokenSource? _generation;
    private int _generatedVersion = -1;
    private bool _disposed;

    private GliderSimulator? _simulator;
    private ProxyDeformer? _deformer;
    private Matrix4x4[] _skin = [];
    private Vector3[] _positionBuffer = [];
    private Vector3[] _normalBuffer = [];
    private Vector3[] _previousDisplay = [];
    private Vector3[] _currentDisplay = [];
    private Vector3[] _shown = [];
    private (double X, double Y, double Z) _displayOrigin;
    private readonly Stopwatch _clock = new();
    private readonly GamepadPilot _gamepad = new();
    private double _lastFrame;
    private float _pending;
    private readonly Dictionary<string, (float Start, float Until)> _releaseAt = [];
    private (double X, double Y, double Z) _recordOrigin;
    private readonly List<float> _recordTimes = [];
    private readonly List<Vector3[]> _recordFrames = [];
    private float _nextRecordTime;

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

    /// <summary>Initializes the preview of <paramref name="node"/>.</summary>
    public ParagliderPreview(ParagliderNode node, Func<ParagliderActions> actions)
    {
        _node = node;
        _actions = actions;
        _proxyLinesInstance = new MeshInstance3D(_proxyLines, new Material3D { BaseColor = Color.FromRgb(255, 196, 0), Unlit = true }) { Name = "Proxy", DrawOnTop = true, IsVisible = false };
        _proxyPointsInstance = new MeshInstance3D(_proxyPoints, new Material3D { BaseColor = Color.FromRgb(255, 112, 67), Unlit = true, PointSize = 5 }) { Name = "Proxy nodes", DrawOnTop = true, IsVisible = false };
        Scene.Instances.Add(_proxyLinesInstance);
        Scene.Instances.Add(_proxyPointsInstance);
        _node.PropertyChanged += OnNodePropertyChanged;
        Regenerate();
    }

    /// <summary>Gets the node.</summary>
    public ParagliderNode Node => _node;

    /// <summary>Gets the scene the viewport shows.</summary>
    public Scene3D Scene { get; } = new();

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
        UpdateMaterials(model);

        // Reuse the parts' meshes and instances when the part list is the same.
        while (_parts.Count > model.Parts.Count)
        {
            Scene.Instances.Remove(_parts[^1].Instance);
            _parts.RemoveAt(_parts.Count - 1);
        }
        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            var colors = part.Colors.Count == part.VertexCount ? part.Colors.ToArray() : null;
            if (p < _parts.Count)
            {
                var existing = _parts[p];
                existing.Mesh.SetGeometry(part.Positions.ToArray(), part.Indices.ToArray(), part.Normals.ToArray(), part.TexCoords.ToArray(), colors);
                existing.Instance.Material = _materials[part.Material];
                existing.Instance.Name = part.Name;
                _parts[p] = (part, existing.Mesh, existing.Instance, model.Skin[p]);
            }
            else
            {
                var mesh = new Mesh3D();
                mesh.SetGeometry(part.Positions.ToArray(), part.Indices.ToArray(), part.Normals.ToArray(), part.TexCoords.ToArray(), colors);
                var instance = new MeshInstance3D(mesh, _materials[part.Material]) { Name = part.Name };
                Scene.Instances.Insert(Scene.Instances.Count - 2, instance);
                _parts.Add((part, mesh, instance, model.Skin[p]));
            }
        }
        BuildProxyMeshes(model.Proxy.Model, model.Proxy.Model.Nodes.Select(n => n.Position).ToArray(), rebuild: true);
        UpdateVisibility();

        var proxy = model.Proxy.Model;
        Status = $"{model.TriangleCount / 1000.0:0.#}k triangles · {model.Shape.CellCount} cells · proxy: {proxy.Nodes.Count} nodes, " +
                 $"{proxy.Constraints.Count} constraints ({model.Design.ProxyComplexity}) · generated in {model.Elapsed.TotalMilliseconds:0} ms";
        if (first) ModelApplied?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMaterials(GliderModel model)
    {
        var design = model.Design;
        Material3D Get(GliderMaterial key) => _materials.TryGetValue(key, out var m) ? m : _materials[key] = new Material3D();

        var canopy = Get(GliderMaterial.Canopy);
        canopy.BaseColor = Color.FromRgb(255, 255, 255);
        canopy.Roughness = 0.72f;
        canopy.Transmission = (float)design.FabricTranslucency;
        canopy.DoubleSided = true;
        canopy.BaseColorTexture = model.BaseColor is { } baseColor ? new Texture3D(baseColor.Width, baseColor.Height, baseColor.Pixels) : null;
        canopy.NormalTexture = model.NormalMap is { } normal ? new Texture3D(normal.Width, normal.Height, normal.Pixels, srgb: false) : null;

        var ribs = Get(GliderMaterial.Ribs);
        ribs.BaseColor = ToColor(design.RibColor);
        ribs.Roughness = 0.8f;
        ribs.Transmission = (float)design.FabricTranslucency;

        var lines = Get(GliderMaterial.Lines);
        lines.BaseColor = Color.FromRgb(255, 255, 255);
        lines.Roughness = 0.6f;
        lines.DoubleSided = true; // low poly lines are flat ribbons

        var risers = Get(GliderMaterial.Risers);
        risers.BaseColor = Color.FromRgb(0x26, 0x32, 0x38);
        risers.Roughness = 0.9f;

        var metal = Get(GliderMaterial.Metal);
        metal.BaseColor = Color.FromRgb(210, 210, 215);
        metal.Metallic = 1;
        metal.Roughness = 0.3f;

        var toggles = Get(GliderMaterial.Toggles);
        toggles.BaseColor = ToColor(design.AccentColor);
        toggles.Roughness = 0.55f;
    }

    private static Color ToColor(string hex)
    {
        var c = CanopyTextureGenerator.ParseColor(hex, SKColors.Gray);
        return Color.FromRgb(c.Red, c.Green, c.Blue);
    }

    private void BuildProxyMeshes(ProxyModel proxy, Vector3[] positions, bool rebuild)
    {
        if (rebuild)
        {
            var indices = new List<uint>();
            foreach (var c in proxy.Constraints)
            {
                if (c.Kind is ConstraintKind.Chordwise or ConstraintKind.Spanwise or ConstraintKind.Rib or ConstraintKind.Line or ConstraintKind.Riser or ConstraintKind.Harness)
                {
                    indices.Add((uint)c.A);
                    indices.Add((uint)c.B);
                }
            }
            _proxyLines.SetGeometry(positions, indices.ToArray());
            _proxyPoints.SetGeometry(positions, Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray());
        }
        else
        {
            _proxyLines.UpdatePositions(positions);
            _proxyPoints.UpdatePositions(positions);
        }
    }

    partial void OnShowCanopyChanged(bool value) => UpdateVisibility();
    partial void OnShowRibsChanged(bool value) => UpdateVisibility();
    partial void OnShowRiggingChanged(bool value) => UpdateVisibility();
    partial void OnShowProxyChanged(bool value) => UpdateVisibility();

    private void UpdateVisibility()
    {
        foreach (var (part, _, instance, _) in _parts)
        {
            instance.IsVisible = part.Material switch
            {
                GliderMaterial.Canopy => ShowCanopy,
                GliderMaterial.Ribs => ShowRibs && ShowCanopy,
                _ => ShowRigging,
            };
        }
        _proxyLinesInstance.IsVisible = ShowProxy;
        _proxyPointsInstance.IsVisible = ShowProxy;
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
    [property: Command("Record", Group, Label = "Record", Icon = MaterialIcons.FiberManualRecord, Description = "Record the simulation, to export it as an animation", DefaultKeybinding = "Ctrl+R")]
    private void ToggleRecording()
    {
        IsRecording = !IsRecording;
        if (IsRecording)
        {
            _recordTimes.Clear();
            _recordFrames.Clear();
            _nextRecordTime = 0;
            _recordOrigin = _simulator?.Origin ?? default;
            if (!IsSimulating) StartSimulation();
        }
        ExportRecordingCommand.NotifyCanExecuteChanged();
    }

    private bool CanExportRecording() => !IsRecording && _recordFrames.Count > 1 && Model != null;

    [RelayCommand(CanExecute = nameof(CanExportRecording))]
    [property: Command("ExportRecording", Group, Label = "Export recording…", Icon = MaterialIcons.Movie,
        Description = "Export the high resolution glider with the recorded flight baked into its proxy joints (glTF)")]
    private async Task ExportRecordingAsync()
    {
        if (Model is not { } model) return;
        float start = _recordTimes[0];
        var animation = new ProxyAnimation("Simulation", _recordTimes.Select(t => t - start).ToArray(), _recordFrames.ToArray());
        await _actions().ExportAnimationAsync(_node, model, animation);
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
            _simulator = new GliderSimulator(model.Proxy.Model);
            _deformer = new ProxyDeformer(model.Proxy.Model);
            _skin = new Matrix4x4[model.Proxy.Model.Nodes.Count];
            int largest = model.Parts.Max(p => p.VertexCount);
            _positionBuffer = new Vector3[largest];
            _normalBuffer = new Vector3[largest];
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
        IsRecording = false;
        ToggleSimulationCommandLabel = "Simulate";
        if (_simulator is null) return;
        _simulator = null;
        _deformer = null;
        _releaseAt.Clear();
        Telemetry = string.Empty;
        // Back to the rest pose.
        foreach (var (part, mesh, _, _) in _parts) mesh.UpdatePositions(part.Positions.ToArray(), part.Normals.ToArray());
        if (Model is { } model) BuildProxyMeshes(model.Proxy.Model, model.Proxy.Model.Nodes.Select(n => n.Position).ToArray(), rebuild: false);
        SimulationStopped?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty] private string _toggleSimulationCommandLabel = "Simulate";

    /// <summary>Advances the simulation to the current time and updates the scene; the view calls it every rendered frame.</summary>
    /// <returns>Whether another frame is needed.</returns>
    public bool Advance()
    {
        if (!IsSimulating || _simulator is not { } sim || _deformer is not { } deformer || Model is not { } model) return false;
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
            sim.Step(StepTime);
            CaptureDisplay(sim, _currentDisplay);
            _pending -= StepTime;
            steps++;
            if (IsRecording && sim.Time >= _nextRecordTime) Record(sim);
        }
        // Too slow to keep up: drop the time rather than fall further behind.
        if (_pending > StepTime) _pending = StepTime;

        // Frames don't line up with the fixed steps: show the glider between the last two steps, so it moves smoothly.
        float alpha = Math.Clamp(_pending / StepTime, 0, 1);
        for (int i = 0; i < _shown.Length; i++) _shown[i] = Vector3.Lerp(_previousDisplay[i], _currentDisplay[i], alpha);

        deformer.ComputeSkinMatrices(_shown, _skin);
        foreach (var (part, mesh, instance, weights) in _parts)
        {
            if (!instance.IsVisible) continue;
            int count = part.VertexCount;
            ProxyDeformer.Deform(part.Positions, part.Normals, weights, _skin, _positionBuffer, _normalBuffer);
            mesh.UpdatePositions(_positionBuffer.AsSpan(0, count), _normalBuffer.AsSpan(0, count));
        }
        if (ShowProxy) BuildProxyMeshes(model.Proxy.Model, _shown, rebuild: false);

        var v = sim.PilotVelocity;
        float horizontal = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        var pressure = sim.CellPressure;
        int deflated = 0;
        foreach (float p in pressure) if (p < 0.4f) deflated++;
        Telemetry = $"{sim.Time,5:0.0} s   airspeed {sim.Airspeed * 3.6f:0} km/h   vario {v.Y:+0.0;-0.0} m/s   glide {(v.Y < -0.05f ? horizontal / -v.Y : 99):0.0}   " +
                    $"AoA {sim.CenterAngleOfAttack:0}°   deflated cells {deflated}/{pressure.Length}{(IsRecording ? $"   ● REC {_recordFrames.Count / 30.0:0.0} s" : "")}" +
                    (_gamepad.GamepadName is { } padName ? $"   gamepad: {padName}" : "");

        // The camera follows the point between the pilot and the canopy.
        var pilot = _shown[model.Proxy.Model.Pilot];
        var canopyCenter = _shown[model.Proxy.Model.Sections[model.Proxy.Model.Sections.Count / 2].LeadingEdge];
        Simulated?.Invoke(this, Vector3.Lerp(pilot, canopyCenter, 0.6f));
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

    private void Record(GliderSimulator sim)
    {
        // World positions relative to where the recording started, so the glider flies through the scene.
        var offset = new Vector3((float)(sim.Origin.X - _recordOrigin.X), (float)(sim.Origin.Y - _recordOrigin.Y), (float)(sim.Origin.Z - _recordOrigin.Z));
        var frame = sim.Positions.ToArray();
        for (int i = 0; i < frame.Length; i++) frame[i] += offset;
        _recordFrames.Add(frame);
        _recordTimes.Add(sim.Time);
        _nextRecordTime = sim.Time + 1 / 30f;
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
