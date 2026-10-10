using System.Numerics;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
using Atelier.Graphics3D;
using ParagliderToolbox.Modules.Terrain;

namespace ParagliderToolbox.Tests.Terrain;

public class TerrainFlightTests
{
    // A camera 1000 m up, 500 m above flat ground, looking level along −Z (north), 100 m behind its target.
    private static TerrainFlight Create(float ground = 500)
    {
        var camera = new OrbitCamera { Target = new Vector3(0, 1000, -100), Distance = 100 };
        camera.SetAngles(0, 0);
        return new TerrainFlight(camera, (_, _) => ground);
    }

    private static void Fly(TerrainFlight flight, float seconds)
    {
        for (float t = 0; t < seconds; t += 0.02f) flight.Update(0.02f);
    }

    [Fact]
    public void HoldingForward_FliesWhereTheCameraLooks_FasterHigherAboveTheGround()
    {
        var flight = Create();
        var start = flight.Camera.Position;
        flight.Press(FlyDirection.Forward);
        Fly(flight, 2);

        var moved = flight.Camera.Position - start;
        Assert.Equal(0, moved.X, 3);
        Assert.Equal(0, moved.Y, 3);
        Assert.InRange(moved.Z, -800, -650);
        // 500 m above the ground: 0.8 × 500 m/s.
        Assert.Equal(400, flight.Speed, 0);
        Assert.Equal(500, flight.HeightAboveGround, 3);
    }

    [Fact]
    public void ReleasingTheKeys_SlowsToAStop()
    {
        var flight = Create();
        flight.Press(FlyDirection.Right);
        Fly(flight, 1);
        flight.Release(FlyDirection.Right);
        Assert.True(flight.IsMoving);
        Fly(flight, 2);
        Assert.False(flight.IsMoving);
        Assert.Equal(0, flight.Speed);
        Assert.True(flight.Camera.Position.X > 300);
    }

    [Fact]
    public void Shift_FliesFourTimesFaster_AndDiagonalsAreNoFaster()
    {
        var flight = Create();
        flight.IsFast = true;
        flight.Press(FlyDirection.Forward);
        flight.Press(FlyDirection.Left);
        Fly(flight, 2);
        Assert.Equal(400 * TerrainFlight.FastFactor, flight.Speed, 0);
    }

    [Fact]
    public void TheCamera_StaysAboveTheGround()
    {
        var flight = Create(ground: 990);
        flight.Press(FlyDirection.Down);
        Fly(flight, 3);
        Assert.InRange(flight.Camera.Position.Y, 990 + TerrainFlight.Clearance - 0.01f, 990 + TerrainFlight.Clearance + 0.5f);
        // Close to the ground it flies at the slowest speed.
        flight.ReleaseAll();
        flight.Press(FlyDirection.Forward);
        Fly(flight, 2);
        Assert.Equal(TerrainFlight.MinSpeed, flight.Speed, 0);
    }

    [Fact]
    public void LookingAround_TurnsTheView_WithoutMovingTheCamera()
    {
        var flight = Create();
        var position = flight.Camera.Position;
        flight.Look(0.5f, -0.2f);
        Assert.Equal(0.5f, flight.Camera.Yaw, 5);
        Assert.Equal(-0.2f, flight.Camera.Pitch, 5);
        Assert.True(Vector3.Distance(position, flight.Camera.Position) < 1e-3f);
    }

    [Fact]
    public void AStepFromTheMenu_FliesASecondsWorth()
    {
        var flight = Create();
        var start = flight.Camera.Position;
        flight.Step(FlyDirection.Up);
        Assert.Equal(400, flight.Camera.Position.Y - start.Y, 3);
        Assert.False(flight.IsMoving);
    }

    [Fact]
    public void TheFlyKeys_AreCommands_TheUserCanRebind()
    {
        TerrainFlightCommands.Register();
        Assert.Equal(FlyDirection.Forward, TerrainFlightCommands.DirectionOf(Key.W));
        Assert.Equal(FlyDirection.Left, TerrainFlightCommands.DirectionOf(Key.A));
        Assert.Equal(FlyDirection.Down, TerrainFlightCommands.DirectionOf(Key.Q));
        Assert.Null(TerrainFlightCommands.DirectionOf(Key.X));
        Assert.Equal("RightDrag", KeybindingManager.FindCommand(TerrainFlight.Group, "LookAround")!.Keybinding);

        try
        {
            KeybindingManager.SetCustomization(TerrainFlight.Group, "FlyForward", new CommandCustomization(Keybinding: "I"));
            Assert.Equal(FlyDirection.Forward, TerrainFlightCommands.DirectionOf(Key.I));
            Assert.Null(TerrainFlightCommands.DirectionOf(Key.W));
        }
        finally
        {
            KeybindingManager.ResetCustomization(TerrainFlight.Group, "FlyForward");
        }
        Assert.Equal(FlyDirection.Forward, TerrainFlightCommands.DirectionOf(Key.W));

        var flight = Create();
        var command = KeybindingManager.FindCommand(TerrainFlight.Group, "FlyBack")!.Command;
        Assert.True(command.CanExecute(flight));
        command.Execute(flight);
        Assert.True(flight.Camera.Position.Z > 0);
    }
}
