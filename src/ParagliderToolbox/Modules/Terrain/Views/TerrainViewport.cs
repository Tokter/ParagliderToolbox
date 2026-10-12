using System.Diagnostics;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Graphics3D;
using Atelier.Layout;
using Atelier.Markup;

namespace ParagliderToolbox.Modules.Terrain.Views;

/// <summary>
/// The 3D view of a terrain's tiles, shared by the terrain's and the airflow's detail views: the levels of detail chosen
/// for the camera every frame, the camera flight (<see cref="TerrainFlight"/>: W, A, S, D …), clipping planes that follow
/// the camera, and an overlay with the altitude, the height above the ground and the speed (and, until the first flight
/// of the session, the keys that fly).
/// </summary>
/// <remarks>
/// The hosting view adds <see cref="Flight"/> as a keybinding scope of its <see cref="TerrainFlight.Group"/> and forwards
/// its keys to <see cref="HandleKeyDown"/> and <see cref="HandleKeyUp"/>, and calls <see cref="ReleaseKeys"/> when the
/// focus leaves it (keys released elsewhere don't reach it).
/// </remarks>
public sealed class TerrainViewport : Grid
{
    // Shown until the first flight of the session.
    private static bool s_hasFlown;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _info, _hint;
    private readonly Border _overlay;
    private double _lastFrame;
    private bool _queued;

    /// <summary>Initializes a view of <paramref name="tiles"/>, drawing <paramref name="scene"/> (the tiles' own by default).</summary>
    public TerrainViewport(TerrainScene tiles, Scene3D? scene = null)
    {
        Tiles = tiles;
        Viewport = new Viewport3D().Scene(scene ?? tiles.Scene).ShowGrid(false);
        Viewport.Camera.FieldOfView = 0.9f;
        Viewport.Camera.Changed += (_, _) => RequestRender();
        TerrainFlightCommands.Register();
        Flight = new TerrainFlight(Viewport.Camera, (x, z) => Tiles.HeightAt(x, z));
        Viewport.Rendered += (_, _) => OnRendered();

        static string Key(string name)
        {
            string gesture = KeybindingManager.FindCommand(TerrainFlight.Group, name)?.Keybinding ?? "";
            // "RightDrag" reads "right drag".
            return gesture.EndsWith("Drag", StringComparison.Ordinal) ? gesture[..^4].ToLowerInvariant() + " drag" : gesture;
        }
        var white = Color.FromRgb(255, 255, 255);
        _info = new TextBlock().BodyMedium().Foreground(white).FontFamily("Consolas");
        _hint = new TextBlock(
                $"{Key("FlyForward")} {Key("FlyLeft")} {Key("FlyBack")} {Key("FlyRight")} fly · {Key("FlyUp")} {Key("FlyDown")} up, down · Shift faster · {Key("LookAround")} looks around")
            .LabelSmall().Foreground(Color.FromArgb(0xC8, 255, 255, 255))
            .IsVisible(!s_hasFlown);
        _overlay = new Border()
            .HorizontalAlignment(HorizontalAlignment.Left).VerticalAlignment(VerticalAlignment.Top)
            .Margin(10).Padding(10, 6).CornerRadius(8)
            .Background(Color.FromArgb(0x8C, 0x12, 0x16, 0x1C))
            .IsHitTestVisible(false)
            .IsVisible(false)
            .Child(new StackPanel().Spacing(2).Children(_info, _hint));
        this.Children(Viewport, _overlay);
    }

    /// <summary>Gets the 3D viewport.</summary>
    public Viewport3D Viewport { get; }

    /// <summary>Gets the camera flight.</summary>
    public TerrainFlight Flight { get; }

    /// <summary>Gets the tiles shown.</summary>
    public TerrainScene Tiles { get; }

    /// <summary>
    /// Gets or sets what runs every frame with the seconds since the last one (at most a tenth of a second); it returns
    /// whether to draw another frame (an animation).
    /// </summary>
    public Func<double, bool>? Animate { get; set; }

    /// <summary>Occurs after every frame's levels of detail were chosen (the tiles shown may have changed).</summary>
    public event EventHandler? LevelsChosen;

