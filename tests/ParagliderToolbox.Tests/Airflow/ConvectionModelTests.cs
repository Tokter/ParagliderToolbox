using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Convection;

namespace ParagliderToolbox.Tests.Airflow;

public class ConvectionModelTests
{
    // Flat ground at 500 m, 6 km square in 100 m cells; the mean wind still, the base flow 2 m/s east sinking 0.3 m/s.
    internal static AirflowField Field(ConvectionClimate? climate, bool baseFlow = true)
    {
        var field = new AirflowField(60, 40, 60, 100, 50, -3000, 400, -3000);
        Array.Fill(field.GroundHeight, 500f);
        if (baseFlow)
        {
            int cells = field.WindX.Length;
            field.BaseWindX = Enumerable.Repeat(2f, cells).ToArray();
            field.BaseWindY = Enumerable.Repeat(-0.3f, cells).ToArray();
            field.BaseWindZ = new float[cells];
        }
        field.Convection = climate;
        return field;
    }

    // An hour watched: thermals rising here and there (the first and every fourth feeding the one cumulus), one there
    // from the start (cut), all 300 m wide, 1200 m high, climbing 2 m/s, leaning and drifting east.
    internal static ConvectionClimate Climate(int thermals = 24)
    {
        var random = new Random(5);
        var lives = new List<ThermalLife>();
        for (int i = 0; i < thermals; i++)
        {
            double start = i == 0 ? 0 : 60 + random.NextDouble() * 3000;
            lives.Add(new ThermalLife(i, start, 600, (float)(random.NextDouble() * 4000 - 2000), (float)(random.NextDouble() * 4000 - 2000), 500, 20, 1700,
                2, 4, 300, 0.2f, 0, 1, 0, i % 4 == 1 ? 0 : -1, Cut: i == 0));
        }
        return new ConvectionClimate
        {
            Start = 0, End = 3600, Interval = 30, Area = 6000.0 * 6000,
            Thermals = lives,
            // The cumulus: its middle column all its life, the eight around it from 30% to 80% of it.
            Clouds =
            [
                new CloudLife(0, 900, 480, 0, 0, 1600, 2000, 500, 20, 200, 1, false)
                {
                    Shape = new CloudShape(3, 3, 100, -150, -150, [77, 77, 77, 77, 0, 77, 77, 77, 77], [204, 204, 204, 204, 255, 204, 204, 204, 204],
                        new byte[9], [30, 30, 30, 30, 40, 30, 30, 30, 30], [20, 20, 20, 20, 60, 20, 20, 20, 20]),
                    DriftX = 2,
                },
            ],
            Sites = [],
            ThermalCycle = [0.9f, 1, 1.05f, 1.1f, 1.1f, 1.05f, 1, 0.95f, 0.9f, 0.85f],
            CloudCycle = [0.1f, 0.3f, 0.6f, 0.9f, 1, 0.9f, 0.7f, 0.5f, 0.3f, 0.2f],
        };
    }

    [Fact]
    public void TheModel_RaisesThermalsAtTheRunsRate_AndKeepsAsManyAboutAsTheRunHad()
    {
        var climate = Climate();
        var model = new ConvectionModel(Field(climate), seed: 1, warmUp: 0);
        Assert.Equal(23, model.ThermalRate, 3);
        // The one the census cut lived longer than it saw: 24 thermals of 10 min (one seen whole would be 23 × 10 min an
        // hour, about 3.8 at a time; the run had 4.0 about).
        Assert.True(model.LifeStretch > 1);
        double about = 0;
        int samples = 0;
        for (int minute = 0; minute < 6000; minute++)
        {
            model.Advance(60);
            if (minute < 60) continue;
            about += model.Thermals.Count;
            samples++;
        }
        Assert.InRange(model.Born / 100.0, 23 * 0.85, 23 * 1.15);
        double expected = climate.Thermals.Sum(t => t.Duration) / (climate.ObservedTime + climate.Interval);
        // Those that drift off the terrain are gone sooner.
        Assert.InRange(about / samples, expected * 0.75, expected * 1.1);
    }

    [Fact]
    public void AThermal_LiftsMostInItsCoreHalfwayUp_AndNothingFarFromIt()
    {
        var model = new ConvectionModel(Field(Climate()), seed: 2);
        var thermal = model.Thermals.First(t => t.Age > 120 && t.Age < t.Duration - 120);
        float ground = thermal.Ground, middle = ground + (thermal.Top - ground) / 2;
        var axis = thermal.AxisAt(middle);
        Assert.True(thermal.Strength > 1.5f, $"strength {thermal.Strength:0.00}");
        // On its axis: its strength and its skirt's, above the base flow (other thermals may add a little).
        float lift = model.WindAt(axis.X, middle, axis.Z).Y + 0.3f;
        Assert.InRange(lift, thermal.Strength * (1 + model.Skirt) * 0.95f, thermal.Strength * (1 + model.Skirt) * 1.6f);
        Assert.Equal(thermal.Strength * (1 + model.Skirt) / 0.845f, thermal.LiftAt(axis.X, middle, axis.Z), 0.01f);
        // Half at its core's radius (and the skirt), weaker near the ground and gone above its top.
        Assert.True(thermal.LiftAt(axis.X + thermal.Diameter / 2, middle, axis.Z) < 0.7f * thermal.LiftAt(axis.X, middle, axis.Z));
        Assert.True(thermal.LiftAt(thermal.X0, ground + 25, thermal.Z0) < thermal.LiftAt(axis.X, middle, axis.Z));
        Assert.Equal(0, thermal.LiftAt(axis.X, thermal.Top + 10, axis.Z));
        Assert.Equal(0, thermal.LiftAt(axis.X + 8 * thermal.Diameter, middle, axis.Z));
        // It leans east with height.
        Assert.Equal(0.2f * (middle - ground), axis.X - thermal.X0, 1f);
    }

