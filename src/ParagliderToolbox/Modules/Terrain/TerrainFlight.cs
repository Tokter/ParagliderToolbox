using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>Where a fly key moves the camera.</summary>
public enum FlyDirection
{
    /// <summary>Where the camera looks.</summary>
    Forward,
    /// <summary>Away from where the camera looks.</summary>
    Back,
    /// <summary>To the camera's left, level.</summary>
    Left,
    /// <summary>To the camera's right, level.</summary>
    Right,
    /// <summary>Straight up.</summary>
    Up,
    /// <summary>Straight down.</summary>
    Down,
}

/// <summary>
/// Flies a terrain preview's camera through the landscape: while fly keys are held (<see cref="Press"/>,
/// <see cref="Release"/>) the camera moves where it looks, sideways, up or down, faster the higher it is above the
/// ground; <see cref="Look"/> turns the view without moving. The camera stays above the ground.
/// </summary>
/// <remarks>
/// The orbit camera's target moves with it, so orbiting and zooming keep working as before. The speed eases in and
/// out (a fifth of a second), so starting and stopping is smooth.
/// </remarks>
public sealed class TerrainFlight(OrbitCamera camera, Func<double, double, float?> groundHeight)
{
    /// <summary>The keybinding group of the fly commands; their target is the flight.</summary>
    public const string Group = "Terrain flight";

    /// <summary>How much faster the camera flies while Shift is held.</summary>
    public const float FastFactor = 4;

    /// <summary>The camera's speed per meter above the ground (1/s): at 500 m it flies 400 m/s.</summary>
    public const float SpeedPerHeight = 0.8f;

    /// <summary>The slowest speed (m/s), close to the ground.</summary>
    public const float MinSpeed = 15;

    /// <summary>The fastest speed (m/s) before Shift.</summary>
    public const float MaxSpeed = 3000;

    /// <summary>How close to the ground the camera may come (m).</summary>
    public const float Clearance = 2;

    /// <summary>How far the view turns per pixel dragged while looking around, in radians.</summary>
    public const float LookSpeed = 0.004f;

    private const float EaseTime = 0.2f;

    private readonly HashSet<FlyDirection> _held = [];
    private Vector3 _velocity;

    /// <summary>Gets the camera flown.</summary>
    public OrbitCamera Camera => camera;

    /// <summary>Gets or sets whether the camera flies <see cref="FastFactor"/> times faster (Shift).</summary>
    public bool IsFast { get; set; }

    /// <summary>Gets whether the camera is moving (keys held, or still slowing down).</summary>
    public bool IsMoving => _held.Count > 0 || _velocity != Vector3.Zero;

    /// <summary>Gets the camera's speed (m/s).</summary>
    public float Speed => _velocity.Length();

    /// <summary>Gets the camera's height above the ground (m), or its height when the ground there isn't known.</summary>
    public float HeightAboveGround
    {
        get
        {
            var position = camera.Position;
            return position.Y - (groundHeight(position.X, position.Z) ?? 0);
        }
    }

    /// <summary>Starts flying in <paramref name="direction"/> (a key went down).</summary>
    public void Press(FlyDirection direction) => _held.Add(direction);

    /// <summary>Stops flying in <paramref name="direction"/> (its key went up).</summary>
    public void Release(FlyDirection direction) => _held.Remove(direction);

    /// <summary>Lets go of every key (the focus left, or the view closed); the camera slows to a stop.</summary>
    public void ReleaseAll() => _held.Clear();

    /// <summary>Moves the camera by the time passed (s); returns whether it is still moving.</summary>
    public bool Update(float seconds)
    {
        if (!IsMoving) return false;
        seconds = Math.Clamp(seconds, 0, 0.1f);
        var wish = Vector3.Zero;
        foreach (var direction in _held) wish += Unit(direction);
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * CruiseSpeed() : Vector3.Zero;
        _velocity += (target - _velocity) * (1 - MathF.Exp(-seconds / EaseTime));
        if (_held.Count == 0 && _velocity.Length() < 0.5f) _velocity = Vector3.Zero;
        Move(_velocity * seconds);
        return IsMoving;
    }

    /// <summary>Moves the camera as far as a second of flight in <paramref name="direction"/> (a command run from a menu).</summary>
    public void Step(FlyDirection direction) => Move(Unit(direction) * CruiseSpeed());

    /// <summary>Turns the view about the camera's position (yaw positive turns left, pitch positive looks down).</summary>
    public void Look(float yaw, float pitch)
    {
        var position = camera.Position;
        camera.SetAngles(camera.Yaw + yaw, camera.Pitch + pitch);
        camera.Target = position - camera.Backward * camera.Distance;
    }

    // The speed for the height above the ground: slow near it, fast high up.
    private float CruiseSpeed() =>
        Math.Clamp(HeightAboveGround * SpeedPerHeight, MinSpeed, MaxSpeed) * (IsFast ? FastFactor : 1);

