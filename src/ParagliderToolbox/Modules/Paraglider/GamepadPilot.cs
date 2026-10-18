using System.Numerics;
using Atelier.Core.Platform;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>The pilot's controls a gamepad sets; <c>null</c> for a control the gamepad left alone.</summary>
/// <param name="BrakeLeft">The left brake.</param>
/// <param name="BrakeRight">The right brake.</param>
/// <param name="SpeedBar">The speed bar.</param>
/// <param name="WeightShift">The weight shift.</param>
/// <param name="Pressed">The buttons pressed since the last read (held buttons count once).</param>
/// <param name="ARiserLeft">The left A riser (while <see cref="GamepadPilot.TriggersPullARisers"/>).</param>
/// <param name="ARiserRight">The right A riser.</param>
/// <param name="Stick">The left stick (+X right, +Y forward, dead zone applied), every read: walking on the ground.</param>
public readonly record struct PilotControls(float? BrakeLeft, float? BrakeRight, float? SpeedBar, float? WeightShift, GamepadButtons Pressed = GamepadButtons.None,
    float? ARiserLeft = null, float? ARiserRight = null, Vector2 Stick = default);

/// <summary>
/// Flies the simulation with a game controller (Xbox layout): the left trigger pulls the left brake, the right trigger
/// the right brake, the left stick shifts the pilot's weight (left/right) and pushes the speed bar (forward); A starts and
/// pauses the simulation, the left and right bumpers collapse the left and right side. Launching, the triggers pull the
/// A risers instead (<see cref="TriggersPullARisers"/>) and the stick walks (<see cref="PilotControls.Stick"/>).
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
    private float? _triggerLeft, _triggerRight, _speedBar, _weightShift;
    private GamepadButtons _held = GamepadButtons.None;
    private bool _connected;
    private bool _triggersPullARisers, _heldLeft, _heldRight;

    /// <summary>
    /// Gets or sets whether the triggers pull the A risers instead of the brakes. After a switch, a trigger takes on its
    /// new control only once it has been let go: a trigger held on the A riser doesn't become a pulled brake.
    /// </summary>
    public bool TriggersPullARisers
    {
        get => _triggersPullARisers;
        set
        {
            if (_triggersPullARisers == value) return;
            _triggersPullARisers = value;
            _heldLeft = _heldRight = true;
        }
    }

    /// <summary>Gets the name of the gamepad last read, or <c>null</c> when none is connected.</summary>
    public string? GamepadName { get; private set; }

    /// <summary>Reads <paramref name="state"/> and returns the controls that changed since the last read.</summary>
    public PilotControls Read(GamepadState state)
    {
        if (!state.IsConnected)
        {
            GamepadName = null;
            _triggerLeft = _triggerRight = _speedBar = _weightShift = null;
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
        float? left = Trigger(ref _triggerLeft, ref _heldLeft, Gamepads.ApplyDeadzone(state.LeftTrigger, TriggerDeadzone));
        float? right = Trigger(ref _triggerRight, ref _heldRight, Gamepads.ApplyDeadzone(state.RightTrigger, TriggerDeadzone));
        bool risers = _triggersPullARisers;
        return new PilotControls(
            risers ? null : left,
            risers ? null : right,
            Changed(ref _speedBar, Math.Max(0, stick.Y)),
            // Stick left (−X) shifts the weight to the left wing (+1).
            Changed(ref _weightShift, -stick.X),
            pressed,
            risers ? left : null,
            risers ? right : null,
            stick);
    }

    // A trigger's new value when it moved, unless it is still held from before a switch (then nothing until let go).
    private static float? Trigger(ref float? last, ref bool held, float value)
    {
        if (held)
        {
            if (value > 0) return null;
            held = false;
            last = 0;
            return null;
        }
        return Changed(ref last, value);
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
