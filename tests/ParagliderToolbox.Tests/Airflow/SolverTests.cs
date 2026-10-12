using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Simulation;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Tests.Airflow;

public class SolverTests
{
    // The standard atmosphere's 6.5 K per km: mildly stable.
    private static readonly Sounding s_standard = new([new(0, 15, 5), new(11000, -56.5, -70)], 1013.25);

    // Neutral (dry adiabatic) up to 1500 m, a sharp inversion above it.
    private static readonly Sounding s_capped = new([new(0, 20, 5), new(1500, 5.3, -2), new(1600, 10.3, -8), new(5000, -10, -30)], 1013.25);

    private static AirflowSolver Solver(Func<double, double, float> ground, double size, double windSpeed, double windDirection = 270,
        Sounding? sounding = null, Func<AirflowGrid, float[]>? heat = null, double clearance = 1500)
    {
        const double cell = 100;
        int samples = (int)(size / (cell / 4)) + 1;
        var terrain = HeightField.Sample(-size / 2, -size / 2, cell / 4, samples, samples, ground);
        var grid = new AirflowGrid(terrain, cell, 50, clearance);
        return new AirflowSolver(grid, new AirflowConditions
        {
            Sounding = sounding ?? s_standard, WindDirection = windDirection, WindSpeed = windSpeed, SensibleHeat = heat?.Invoke(grid), Moisture = false,
        });
    }

    private static int CellAt(AirflowGrid grid, double x, double y, double z) => grid.Cell(
        Math.Clamp((int)((x - grid.X0) / grid.CellSize), 0, grid.Nx - 1),
        Math.Clamp((int)((y - grid.Y0) / grid.LayerHeight), 0, grid.Ny - 1),
        Math.Clamp((int)((z - grid.Z0) / grid.CellSize), 0, grid.Nz - 1));

    private static (float X, float Y, float Z) VelocityAt(AirflowSolver solver, double x, double y, double z)
    {
        int c = CellAt(solver.Grid, x, y, z);
        var (vx, vy, vz) = solver.CenterVelocity;
        return (vx[c], vy[c], vz[c]);
    }

    private static void Run(AirflowSolver solver, double seconds)
    {
        while (solver.Time < seconds) solver.Step(4);
    }

    [Fact]
    public void Grid_CutsTheTerrainIntoTheCells()
    {
        // A slope of 45° along X: the faces' open shares follow it within the cells.
        var terrain = HeightField.Sample(-500, -500, 25, 41, 41, (x, _) => (float)(1000 + x));
        var grid = new AirflowGrid(terrain, 100, 50, 500);
        int i = grid.Nx / 2, k = grid.Nz / 2;
        double ground = grid.GroundHeight[k * grid.Nx + i];
        Assert.InRange(ground, grid.X0 + (i + 0.5) * 100 + 1000 - 5, grid.X0 + (i + 0.5) * 100 + 1000 + 5);
        float total = 0;
        for (int j = 0; j < grid.Ny; j++) total += grid.Ground[grid.Cell(i, j, k)];
        Assert.Equal(1, total, 3);
        int level = (int)((ground - grid.Y0) / 50);
        Assert.InRange(grid.Air[grid.Cell(i, level, k)], 0.01f, 0.99f);
        Assert.Equal(1, grid.Air[grid.Cell(i, level + 3, k)]);
        Assert.Equal(0, grid.Air[grid.Cell(i, level - 3, k)]);
        Assert.Equal(0, grid.OpenY[grid.FaceY(i, grid.Ny, k)]);
    }

    [Fact]
    public void FlatGround_KeepsTheWind_AndConservesMass()
    {
        var solver = Solver((_, _) => 500, 3000, 5);
        Run(solver, 120);
        Assert.True(solver.Divergence() < 1e-4, $"divergence {solver.Divergence()}");
        var v = VelocityAt(solver, 0, 1300, 0);
        Assert.InRange(v.X, 4.4f, 5.2f);
        Assert.InRange(Math.Abs(v.Y), 0, 0.05f);
        Assert.InRange(Math.Abs(v.Z), 0, 0.1f);
        // Slower near the ground (friction and the logarithmic profile).
        Assert.True(VelocityAt(solver, 0, 530, 0).X < v.X * 0.75f);
        Assert.True(solver.PressureLevels >= 3);
    }

