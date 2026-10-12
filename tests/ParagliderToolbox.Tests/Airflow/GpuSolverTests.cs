using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Gpu;
using ParagliderToolbox.Airflow.Simulation;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Tests.Airflow;

public class GpuSolverTests
{
    // Neutral up to 1500 m, an inversion above it, moist enough for a cloud over the hot patch.
    private static readonly Sounding s_capped = new([new(0, 20, 14), new(1500, 5.3, 4.5), new(1600, 10.3, -8), new(5000, -10, -30)], 1013.25);

    // A ridge across the wind, a hot patch upwind of it and a lake in the lee: every step of the solver has work.
    private static (AirflowGrid Grid, AirflowConditions Conditions) Case()
    {
        const double size = 2400, cell = 100;
        int samples = (int)(size / (cell / 4)) + 1;
        var terrain = HeightField.Sample(-size / 2, -size / 2, cell / 4, samples, samples,
            (x, _) => (float)(300 + 400 * Math.Exp(-x * x / (2 * 300.0 * 300))));
        var grid = new AirflowGrid(terrain, cell, 50, 1600);
        var heat = new float[grid.Nx * grid.Nz];
        var water = new float[grid.Nx * grid.Nz];
        var waterTemperature = new float[grid.Nx * grid.Nz];
        for (int k = 0; k < grid.Nz; k++)
        {
            for (int i = 0; i < grid.Nx; i++)
            {
                double x = grid.X0 + (i + 0.5) * cell, z = grid.Z0 + (k + 0.5) * cell;
                int column = k * grid.Nx + i;
                heat[column] = (x + 700) * (x + 700) + z * z < 300 * 300 ? 450 : 60;
                if (x > 600)
                {
                    water[column] = 1;
                    waterTemperature[column] = 280;
                }
            }
        }
        return (grid, new AirflowConditions
        {
            Sounding = s_capped, WindDirection = 260, WindSpeed = 4, SensibleHeat = heat, WaterShare = water, WaterTemperature = waterTemperature,
            ShadedHeat = heat.Select(h => h * 0.3f).ToArray(), SunDirection = System.Numerics.Vector3.Normalize(new(0.3f, 0.9f, 0.3f)), InflowNoise = 0.3,
            InflowNoiseDepth = 600,
        });
    }

    private static float MaxDifference(float[] a, float[] b, AirflowSolver reference)
    {
        float worst = 0;
        for (int c = 0; c < a.Length; c++)
        {
            if (reference.IsAir(c)) worst = Math.Max(worst, Math.Abs(a[c] - b[c]));
        }
        return worst;
    }

    private static void AssertMatches(Func<AirflowGrid, AirflowConditions, IAirflowSolver> create, int steps, float velocityTolerance)
    {
        var (grid, conditions) = Case();
        using var cpu = new AirflowSolver(grid, conditions);
        using var gpu = create(grid, conditions);
        for (int step = 0; step < steps; step++)
        {
            double a = cpu.Step(3), b = gpu.Step(3);
            Assert.Equal(a, b, 1e-3);
            cpu.Accumulate(a);
            gpu.Accumulate(b);
        }
        Assert.Equal(cpu.Time, gpu.Time, 1e-2);
        Assert.True(gpu.PressureIterations > 0 && gpu.PressureIterations < 80, $"{gpu.PressureIterations} pressure iterations");

        var expected = cpu.Snapshot();
        var actual = gpu.Snapshot();
        float wind = MaxDifference(expected.X, actual.X, cpu), climb = MaxDifference(expected.Y, actual.Y, cpu);
        Assert.True(wind < velocityTolerance && climb < velocityTolerance, $"velocity off by {wind} (X), {climb} (Y)");
        float warming = MaxDifference(expected.Temperature, actual.Temperature, cpu);
        Assert.True(warming < 0.05f, $"temperature off by {warming}");
        float vapor = MaxDifference(expected.Vapor, actual.Vapor, cpu);
        Assert.True(vapor < 2e-5f, $"vapor off by {vapor}");
        for (int column = 0; column < grid.Nx * grid.Nz; column++)
        {
            Assert.Equal(expected.SurfaceHeat[column], actual.SurfaceHeat[column], 1f);
            Assert.Equal(expected.WaterHeat[column], actual.WaterHeat[column], 1f);
        }

        var averageExpected = cpu.Averages();
        var averageActual = gpu.Averages();
        Assert.Equal(cpu.AveragedTime, gpu.AveragedTime, 1e-2);
        float mean = MaxDifference(averageExpected.X, averageActual.X, cpu);
        float turbulence = MaxDifference(averageExpected.Turbulence, averageActual.Turbulence, cpu);
        Assert.True(mean < velocityTolerance, $"mean wind off by {mean}");
        // A square root of the variance: near calm it magnifies small differences.
        Assert.True(turbulence < 2 * velocityTolerance, $"turbulence off by {turbulence}");
    }

    [Fact]
    public void GpuSolver_OnIlgpusCpuAccelerator_MatchesTheCpuSolver()
    {
        AssertMatches(AirflowGpu.CreateCpuSolver, 12, 0.01f);
    }

    [Fact]
    public void GpuSolver_OnTheGraphicsCard_MatchesTheCpuSolver()
    {
        // Without a card there is nothing to compare (the CPU accelerator's test covers the kernels).
        if (!AirflowGpu.IsAvailable) return;
        AssertMatches(AirflowGpu.CreateSolver, 40, 0.15f);
    }

    [Fact]
    public void AirflowSimulation_OnTheGpuSolver_FindsTheSameThermal()
    {
        if (!AirflowGpu.IsAvailable) return;
        var (grid, conditions) = Case();
        using var cpu = new AirflowSolver(grid, conditions);
        using var gpu = AirflowGpu.CreateSolver(grid, conditions);
        Assert.Equal(cpu.PressureLevels, gpu.PressureLevels);
        Assert.False(string.IsNullOrEmpty(gpu.Device));
        // Ten minutes of the hot patch: both lift the air over it about as strongly.
        while (cpu.Time < 600) cpu.Accumulate(cpu.Step(3));
        while (gpu.Time < 600) gpu.Accumulate(gpu.Step(3));
        var a = cpu.Averages();
        var b = gpu.Averages();
        float Strongest(AirflowCells cells)
        {
            float best = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                double x = grid.X0 + (c / grid.Ny % grid.Nx + 0.5) * grid.CellSize;
                if (x < -400 && cpu.IsAir(c)) best = Math.Max(best, cells.Y[c]);
            }
            return best;
        }
        float expected = Strongest(a), actual = Strongest(b);
        Assert.True(expected > 0.5f, $"no thermal on the CPU ({expected} m/s)");
        Assert.InRange(actual, expected * 0.75f, expected * 1.25f);
    }
}
