using System.Numerics;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Convection;
using ParagliderToolbox.Airflow.Simulation;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Tests.Airflow;

public class ConvectionTests
{
    private static readonly Sounding s_summer = SoundingPresets.Find("summer-classic").Sounding;

    [Fact]
    public void ACloud_ShadesTheGroundUnderIt_AlongTheSun()
    {
        // A cloud layer over the west half at 1500–1700 m, the sun from the south-west and high: the shadow falls on the
        // ground north-east of the cloud, which loses its direct sunlight's heat within a few minutes.
        var terrain = HeightField.Sample(-1500, -1500, 25, 121, 121, (_, _) => 500f);
        var grid = new AirflowGrid(terrain, 100, 50, 2000);
        int columns = grid.Nx * grid.Nz;
        var heat = Enumerable.Repeat(250f, columns).ToArray();
        var vapor = new float[grid.CellCount];
        for (int c = 0; c < grid.CellCount; c++)
        {
            int j = c % grid.Ny, i = c / grid.Ny % grid.Nx;
            double y = grid.LevelHeight(j);
            bool cloud = i < grid.Nx / 2 && y > 1500 && y < 1700;
            vapor[c] = (float)(cloud ? 1.5 * Thermodynamics.SaturationMixingRatio(s_summer.TemperatureAt(y), s_summer.PressureAt(y)) : s_summer.MixingRatioAt(y) * 0.5);
        }
        var sun = Vector3.Normalize(new Vector3(-0.3f, 0.8f, 0.3f));
        using var solver = new AirflowSolver(grid, new AirflowConditions
        {
            Sounding = s_summer, SensibleHeat = heat, ShadedHeat = Enumerable.Repeat(40f, columns).ToArray(), SunDirection = sun, InitialVapor = vapor,
        });
        while (solver.Time < 600) solver.Step(4);
        var cells = solver.Snapshot();
        float At(double x, double z) => cells.SurfaceHeat[(int)((z - grid.Z0) / grid.CellSize) * grid.Nx + (int)((x - grid.X0) / grid.CellSize)];
        // The cloud (x < 0) about 1100 m up throws its shadow 400 m away from the sun: on the ground to x = 400.
        Assert.InRange(At(200, 0), 30, 120);
        Assert.InRange(At(1000, 0), 220, 260);
        Assert.InRange(At(-1400, 0), 220, 260);
    }

    // Flat ground at 500 m, 3 km square, 50 m cells.
    private static AirflowGrid FlatGrid() => new(HeightField.Sample(-1500, -1500, 12.5, 241, 241, (_, _) => 500f), 50, 25, 1500);

    // A frame of made-up flow: a 2 m/s west wind sinking 0.2 m/s; warm thermals (300 m wide, 2.5 m/s, +0.3 K, up to
    // 1000 m above the ground) born at the given times and drifting east with the wind from x = −800; one feeds a cumulus
    // at 1500–1700 m over it from 300 to 660 s; and air forced up a ridge at (800, −800), cooler than around it.
    private static AirflowCells Frame(AirflowGrid grid, double time, (double Born, double Dies)[] thermals)
    {
        var cells = new AirflowCells(grid);
        for (int k = 0; k < grid.Nz; k++)
        {
            for (int i = 0; i < grid.Nx; i++)
            {
                var (x, _, z) = grid.CellCenter(i, 0, k);
                for (int j = grid.FirstLevel[k * grid.Nx + i]; j < grid.Ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    double above = grid.LevelHeight(j) - 500;
                    cells.X[c] = 2;
                    cells.Y[c] = -0.2f;
                    foreach (var (born, dies) in thermals)
                    {
                        if (time < born || time > dies) continue;
                        double cx = -800 + 2 * (time - born);
                        bool inside = (x - cx) * (x - cx) + z * z < 150 * 150;
                        if (inside && above < 1000)
                        {
                            cells.Y[c] = 2.5f;
                            cells.Temperature[c] = 0.3f;
                        }
                    }
                    // The first one's cumulus drifts on with the wind after it died.
                    double cloudX = -800 + 2 * (time - thermals[0].Born);
                    bool cloudy = time >= 300 && time <= 660 && (x - cloudX) * (x - cloudX) + z * z < 200 * 200;
                    if (cloudy && above >= 1000 && above <= 1200) cells.CloudWater[c] = 3e-4f;
                    if ((x - 800) * (x - 800) + (z + 800) * (z + 800) < 150 * 150 && above < 600)
                    {
                        cells.Y[c] = 1.5f;
                        cells.Temperature[c] = -0.2f;
                    }
                }
            }
        }
        return cells;
    }