    [Fact]
    public void AGapInAWall_SpeedsTheWindUp()
    {
        // A wall to 2000 m across the wind with a 500 m wide gap at the ground.
        var solver = Solver((x, z) => Math.Abs(x) < 300 && Math.Abs(z) > 250 ? 2000f : 500f, 4000, 5, clearance: 500);
        Run(solver, 160);
        var gap = VelocityAt(solver, 0, 800, 0);
        var upstream = VelocityAt(solver, -1700, 800, 0);
        Assert.True(gap.X > upstream.X * 1.3f, $"gap {gap.X}, upstream {upstream.X}");
    }

    [Fact]
    public void ARidge_LiftsTheAirUpwind_AndSinksItInTheLee()
    {
        var solver = Solver((x, _) => (float)(500 + 600 * Math.Exp(-x * x / (2 * 400.0 * 400))), 4000, 6);
        Run(solver, 200);
        double Ground(double x) => 500 + 600 * Math.Exp(-x * x / (2 * 400.0 * 400));
        float windward = VelocityAt(solver, -450, Ground(-450) + 150, 0).Y;
        float lee = VelocityAt(solver, 450, Ground(450) + 150, 0).Y;
        Assert.True(windward > 0.5f, $"windward {windward}");
        Assert.True(lee < -0.3f, $"lee {lee}");
    }

    [Fact]
    public void AHotPatch_MakesAThermal_ThatTheInversionStops()
    {
        static float[] Heat(AirflowGrid grid)
        {
            var heat = new float[grid.Nx * grid.Nz];
            for (int k = 0; k < grid.Nz; k++)
            {
                for (int i = 0; i < grid.Nx; i++)
                {
                    double x = grid.X0 + (i + 0.5) * grid.CellSize, z = grid.Z0 + (k + 0.5) * grid.CellSize;
                    heat[k * grid.Nx + i] = x * x + z * z < 350 * 350 ? 400 : 20;
                }
            }
            return heat;
        }
        var solver = Solver((_, _) => 100, 3000, 0, sounding: s_capped, heat: Heat, clearance: 2200);
        // A wide, flat bubble of warm air lifts off slowly on this grid; in ten minutes or so it is a thermal.
        Run(solver, 900);
        float low = VelocityAt(solver, 0, 900, 0).Y;
        float high = VelocityAt(solver, 0, 2100, 0).Y;
        float aside = VelocityAt(solver, 1200, 900, 0).Y;
        Assert.True(low > 0.8f, $"thermal {low}");
        Assert.True(Math.Abs(high) < low * 0.3f, $"above the inversion {high}, thermal {low}");
        Assert.True(aside < low * 0.3f, $"aside {aside}");
        Assert.True(solver.Temperature[CellAt(solver.Grid, 0, 140, 0)] > 0.3f);
    }

    [Fact]
    public void TheLeeOfACliff_IsTurbulent()
    {
        // A plateau at 900 m ending in a 400 m cliff, the wind across it.
        var solver = Solver((x, _) => (float)(700 - 200 * Math.Tanh(x / 25)), 4000, 8, clearance: 1200);
        Run(solver, 200);
        double from = solver.Time;
        while (solver.Time < from + 300)
        {
            double before = solver.Time;
            solver.Step(4);
            solver.Accumulate(solver.Time - before);
        }
        var averages = solver.Averages();
        int lee = CellAt(solver.Grid, 400, 620, 0), plateau = CellAt(solver.Grid, -900, 1020, 0);
        Assert.True(averages.Turbulence[lee] > averages.Turbulence[plateau] * 1.5f, $"lee {averages.Turbulence[lee]}, plateau {averages.Turbulence[plateau]}");
        Assert.True(averages.X[lee] < averages.X[plateau] * 0.6f, $"lee wind {averages.X[lee]}, plateau {averages.X[plateau]}");
    }

