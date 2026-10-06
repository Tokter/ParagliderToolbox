using Atelier.Core.Platform;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>The pilot's controls a gamepad sets; <c>null</c> for a control the gamepad left alone.</summary>
/// <param name="BrakeLeft">The left brake.</param>
/// <param name="BrakeRight">The right brake.</param>
/// <param name="SpeedBar">The speed bar.</param>
/// <param name="WeightShift">The weight shift.</param>
/// <param name="Pressed">The buttons pressed since the last read (held buttons count once).</param>
public readonly record struct PilotControls(float? BrakeLeft, float? BrakeRight, float? SpeedBar, float? WeightShift, GamepadButtons Pressed = GamepadButtons.None);

/// <summary>
/// Flies the simulation with a game controller (Xbox layout): the left trigger pulls the left brake, the right trigger
/// the right brake, the left stick shifts the pilot's weight (left/right) and pushes the speed bar (forward); A starts and
/// pauses the simulation, the left and right bumpers collapse the left and right side.
/// </summary>
/// <remarks>
/// A control follows the gamepad only when its stick or trigger moves, so the mouse can still set it with the sliders:
/// whichever was used last wins. Small movements around the rest position are ignored (dead zones). Buttons report once
/// when pressed, not while held.
/// </remarks>
public sealed class GamepadPilot
{
    /// <summary>The stick dead zone (radial).</summary>
    public const float StickDeadzone = 0.15f;

    /// <summary>The trigger dead zone.</summary>
    public const float TriggerDeadzone = 0.04f;

    private const float Change = 0.01f;
    private float? _brakeLeft, _brakeRight, _speedBar, _weightShift;
    private GamepadButtons _held = GamepadButtons.None;
    private bool _connected;

    /// <summary>Gets the name of the gamepad last read, or <c>null</c> when none is connected.</summary>
    public string? GamepadName { get; private set; }

    /// <summary>Reads <paramref name="state"/> and returns the controls that changed since the last read.</summary>
    public PilotControls Read(GamepadState state)
    {
        if (!state.IsConnected)
        {
            GamepadName = null;
            _brakeLeft = _brakeRight = _speedBar = _weightShift = null;
            _held = GamepadButtons.None;
            _connected = false;
            return default;
        }
        GamepadName = state.Name;

        // Buttons already down when the gamepad appears (or the reading starts) don't count as presses.
        var pressed = _connected ? state.Buttons & ~_held : GamepadButtons.None;
        _held = state.Buttons;
        _connected = true;

        var stick = Gamepads.ApplyDeadzone(state.LeftStick, StickDeadzone);
        return new PilotControls(
            Changed(ref _brakeLeft, Gamepads.ApplyDeadzone(state.LeftTrigger, TriggerDeadzone)),
            Changed(ref _brakeRight, Gamepads.ApplyDeadzone(state.RightTrigger, TriggerDeadzone)),
            Changed(ref _speedBar, Math.Max(0, stick.Y)),
            // Stick left (−X) shifts the weight to the left wing (+1).
            Changed(ref _weightShift, -stick.X),
            pressed);
    }

    // The new value when it moved (or on the first read, unless at rest), otherwise null.
    private static float? Changed(ref float? last, float value)
    {
        bool moved = last is { } previous ? Math.Abs(value - previous) > Change : Math.Abs(value) > Change;
        if (!moved) return null;
        last = value;
        return value;
    }
}