    [Fact]
    public void TheCensus_FollowsWarmThermalsAndTheirCumulus_NotAirForcedUpARidge()
    {
        var grid = FlatGrid();
        var census = new ConvectionCensus(grid, s_summer);
        (double, double)[] thermals = [(60, 570), (630, 840)];
        for (double time = 0; time <= 900; time += 30) census.Add(time, Frame(grid, time, thermals));
        Assert.Equal(31, census.Frames);

        var climate = census.Climate();
        Assert.Equal((0.0, 900.0, 30.0), (climate.Start, climate.End, climate.Interval));
        Assert.Equal(2, climate.Thermals.Count);
        var first = climate.Thermals[0];
        Assert.False(first.Cut);
        Assert.Equal(60, first.Start);
        Assert.Equal(570 - 60 + 30, first.Duration, 3);
        Assert.Equal(-800, first.X, 30.0);
        Assert.Equal(0, first.Z, 30.0);
        Assert.Equal(500, first.Ground, 1);
        Assert.InRange(first.Bottom, 0, 30);
        Assert.Equal(1500, first.Top, 30.0);
        Assert.Equal(2.5, first.Climb, 2);
        Assert.InRange(first.Diameter, 250, 350);
        Assert.Equal(2, first.DriftX, 1);
        Assert.Equal(0, first.LeanX, 2);

        // Its cumulus, from 300 to 660 s at 1500–1700 m, fed by it.
        var cloud = Assert.Single(climate.Clouds);
        Assert.Equal(0, first.CloudId);
        Assert.Equal(0, cloud.ThermalId);
        Assert.Equal(300, cloud.Start);
        Assert.Equal(660 - 300 + 30, cloud.Duration, 3);
        Assert.Equal(1500, cloud.Base, 30.0);
        Assert.Equal(1700, cloud.Top, 30.0);
        Assert.InRange(cloud.Diameter, 330, 470);
        Assert.True(cloud.Water > 0);
        // Its shape: a disc 400 m across of 50 m columns, cloudy all its life, 200 m deep; it drifted with the wind.
        var shape = Assert.IsType<CloudShape>(cloud.Shape);
        Assert.Equal(50, shape.CellSize);
        var cloudy = Enumerable.Range(0, shape.Columns * shape.Rows).Where(n => shape.Water[n] > 0).ToList();
        Assert.InRange(cloudy.Count, 45, 65);
        // Its columns but the edge's (the disc drifts across the grid) are cloudy all its life.
        Assert.True(cloudy.Count(n => shape.Appear[n] < 20 && shape.Vanish[n] > 235) >= 40);
        Assert.All(cloudy, n => Assert.Equal(0, shape.Bottom[n]));
        Assert.All(cloudy, n => Assert.InRange(shape.Top[n], (byte)18, (byte)25));
        Assert.Equal(-200, shape.West, 30.0);
        Assert.Equal(2, cloud.DriftX, 1);
        Assert.Equal(-1, climate.Thermals[1].CloudId);

        // Both rose from the same place: a site, two an hour of 15 minutes watched … eight an hour.
        var site = Assert.Single(climate.Sites);
        Assert.Equal(-800, site.X, 50.0);
        Assert.Equal(2, site.Count);
        Assert.Equal(8, site.Rate, 3);
        Assert.Equal(0.5, site.CloudShare, 3);
        Assert.Equal(8, climate.ThermalRate, 3);
        Assert.Equal(0.5, climate.CloudShare, 3);
    }