    private Vector3 Unit(FlyDirection direction) => direction switch
    {
        FlyDirection.Forward => -camera.Backward,
        FlyDirection.Back => camera.Backward,
        FlyDirection.Left => -camera.Right,
        FlyDirection.Right => camera.Right,
        FlyDirection.Up => Vector3.UnitY,
        _ => -Vector3.UnitY,
    };

    private void Move(Vector3 delta)
    {
        var target = camera.Target + delta;
        var position = target + camera.Backward * camera.Distance;
        if (groundHeight(position.X, position.Z) is { } ground && position.Y < ground + Clearance)
        {
            target.Y += ground + Clearance - position.Y;
            _velocity.Y = Math.Max(0, _velocity.Y);
        }
        camera.Target = target;
    }
}

/// <summary>
/// The fly commands of the terrain preview, in the <see cref="TerrainFlight.Group"/> keybinding group with the flight
/// as their target: W, A, S, D, E and Q move the camera while held (the view tracks the keys bound to them, so they can
/// be rebound to other single keys), and a right drag looks around. From a menu or the palette a fly command moves one
/// second's worth.
/// </summary>
public static class TerrainFlightCommands
{
    /// <summary>Registers the commands that aren't registered yet (with the users' changes applied).</summary>
    public static void Register()
    {
        Add("FlyForward", "W", new FlyCommand(FlyDirection.Forward), "Fly forward", "Fly where the camera looks while the key is held (Shift: faster)", MaterialIcons.ArrowUpward);
        Add("FlyBack", "S", new FlyCommand(FlyDirection.Back), "Fly back", "Fly backwards while the key is held (Shift: faster)", MaterialIcons.ArrowDownward);
        Add("FlyLeft", "A", new FlyCommand(FlyDirection.Left), "Fly left", "Fly to the left while the key is held (Shift: faster)", MaterialIcons.ArrowBack);
        Add("FlyRight", "D", new FlyCommand(FlyDirection.Right), "Fly right", "Fly to the right while the key is held (Shift: faster)", MaterialIcons.ArrowForward);
        Add("FlyUp", "E", new FlyCommand(FlyDirection.Up), "Fly up", "Climb while the key is held (Shift: faster)", MaterialIcons.North);
        Add("FlyDown", "Q", new FlyCommand(FlyDirection.Down), "Fly down", "Descend while the key is held (Shift: faster)", MaterialIcons.South);
        Add("LookAround", "RightDrag", new LookCommand(), "Look around", "Turn the view without moving the camera by dragging", MaterialIcons.Visibility);
    }

    /// <summary>Gets the direction of the fly command <paramref name="key"/> is bound to (without modifiers), or <c>null</c>.</summary>
    public static FlyDirection? DirectionOf(Key key) =>
        KeybindingManager.FindKeybinding(TerrainFlight.Group, key, ModifierKeys.None)?.Command is FlyCommand fly ? fly.Direction : null;

    private static void Add(string name, string keybinding, AtelierCommand command, string label, string description, string icon)
    {
        foreach (var registered in KeybindingManager.GetKeybindings(TerrainFlight.Group))
        {
            if (registered.Name == name) return;
        }
        KeybindingManager.RegisterKeybinding(new KeybindingDescriptor(name, TerrainFlight.Group, keybinding, command,
            label: label, description: description, icon: icon));
    }

    /// <summary>Flies one way: held, while its key is down (handled by the view); run, a second's worth.</summary>
    public sealed class FlyCommand(FlyDirection direction) : AtelierCommand
    {
        /// <summary>Gets the direction.</summary>
        public FlyDirection Direction { get; } = direction;

        /// <inheritdoc/>
        public override bool CanExecute(object? parameter) => parameter is TerrainFlight;

        /// <inheritdoc/>
        public override void Execute(object? parameter)
        {
            if (parameter is TerrainFlight flight) flight.Step(Direction);
        }
    }

    /// <summary>Turns the view about the camera by dragging; Escape turns it back.</summary>
    public sealed class LookCommand : DragCommand
    {
        /// <inheritdoc/>
        public override bool CanExecute(object? parameter) => parameter is TerrainFlight;

        /// <inheritdoc/>
        public override IDragOperation? BeginDrag(DragStart start) =>
            start.Target is TerrainFlight flight ? new Operation(flight, start.ScreenPosition) : null;

        private sealed class Operation(TerrainFlight flight, Point start) : IDragOperation
        {
            private readonly Vector3 _target = flight.Camera.Target;
            private readonly float _yaw = flight.Camera.Yaw;
            private readonly float _pitch = flight.Camera.Pitch;
            private Point _last = start;

            public void Update(Point screenPosition, ModifierKeys modifiers)
            {
                var delta = screenPosition - _last;
                _last = screenPosition;
                // Dragging right turns the view right; dragging up looks up.
                flight.Look(-delta.X * TerrainFlight.LookSpeed, delta.Y * TerrainFlight.LookSpeed);
            }

            public void Complete(Point screenPosition, ModifierKeys modifiers) => Update(screenPosition, modifiers);

            public void Cancel()
            {
                flight.Camera.SetAngles(_yaw, _pitch);
                flight.Camera.Target = _target;
            }
        }
    }
}