    [Fact]
    public void TheModel_IsTheSame_HoweverItsTimeIsCut()
    {
        var field = Field(Climate());
        var once = new ConvectionModel(field, seed: 7);
        var often = new ConvectionModel(field, seed: 7);
        once.Advance(3600);
        for (int i = 0; i < 1000; i++) often.Advance(3.6);
        Assert.Equal(once.Time, often.Time, 6);
        Assert.Equal(once.Born, often.Born);
        Assert.Equal(once.Thermals.Select(t => (t.RootX, t.Born)), often.Thermals.Select(t => (t.RootX, t.Born)));
        Assert.Equal(once.Clouds.Select(c => (c.X0, c.Share)), often.Clouds.Select(c => (c.X0, c.Share)));
        foreach (double x in new[] { -1500.0, 0, 1200 }) Assert.Equal(once.WindAt(x, 900, 300), often.WindAt(x, 900, 300));
        // Another seed, other thermals.
        var other = new ConvectionModel(field, seed: 8);
        other.Advance(3600);
        Assert.NotEqual(once.Thermals.Select(t => t.RootX), other.Thermals.Select(t => t.RootX));
    }

    [Fact]
    public void ACumulus_StartsAsAWispOverItsThermal_BuildsAndDecays()
    {
        var model = new ConvectionModel(Field(Climate()), seed: 3, warmUp: 0);
        Assert.InRange(model.CloudChance, 0.1, 1);
        LiveCloud? cloud = null;
        float first = 0, most = 0;
        int mostColumns = 0;
        List<CloudColumn> firstColumns = [];
        float firstX = 0;
        double end = 0;
        for (int step = 0; step < 20000 && (cloud is null || model.Time < end + 20); step++)
        {
            model.Advance(10);
            if (cloud is null && model.Clouds.Count > 0)
            {
                cloud = model.Clouds[0];
                first = cloud.Share;
                firstColumns = cloud.Columns().ToList();
                firstX = cloud.X0;
                end = cloud.Born + cloud.Duration;
                // At its thermal's axis where it reaches the cloud's base, drifting with the base flow there.
                var axis = cloud.Thermal.AxisAt(cloud.Base);
                Assert.True(MathF.Abs(cloud.X0 - axis.X) < 60 && MathF.Abs(cloud.Z0 - axis.Z) < 60, $"cloud at {cloud.X0:0}, thermal at {axis.X:0}");
                Assert.Equal(2, cloud.WindX, 3);
            }
            if (cloud != null && model.Clouds.Contains(cloud))
            {
                most = Math.Max(most, cloud.Share);
                mostColumns = Math.Max(mostColumns, cloud.Columns().Count());
            }
        }
        Assert.NotNull(cloud);
        Assert.Equal(1600, cloud.Base);
        Assert.InRange(first, 0, 0.2f);
        Assert.InRange(most, 0.8f, 1);
        Assert.True(cloud.DiameterNow <= cloud.Diameter);
        // As wide as the run's at most, and no more than 2.5 times its thermal's core.
        Assert.InRange(cloud.Diameter, 450, 550);
        // Its shape: the middle column alone at first (where it formed), all nine at its most; 400 m deep in the middle.
        var middle = Assert.Single(firstColumns);
        Assert.Equal(firstX, middle.X, 1f);
        Assert.Equal(1600, middle.Bottom);
        Assert.InRange(middle.Top, 1600 + 400 * 0.35f, 1600 + 400 * 0.5f);
        Assert.Equal(9, mostColumns);
        Assert.DoesNotContain(cloud, model.Clouds);
    }

    [Fact]
    public void TheThermals_CarryTheAirTheRunsCarried_AboveItsBaseFlow()
    {
        // The run's mean rises 0.15 m/s 300 m above the ground where its base flow sinks 0.3: its thermals carried 0.45.
        var field = Field(Climate(80));
        Array.Fill(field.WindY, 0.15f);
        var model = new ConvectionModel(field, seed: 4);
        Assert.InRange(model.Skirt, 0.01f, 1);
        double sum = 0;
        int n = 0;
        var random = new Random(9);
        for (int step = 0; step < 300; step++)
        {
            model.Advance(30);
            // Over the whole terrain, as the run's mean.
            for (int p = 0; p < 200; p++)
            {
                double x = random.NextDouble() * 6000 - 3000, z = random.NextDouble() * 6000 - 3000;
                sum += model.LiftAt(x, 800, z);
                n++;
            }
        }
        Assert.InRange(sum / n, 0.45 * 0.7, 0.45 * 1.3);
    }

    [Fact]
    public void WithoutAFollowedRun_TheModelHasNoThermals_AndItsWindIsTheMean()
    {
        var field = Field(null, baseFlow: false);
        Array.Fill(field.WindX, 3f);
        var model = new ConvectionModel(field);
        model.Advance(600);
        Assert.Empty(model.Thermals);
        Assert.Empty(model.Clouds);
        Assert.Equal(0, model.ThermalRate);
        Assert.Equal(field.WindAt(100, 900, 100), model.WindAt(100, 900, 100));
    }
}