    /// <summary>Draws again (once, however often it is asked before the next frame).</summary>
    public void RequestRender()
    {
        if (_queued) return;
        _queued = true;
        Atelier.Core.Threading.Dispatcher.Post(() =>
        {
            _queued = false;
            Viewport.RequestRender();
        });
    }

    /// <summary>Looks at the terrain in a three-quarter view from the south and above, like the view from a launch.</summary>
    public void Frame()
    {
        if (Tiles.Bounds() is not { } bounds) return;
        // Framing fits the box's diagonal; a flat terrain fills the view from closer (its far corners just outside).
        Viewport.Camera.SetAngles(0.5f, 0.4f);
        Viewport.Camera.Frame(bounds.Min, bounds.Max);
        Viewport.Camera.Distance *= 0.5f;
        RequestRender();
    }

    /// <summary>
    /// Flies while a key bound to a fly command (alone, or with Shift for speed) is held; returns whether it took the key.
    /// With Ctrl or Alt the key is left to the other shortcuts.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        Flight.IsFast = (e.Modifiers & ModifierKeys.Shift) != 0 || e.Key is Key.LeftShift or Key.RightShift;
        if ((e.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return false;
        if (TerrainFlightCommands.DirectionOf(e.Key) is not { } direction) return false;
        if (!Flight.IsMoving) _lastFrame = _clock.Elapsed.TotalSeconds;
        Flight.Press(direction);
        s_hasFlown = true;
        _hint.IsVisible(false);
        Viewport.RequestRender();
        return true;
    }

    /// <summary>Stops flying in the released key's direction.</summary>
    public void HandleKeyUp(KeyEventArgs e)
    {
        Flight.IsFast = (e.Modifiers & ModifierKeys.Shift) != 0 && e.Key is not (Key.LeftShift or Key.RightShift);
        if (TerrainFlightCommands.DirectionOf(e.Key) is { } direction) Flight.Release(direction);
    }

    /// <summary>Stops the camera (every key released).</summary>
    public void ReleaseKeys() => Flight.ReleaseAll();

    private void OnRendered()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _lastFrame;
        _lastFrame = now;
        bool again = Flight.Update((float)elapsed);
        var camera = Viewport.Camera;
        Tiles.Select(camera.Position, camera.FieldOfView, Math.Max(1, Viewport.PixelSize.Height));
        // Meshes still to make, flying or an animation: another frame.
        again |= Tiles.NeedsAnotherPass;
        if (Animate is { } animate) again |= animate(Math.Min(elapsed, 0.1));
        ShowInfo();
        KeepClippingInRange();
        LevelsChosen?.Invoke(this, EventArgs.Empty);
        if (again) Viewport.RequestRender();
    }

    // Where the camera is, as a pilot reads it: the altitude and the height above the ground (and the speed, flying).
    private void ShowInfo()
    {
        if (Tiles.ShownTiles == 0)
        {
            _overlay.IsVisible(false);
            return;
        }
        var position = Viewport.Camera.Position;
        string info = string.Create(CultureInfo.CurrentCulture, $"{position.Y:#,0} m");
        if (Tiles.HeightAt(position.X, position.Z) is not null) info += string.Create(CultureInfo.CurrentCulture, $" · {Flight.HeightAboveGround:#,0} m above ground");
        if (Flight.IsMoving) info += string.Create(CultureInfo.CurrentCulture, $" · {Flight.Speed * 3.6f:#,0} km/h");
        _info.Text = info;
        _overlay.IsVisible(true);
    }

    // The clipping planes follow how close the camera is to its target and to the ground, so the ground doesn't clip when
    // zoomed in or flying low.
    private void KeepClippingInRange()
    {
        var camera = Viewport.Camera;
        float extent = (float)(Tiles.Layout?.Extent ?? 1000);
        float near = Math.Clamp(Math.Min(camera.Distance / 2000, Math.Max(0.5f, Flight.HeightAboveGround) / 3), 0.05f, 50);
        float far = Math.Max(extent * 3, camera.Distance * 20);
        if (Math.Abs(camera.Near - near) > near * 0.25f) camera.Near = near;
        if (Math.Abs(camera.Far - far) > far * 0.25f) camera.Far = far;
    }
}
