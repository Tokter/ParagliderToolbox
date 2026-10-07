using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Threading;
using Atelier.Graphics3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The replay of a <see cref="RecordingNode"/>: generates the paraglider as it was flown (in the background), decodes the
/// frames, and shows them in a 3D scene; play at a chosen speed, pause, scrub and step frame by frame, and orbit around
/// the glider meanwhile. The commands are the <see cref="Group"/> keybinding group, active while the view has the focus.
/// </summary>
public sealed partial class RecordingPlayer : ObservableObject, IDisposable
{
    /// <summary>The keybinding group of the player's commands.</summary>
    public const string Group = "Recording playback";

    private const int PreviewTextureSize = 2048;
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1, 2];

    private readonly RecordingNode _node;
    private readonly GliderScene _scene = new();
    private readonly Stopwatch _clock = new();
    private FlightFrames? _frames;
    private Vector3[] _shown = [];
    private double _lastFrame;
    private bool _disposed;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _status = "Generating the paraglider…";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private float _time;
    [ObservableProperty] private float _speed = 1;
    [ObservableProperty] private string _telemetry = string.Empty;
    [ObservableProperty] private bool _showCanopy = true;
    [ObservableProperty] private bool _showRibs = true;
    [ObservableProperty] private bool _showRigging = true;
    [ObservableProperty] private bool _showProxy;

    /// <summary>Initializes the player of <paramref name="node"/> and starts loading it.</summary>
    public RecordingPlayer(RecordingNode node)
    {
        _node = node;
        Load();
    }

    /// <summary>Gets the recording node.</summary>
    public RecordingNode Node => _node;

    /// <summary>Gets the scene the viewport shows.</summary>
    public Scene3D Scene => _scene.Scene;

    /// <summary>Gets the recorded time (s).</summary>
    public float Duration => _node.Recording.Duration;

    /// <summary>Occurs once the glider is shown (the view frames it).</summary>
    public event EventHandler? Loaded;

    /// <summary>Occurs whenever the glider moved, with the point the camera follows.</summary>
    public event EventHandler<Vector3>? Moved;

    private void Load()
    {
        var recording = _node.Recording;
        if (recording.Design is not { } design || recording.FrameCount == 0)
        {
            IsLoading = false;
            Status = "The recording is empty.";
            return;
        }
        Task.Run(() =>
        {
            var model = GliderGenerator.Generate(design, new GenerateOptions(Math.Min(PreviewTextureSize, MeshSettings.FromDesign(design).TextureSize)));
            if (model.Proxy.Model.Nodes.Count != recording.ProxyNodes) throw new InvalidOperationException("The recording doesn't match its design's proxy.");
            return (Model: model, Frames: recording.Decode());
        }).ContinueWith(task => Dispatcher.Post(() =>
        {
            if (_disposed) return;
            IsLoading = false;
            if (task.Exception?.GetBaseException() is { } error)
            {
                Status = $"Can't replay: {error.Message}";
                return;
            }
            var (model, frames) = task.Result;
            _scene.Show(model);
            UpdateVisibility();
            _frames = frames;
            _shown = new Vector3[recording.ProxyNodes];
            Status = string.Create(CultureInfo.CurrentCulture,
                $"{recording.Duration:0.0} s at {1 / recording.FrameTime:0} frames per second · {design.ProxyComplexity} proxy, {recording.ProxyNodes} nodes · recorded {recording.Recorded:g}");
            ShowFrame();
            Loaded?.Invoke(this, EventArgs.Empty);
        }), TaskScheduler.Default);
    }

    #region Commands

    [RelayCommand]
    [property: Command("PlayRecording", Group, Label = "Play", Icon = MaterialIcons.PlayArrow, Description = "Play or pause the recording", DefaultKeybinding = "P")]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            return;
        }
        if (_frames is null) return;
        if (Time >= Duration) Time = 0;
        IsPlaying = true;
        _clock.Restart();
        _lastFrame = 0;
    }

    [RelayCommand]
    [property: Command("RestartRecording", Group, Label = "Restart", Icon = MaterialIcons.SkipPrevious, Description = "Back to the start of the recording", DefaultKeybinding = "R")]
    private void Restart() => Time = 0;

    [RelayCommand]
    [property: Command("PreviousFrame", Group, Label = "Previous frame", Icon = MaterialIcons.ChevronLeft, Description = "One frame back (pauses)", DefaultKeybinding = "Comma")]
    private void PreviousFrame() => StepFrames(-1);

    [RelayCommand]
    [property: Command("NextFrame", Group, Label = "Next frame", Icon = MaterialIcons.ChevronRight, Description = "One frame on (pauses)", DefaultKeybinding = "Period")]
    private void NextFrame() => StepFrames(1);

    [RelayCommand]
    [property: Command("SlowerPlayback", Group, Label = "Slower", Icon = MaterialIcons.FastRewind, Description = "Play slower (down to a tenth of real time)", DefaultKeybinding = "Minus")]
    private void Slower() => Speed = Speeds[Math.Max(0, SpeedIndex() - 1)];

    [RelayCommand]
    [property: Command("FasterPlayback", Group, Label = "Faster", Icon = MaterialIcons.FastForward, Description = "Play faster (up to twice real time)", DefaultKeybinding = "Equal")]
    private void Faster() => Speed = Speeds[Math.Min(Speeds.Length - 1, SpeedIndex() + 1)];

    [RelayCommand]
    [property: Command("ReplayToggleCanopy", Group, Label = "Show canopy", Icon = MaterialIcons.Paragliding, Description = "Show or hide the canopy and its ribs", DefaultKeybinding = "1")]
    private void ToggleCanopy() => ShowCanopy = !ShowCanopy;

    [RelayCommand]
    [property: Command("ReplayToggleRigging", Group, Label = "Show rigging", Icon = MaterialIcons.Timeline, Description = "Show or hide the lines, risers and toggles", DefaultKeybinding = "2")]
    private void ToggleRigging() => ShowRigging = !ShowRigging;

    [RelayCommand]
    [property: Command("ReplayToggleProxy", Group, Label = "Show physics proxy", Icon = MaterialIcons.Hub, Description = "Show or hide the simulation proxy's nodes and constraints", DefaultKeybinding = "3")]
    private void ToggleProxy() => ShowProxy = !ShowProxy;

    [RelayCommand]
    [property: Command("ReplayToggleRibs", Group, Label = "Show internal ribs", Icon = MaterialIcons.ViewColumn, Description = "Show or hide the internal ribs", DefaultKeybinding = "4")]
    private void ToggleRibs() => ShowRibs = !ShowRibs;

    private int SpeedIndex()
    {
        int best = 0;
        for (int i = 1; i < Speeds.Length; i++) if (Math.Abs(Speeds[i] - Speed) < Math.Abs(Speeds[best] - Speed)) best = i;
        return best;
    }

    private void StepFrames(int frames)
    {
        IsPlaying = false;
        float frameTime = _node.Recording.FrameTime;
        Time = Math.Clamp((MathF.Round(Time / frameTime) + frames) * frameTime, 0, Duration);
    }

    #endregion

    /// <summary>Advances the playback to the current time; the view calls it every rendered frame.</summary>
    /// <returns>Whether another frame is needed.</returns>
    public bool Advance()
    {
        if (!IsPlaying) return false;
        double now = _clock.Elapsed.TotalSeconds;
        float elapsed = (float)Math.Min(0.1, now - _lastFrame);
        _lastFrame = now;
        float time = Time + elapsed * Speed;
        if (time >= Duration)
        {
            time = Duration;
            IsPlaying = false;
        }
        Time = time;
        return IsPlaying;
    }

    partial void OnTimeChanged(float value) => ShowFrame();
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
        // Parts shown again are posed where the glider is now.
        if (_frames != null) ShowFrame();
    }

    // Poses the glider between the two frames around the current time, and shows that moment's flight data.
    private void ShowFrame()
    {
        if (_frames is not { } frames || frames.Positions.Length == 0) return;
        var recording = _node.Recording;
        float position = Math.Clamp(Time / recording.FrameTime, 0, frames.Positions.Length - 1);
        int index = Math.Min((int)position, frames.Positions.Length - 1);
        int next = Math.Min(index + 1, frames.Positions.Length - 1);
        float blend = position - index;
        var a = frames.Positions[index];
        var b = frames.Positions[next];
        for (int i = 0; i < _shown.Length; i++) _shown[i] = Vector3.Lerp(a[i], b[i], blend);
        _scene.Pose(_shown);

        var t = frames.Telemetry[blend < 0.5f ? index : next];
        var input = t.Inputs;
        string pulses = string.Join(" ", new[]
        {
            input.CollapseLeft > 0 ? "collapse left" : null, input.CollapseRight > 0 ? "collapse right" : null,
            input.Frontal > 0 ? "frontal" : null, input.BigEars > 0 ? "big ears" : null, input.WindY != 0 ? "gust" : null,
        }.Where(s => s != null));
        Telemetry = string.Create(CultureInfo.CurrentCulture,
            $"{Time,5:0.00} / {Duration:0.00} s   flight {t.Time:0.0} s   airspeed {t.Airspeed * 3.6f:0} km/h   vario {t.VerticalSpeed:+0.0;-0.0} m/s   " +
            $"G {t.LoadFactor:0.0}   AoA {t.AngleOfAttack:0}°   deflated cells {t.DeflatedCells}   " +
            $"brakes {input.BrakeLeft:P0}/{input.BrakeRight:P0}   bar {input.SpeedBar:P0}   shift {input.WeightShift:+0.00;-0.00}{(pulses.Length > 0 ? "   " + pulses : "")}");
        Moved?.Invoke(this, _scene.FollowPoint(_shown));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        IsPlaying = false;
    }
}
