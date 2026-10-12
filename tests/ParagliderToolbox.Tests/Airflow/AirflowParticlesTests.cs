using System.Numerics;
using Atelier.Graphics3D;
using ParagliderToolbox.Modules.Airflow;

namespace ParagliderToolbox.Tests.Airflow;

public class AirflowParticlesTests
{
    // The terrain: 500 m, as the field's columns, but a 20 m deep step west of x = 0 that they don't see.
    private static float Terrain(double x, double z) => x < 0 ? 480 : 500;

    // Particles in a 3 m/s wind from the west over the field, moved on a moment so their mesh holds them; returns their heads.
    private static (AirflowParticles Particles, Func<Vector3[]> Heads) Stream(float height, Func<(Vector2, float)>? focus)
    {
        var field = ConvectionModelTests.Field(null, baseFlow: false);
        Array.Fill(field.WindX, 3f);
        var scene = new Scene3D();
        var particles = new AirflowParticles(scene) { Height = height, Focus = focus };
        particles.Show(field, Terrain);
        var mesh = scene.Instances[^1].Mesh;
        return (particles, () =>
        {
            particles.Step(0.001);
            return mesh.Positions.Where((_, i) => i % 10 == 0).ToArray();
        });
    }

    [Fact]
    public void AtZero_TheyStartJustAboveTheGround_WhereTheAirIs()
    {
        var (_, heads) = Stream(0, null);

        // 1–10 m above the ground; where the terrain steps below the field's columns, above the columns (still air below).
        Assert.All(heads(), p => Assert.InRange(p.Y, 501, 510.01));
    }

    [Fact]
    public void TheyStartInABandAroundTheHeight_AndAgainWhenItChanges()
    {
        var (particles, heads) = Stream(0, null);
        particles.Height = 300;

        var band = heads();

        Assert.All(band, p => Assert.InRange(p.Y, 500 + 140, 500 + 460));
        Assert.Contains(band, p => p.Y < 500 + 200);
        Assert.Contains(band, p => p.Y > 500 + 400);
    }

    [Fact]
    public void ACloseLook_HasThemAllAroundTheFocus_AndThoseStreamingAwayStartAgain()
    {
        var (particles, heads) = Stream(50, () => (new Vector2(1000, -500), 500));
        Assert.All(heads(), p =>
        {
            Assert.InRange(p.X, 500, 1501);
            Assert.InRange(p.Z, -1000, 0);
        });

        for (int step = 0; step < 20; step++) particles.Step(0.5);

        Assert.All(heads(), p => Assert.InRange(p.X, 0, 2000));
        // From afar (or with no focus) the whole field.
        particles.Focus = null;
        for (int step = 0; step < 20; step++) particles.Step(0.5);
        Assert.Contains(heads(), p => Math.Abs(p.X) > 2000);
    }
}