    [Fact]
    public void ASimulation_MakesAField_ThatSurvivesItsFile()
    {
        var terrain = HeightField.Sample(-1500, -1500, 25, 121, 121, (x, z) => (float)(800 + 300 * Math.Exp(-(x * x + z * z) / (2 * 500.0 * 500))));
        var colors = Enumerable.Repeat(0xFF4A9A6Eu, terrain.Heights.Length).ToArray();
        var settings = new AirflowSettings
        {
            WindDirection = 300, WindSpeed = 4, Duration = 160, AveragingTime = 80, LocalTime = new DateTime(2026, 7, 15, 14, 0, 0),
            Sounding = SoundingPresets.Find("summer-classic").Sounding, SoundingName = "Summer, classic",
        };
        var progress = new List<AirflowProgress>();
        var field = new AirflowSimulation(terrain, colors, settings).Run(new SynchronousProgress(progress.Add));
        Assert.Equal(160, field.Info.SimulatedTime, 3);
        Assert.Equal(80, field.Info.AveragedTime, 3);
        Assert.InRange(field.Info.SunElevation, 50, 70);
        Assert.Equal("Done", progress[^1].Stage);
        Assert.All(field.LandCover, c => Assert.Equal((byte)LandCover.Meadow, c));
        Assert.True(field.SurfaceHeat.Average(h => h) > 100);
        // A summer afternoon's boundary layer reaches well above the hill.
        Assert.True(field.BoundaryLayerTop.Average(h => h) > 1800);

        // Still air below the ground, the wind above it.
        Assert.Equal(default, field.Sample(0, 900, 0));
        var above = field.Sample(-1000, 1800, 0);
        Assert.True(above.HorizontalSpeed > 1);

        var copy = AirflowField.FromBytes(field.ToBytes());
        Assert.Equal((field.Nx, field.Ny, field.Nz), (copy.Nx, copy.Ny, copy.Nz));
        Assert.Equal(field.Info with { Created = default }, copy.Info with { Created = default });
        Assert.Equal(field.Thermals.Count, copy.Thermals.Count);
        Assert.Equal(field.Triggers.Count, copy.Triggers.Count);
        for (int c = 0; c < field.WindX.Length; c += 97)
        {
            Assert.Equal(field.WindX[c], copy.WindX[c], 0.006f);
            Assert.Equal(field.WindY[c], copy.WindY[c], 0.006f);
            Assert.Equal(field.LiftChance[c], copy.LiftChance[c], 0.003f);
        }
        Assert.Equal(field.GroundHeight, copy.GroundHeight);
        Assert.Equal(field.LandCover, copy.LandCover);
        Assert.Throws<InvalidDataException>(() => AirflowField.FromBytes("nope, not a field"u8.ToArray()));
    }

    [Fact]
    public void ALake_CoolsTheAirOverIt_AndNoThermalRisesFromIt()
    {
        // Flat ground at 600 m: a lake in the west, sunny fields in the east; a calm July afternoon.
        var terrain = HeightField.Sample(-1500, -1500, 25, 121, 121, (_, _) => 600f);
        var colors = new uint[terrain.Heights.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = terrain.X0 + i % terrain.Columns * terrain.Spacing < 0 ? 0xFF8C7340u : 0xFF5A8AB4u;
        var settings = new AirflowSettings
        {
            WindSpeed = 0, Duration = 600, AveragingTime = 300, TopClearance = 2500, LocalTime = new DateTime(2026, 7, 15, 14, 0, 0),
            Sounding = SoundingPresets.Find("summer-classic").Sounding,
        };
        var simulation = new AirflowSimulation(terrain, colors, settings);
        var field = simulation.Run();

        var grid = simulation.Grid;
        int lowest = grid.FirstLevel[grid.Nz / 2 * grid.Nx + grid.Nx / 2];
        double lake = 0, land = 0;
        int n = 0, m = 0;
        for (int k = 0; k < grid.Nz; k++)
        {
            for (int i = 0; i < grid.Nx; i++)
            {
                int column = k * grid.Nx + i;
                // Away from the shore and the domain's edges.
                if (i < 5 || i >= grid.Nx - 5 || Math.Abs(i - grid.Nx / 2) < 4) continue;
                float warming = field.Temperature[field.Index(i, lowest, k)];
                if (i < grid.Nx / 2)
                {
                    Assert.True(simulation.ColumnWater[column] > 0.9f);
                    Assert.True(field.SurfaceHeat[column] < 0, $"lake heat {field.SurfaceHeat[column]}");
                    lake += warming;
                    n++;
                }
                else
                {
                    Assert.True(field.SurfaceHeat[column] > 100);
                    land += warming;
                    m++;
                }
            }
        }
        Assert.True(lake / n < land / m - 0.5, $"lake {lake / n:0.00} K, land {land / m:0.00} K");
        Assert.All(field.Thermals, t => Assert.True(t.X > -100, $"a thermal rises at {t.X:0}, {t.Z:0}"));
        // The cores start at the ground and rise.
        Assert.All(field.Thermals, t =>
        {
            Assert.True(t.Core.Count >= 2);
            Assert.True(t.Core[^1].Y > t.Core[0].Y);
            Assert.Equal(t.X, t.Core[0].X, 1);
        });
    }

    private sealed class SynchronousProgress(Action<AirflowProgress> report) : IProgress<AirflowProgress>
    {
        public void Report(AirflowProgress value) => report(value);
    }
}