    [Fact]
    public void TheBaseFlow_LeavesTheThermalsOut_AndKeepsTheRidgeLift()
    {
        var grid = FlatGrid();
        var census = new ConvectionCensus(grid, s_summer);
        (double, double)[] thermals = [(60, 570), (630, 840)];
        var mean = new AirflowCells(grid);
        int frames = 0;
        for (double time = 0; time <= 900; time += 30)
        {
            var frame = Frame(grid, time, thermals);
            census.Add(time, frame);
            for (int c = 0; c < grid.CellCount; c++) mean.Y[c] += frame.Y[c];
            frames++;
        }
        for (int c = 0; c < grid.CellCount; c++) mean.Y[c] /= frames;
        var (x, y, _) = census.BaseWind(mean)!.Value;

        int Cell(double px, double above, double pz) => grid.Cell((int)((px - grid.X0) / 50), (int)((500 + above - grid.Y0) / 25), (int)((pz - grid.Z0) / 50));
        // Where the thermals passed, the mean rises; the base flow sinks as the air between them does.
        int path = Cell(-300, 300, 0);
        Assert.True(mean.Y[path] > 0, $"mean {mean.Y[path]:0.00}");
        Assert.Equal(-0.2, y[path], 2);
        Assert.Equal(-0.2, y[Cell(1200, 300, 1200)], 2);
        // Air forced up the ridge is the base flow's own.
        Assert.Equal(1.5, y[Cell(800, 300, -800)], 2);
        Assert.Equal(2, x[path], 2);
    }

    [Fact]
    public void AField_KeepsItsBaseFlowAndConvection_ThroughItsFile()
    {
        var field = new AirflowField(4, 3, 2, 50, 25, -100, 500, -50)
        {
            BaseWindX = Enumerable.Range(0, 24).Select(i => i * 0.1f).ToArray(),
            BaseWindY = Enumerable.Repeat(-0.3f, 24).ToArray(),
            BaseWindZ = new float[24],
            Convection = new ConvectionClimate
            {
                Start = 1800, End = 5400, Interval = 30, Area = 1e8,
                Thermals = [new ThermalLife(0, 1830, 420, -400, 200, 600, 12, 2100, 2.1f, 4.5f, 320, 0.2f, -0.05f, 2, 0.1f, 0, false)],
                Clouds =
                [
                    new CloudLife(0, 2100, 600, -300, 150, 2200, 2600, 500, 120, 240, 0, false)
                    {
                        Shape = new CloudShape(2, 1, 50, -50, -25, [0, 40], [255, 200], [0, 3], [30, 25], [60, 9]), DriftX = 2.5f,
                    },
                ],
                Sites = [new ThermalSite(-400, 200, 600, 6, 420, 2.1f, 4.5f, 320, 2100, 0.5f, 0.2f, -0.05f, 6)],
            },
        };
        Assert.True(field.HasBaseFlow);
        Assert.Same(field.BaseWindY, field.BaseFlow().WindY);
        Assert.Same(field.Turbulence, field.BaseFlow().Turbulence);
        Assert.Equal(-0.3f, field.BaseWindAt(-25, 540, -25).Y, 3);

        var copy = AirflowField.FromBytes(field.ToBytes());
        Assert.True(copy.HasBaseFlow);
        Assert.Equal(field.BaseWindX, copy.BaseWindX!, (a, b) => Math.Abs(a - b) < 0.006f);
        Assert.Equal(field.Convection.Thermals, copy.Convection!.Thermals);
        Assert.Equal(field.Convection.Clouds.Select(c => c with { Shape = null }), copy.Convection.Clouds.Select(c => c with { Shape = null }));
        var (shape, read) = (field.Convection.Clouds[0].Shape!, copy.Convection.Clouds[0].Shape!);
        Assert.Equal((shape.Columns, shape.Rows, shape.CellSize, shape.West, shape.North), (read.Columns, read.Rows, read.CellSize, read.West, read.North));
        Assert.Equal([shape.Appear, shape.Vanish, shape.Bottom, shape.Top, shape.Water], new[] { read.Appear, read.Vanish, read.Bottom, read.Top, read.Water });
        Assert.Equal(2.5f, copy.Convection.Clouds[0].DriftX);
        Assert.Equal(field.Convection.Sites, copy.Convection.Sites);
        Assert.Equal((1800.0, 5400.0, 30.0), (copy.Convection.Start, copy.Convection.End, copy.Convection.Interval));
        // A field without them reads as one.
        var plain = AirflowField.FromBytes(new AirflowField(2, 2, 2, 50, 25, 0, 0, 0).ToBytes());
        Assert.False(plain.HasBaseFlow);
        Assert.Null(plain.Convection);
        Assert.Same(plain, plain.BaseFlow());
    }
}
