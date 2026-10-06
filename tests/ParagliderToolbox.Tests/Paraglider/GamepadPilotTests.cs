using System.Numerics;
using Atelier.Core.Platform;
using ParagliderToolbox.Modules.Paraglider;

namespace ParagliderToolbox.Tests.Paraglider;

public class GamepadPilotTests
{
    private static GamepadState Pad(Vector2 leftStick = default, float leftTrigger = 0, float rightTrigger = 0) =>
        new(true, "Xbox Controller", leftStick, Vector2.Zero, leftTrigger, rightTrigger, GamepadButtons.None);

    [Fact]
    public void Triggers_PullTheBrakes_AndTheStickShiftsWeightAndPushesTheSpeedBar()
    {
        var pilot = new GamepadPilot();
        var controls = pilot.Read(Pad(new Vector2(-1, 1) / MathF.Sqrt(2) * 0.99f, leftTrigger: 1, rightTrigger: 0.5f));

        Assert.Equal(1, controls.BrakeLeft!.Value, 3);
        Assert.InRange(controls.BrakeRight!.Value, 0.45f, 0.5f);
        Assert.InRange(controls.SpeedBar!.Value, 0.6f, 0.75f);
        Assert.InRange(controls.WeightShift!.Value, 0.6f, 0.75f); // stick left: weight to the left
        Assert.Equal("Xbox Controller", pilot.GamepadName);
    }

    [Fact]
    public void PullingTheStickBack_DoesNotUseTheSpeedBar()
    {
        var controls = new GamepadPilot().Read(Pad(new Vector2(0, -1)));
        Assert.Null(controls.SpeedBar);
    }

    [Fact]
    public void ControlsAtRest_LeaveTheSlidersAlone_UntilTheyMove()
    {
        var pilot = new GamepadPilot();
        // Resting noise inside the dead zones: nothing changes.
        Assert.Equal(default, pilot.Read(Pad(new Vector2(0.05f, -0.04f), leftTrigger: 0.02f)));

        var pressed = pilot.Read(Pad(leftTrigger: 0.6f));
        Assert.NotNull(pressed.BrakeLeft);
        Assert.Null(pressed.BrakeRight);

        // Held still: the slider can be moved with the mouse without the gamepad taking it back.
        Assert.Null(pilot.Read(Pad(leftTrigger: 0.6f)).BrakeLeft);

        // Released: the brake goes back to zero.
        Assert.Equal(0, pilot.Read(Pad()).BrakeLeft);
    }

    [Fact]
    public void WithoutAGamepad_NothingChanges()
    {
        var pilot = new GamepadPilot();
        Assert.Equal(default, pilot.Read(GamepadState.Disconnected));
        Assert.Null(pilot.GamepadName);
    }
}

public class GamepadButtonTests
{
    private static GamepadState Pad(GamepadButtons buttons) =>
        new(true, "Xbox Controller", Vector2.Zero, Vector2.Zero, 0, 0, buttons);

    [Fact]
    public void AButtonPress_CountsOnce_UntilReleasedAndPressedAgain()
    {
        var pilot = new GamepadPilot();
        Assert.Equal(GamepadButtons.None, pilot.Read(Pad(GamepadButtons.None)).Pressed);
        Assert.Equal(GamepadButtons.A, pilot.Read(Pad(GamepadButtons.A)).Pressed);
        Assert.Equal(GamepadButtons.None, pilot.Read(Pad(GamepadButtons.A)).Pressed);
        Assert.Equal(GamepadButtons.None, pilot.Read(Pad(GamepadButtons.None)).Pressed);
        Assert.Equal(GamepadButtons.A, pilot.Read(Pad(GamepadButtons.A)).Pressed);
    }

    [Fact]
    public void ButtonsHeldWhenTheGamepadAppears_AreNotPresses()
    {
        var pilot = new GamepadPilot();
        Assert.Equal(GamepadButtons.None, pilot.Read(Pad(GamepadButtons.LeftBumper)).Pressed);
        Assert.Equal(GamepadButtons.RightBumper, pilot.Read(Pad(GamepadButtons.LeftBumper | GamepadButtons.RightBumper)).Pressed);
    }

    [Fact]
    public void ReconnectingResetsTheButtons()
    {
        var pilot = new GamepadPilot();
        pilot.Read(Pad(GamepadButtons.None));
        pilot.Read(GamepadState.Disconnected);
        Assert.Equal(GamepadButtons.None, pilot.Read(Pad(GamepadButtons.A)).Pressed);
    }
}
