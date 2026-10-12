using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Sun;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Tests.Airflow;

public class AtmosphereTests
{
    private static Sounding Preset(string id) => SoundingPresets.All.Single(p => p.Id == id).Sounding;


    [Fact]
    public void Parcels_StopUnderTheInversion_OrRiseInCloud_LessWhenTheyMixInTheirSurroundings()
    {
        var sounding = Preset("summer-classic");
        double ground = 600, theta = sounding.PotentialTemperatureAt(ground);
        // Dry air no warmer than the morning's barely rises; the afternoon's warm, moist air rises into cumulus.
        Assert.InRange(Thermodynamics.ParcelTop(sounding, ground, theta, 0.001), ground, ground + 300);
        double undiluted = Thermodynamics.ParcelTop(sounding, ground, theta + 6, 0.012);
        double mixing = Thermodynamics.ParcelTop(sounding, ground, theta + 6, 0.012, entrainment: 0.001);
        double dry = Thermodynamics.ParcelTop(sounding, ground, theta + 6, 0.001);
        Assert.True(undiluted > dry + 1000, $"undiluted {undiluted}, dry {dry}");
        Assert.InRange(mixing, dry, undiluted - 500);
    }
    [Fact]
    public void Soundings_InterpolateTheLevels_AndIntegrateThePressure()
    {
        var sounding = Preset("summer-classic");
        Assert.Equal(15 + 273.15, sounding.TemperatureAt(490), 3);
        Assert.Equal((17 + 16) / 2.0 + 273.15, sounding.TemperatureAt(850), 1);
        Assert.Equal(963e2, sounding.PressureAt(490), 0);
        // Half the air is below about 5.5 km.
        Assert.InRange(sounding.PressureAt(5500), 480e2, 520e2);
        // Stable above the inversion: the potential temperature rises.
        Assert.True(sounding.PotentialTemperatureAt(4200) > sounding.PotentialTemperatureAt(3700) + 1.5);
        Assert.InRange(sounding.MixingRatioAt(490), 0.0075, 0.0095);
    }

    [Fact]
    public void CloudBase_IsAbout122MetersPerDegreeOfDewPointSpread()
    {
        double height = Thermodynamics.CondensationHeight(25 + 273.15, 13 + 273.15);
        Assert.InRange(height, 1400, 1550);
        Assert.Equal(0, Thermodynamics.CondensationHeight(290, 290), 1);
    }

    [Fact]
    public void BoundaryLayer_GrowsWithTheHeat_AndAnInversionCapsIt()
    {
        var unstable = Preset("spring-unstable");
        var capped = Preset("summer-stable");
        var morning = BoundaryLayer.Grow(unstable, 490, 1e6);
        var afternoon = BoundaryLayer.Grow(unstable, 490, 6e6);
        Assert.True(afternoon.Top > morning.Top + 500);
        Assert.True(afternoon.PotentialTemperature > morning.PotentialTemperature);

        // The same day's heat under the subsidence inversion stays below about 1900 m.
        var stable = BoundaryLayer.Grow(capped, 490, 6e6);
        Assert.InRange(stable.Top, 1200, 1900);
        Assert.True(afternoon.Top > stable.Top + 400);

        // The moist sounding's cumulus are low; the dry one's thermals blue.
        // With a summer day's evaporation (2 kg/m²).
        var moist = BoundaryLayer.Grow(Preset("summer-thunderstorm"), 490, 5e6, 2);
        Assert.True(moist.HasClouds);
        Assert.True(moist.CloudBase < 2200);
        Assert.False(stable.HasClouds);
        Assert.Equal(0, BoundaryLayer.Grow(unstable, 490, 0).Depth, 6);
    }

    [Fact]
    public void ConvectiveVelocity_IsAFewMetersPerSecond_OnASummerDay() =>
        Assert.InRange(BoundaryLayer.ConvectiveVelocity(250, 2000, 300), 2, 3);

    [Fact]
    public void Sun_StandsInTheSouthAtSolarNoon_AsHighAsTheLatitudeAllows()
    {
        // Zurich at the June solstice: solar noon is about 11:27 UTC.
        var noon = SolarPosition.At(47.37, 8.54, new DateTime(2026, 6, 21, 11, 27, 0, DateTimeKind.Utc));
        Assert.InRange(noon.Elevation, 65.8, 66.4);
        Assert.InRange(noon.Azimuth, 178, 182);
        var morning = SolarPosition.At(47.37, 8.54, new DateTime(2026, 6, 21, 6, 0, 0, DateTimeKind.Utc));
        var evening = SolarPosition.At(47.37, 8.54, new DateTime(2026, 6, 21, 17, 0, 0, DateTimeKind.Utc));
        Assert.InRange(morning.Azimuth, 70, 110);
        Assert.InRange(evening.Azimuth, 250, 290);
        Assert.False(SolarPosition.At(47.37, 8.54, new DateTime(2026, 6, 21, 23, 0, 0, DateTimeKind.Utc)).IsUp);

        // The direction vector: south is +Z.
        var direction = noon.Direction;
        Assert.True(direction.Z > 0.3 && direction.Y > 0.85 && Math.Abs(direction.X) < 0.05);

        // Winter: low.
        var winter = SolarPosition.At(47.37, 8.54, new DateTime(2026, 12, 21, 11, 37, 0, DateTimeKind.Utc));
        Assert.InRange(winter.Elevation, 18.8, 19.6);
    }

    [Fact]
    public void ClearSky_IsStrongerHighUp_AndWeakWithALowSun()
    {
        var (high, _) = SolarPosition.ClearSky(60, 3000);
        var (low, diffuse) = SolarPosition.ClearSky(60, 0);
        Assert.InRange(low, 850, 1000);
        Assert.True(high > low);
        Assert.InRange(diffuse, 80, 160);
        Assert.True(SolarPosition.ClearSky(5, 0).Direct < low / 2);
        Assert.Equal((0.0, 0.0), SolarPosition.ClearSky(-3, 0));
    }

    [Theory]
    [InlineData(0xFF1F3A26u, 10, 1000, LandCover.Forest)]   // dark green
    [InlineData(0xFF4A9A6Eu, 10, 1000, LandCover.Meadow)]   // grass
    [InlineData(0xFF8C7340u, 1, 600, LandCover.Water)]      // turquoise lake, flat
    [InlineData(0xFFF0F0EEu, 25, 3200, LandCover.Snow)]     // snow
    [InlineData(0xFF7F8587u, 40, 2600, LandCover.Rock)]     // gray, steep
    [InlineData(0xFF7A848Cu, 2, 600, LandCover.Urban)]      // gray, flat
    [InlineData(0xFF5A8AB4u, 3, 500, LandCover.Field)]      // brown field
    public void AerialColors_AreClassified(uint rgba, double slope, double height, LandCover expected) =>
        Assert.Equal(expected, LandCovers.Classify(rgba, slope, height));

    [Fact]
    public void FlatBrightTurquoise_IsALake_ButNotWhereTheGroundVaries()
    {
        // Glacial silt makes Lake Brienz bright turquoise in places.
        const uint turquoise = 0xFFB4AB61u;
        Assert.Equal(LandCover.Water, LandCovers.Classify(turquoise, 0, 564, relief: 0));
        Assert.NotEqual(LandCover.Water, LandCovers.Classify(turquoise, 0, 564, relief: 2));
        // A flat green or gray field stays land.
        Assert.Equal(LandCover.Meadow, LandCovers.Classify(0xFF4A9A6Eu, 0, 600, relief: 0));
        Assert.Equal(LandCover.Urban, LandCovers.Classify(0xFF7A848Cu, 0, 600, relief: 0));
    }

    [Fact]
    public void Lakes_FollowTheSeason_AndAreColderHigherUp()
    {
        Assert.InRange(SurfaceHeating.WaterTemperature(7, 15, 500), 20.5, 21.5);
        Assert.InRange(SurfaceHeating.WaterTemperature(1, 15, 500), 4, 5);
        Assert.InRange(SurfaceHeating.WaterTemperature(10, 15, 500), 13, 14);
        Assert.True(SurfaceHeating.WaterTemperature(7, 15, 1800) < SurfaceHeating.WaterTemperature(7, 15, 500) - 7);
        Assert.Equal(0.5, SurfaceHeating.WaterTemperature(1, 15, 2500));
    }

    [Fact]
    public void ALake_CoolsWarmerAir_AndWarmsColderAir()
    {
        const double k = 273.15;
        // July: 21 °C water under 27 °C afternoon air.
        var (summer, evaporation) = SurfaceHeating.WaterFlux(21 + k, 27 + k, 0.009, 3, 95_000, 1.12);
        Assert.InRange(summer, -40, -10);
        Assert.True(evaporation > 0);
        // October: 14 °C water under a 6 °C cold pool.
        var (autumn, _) = SurfaceHeating.WaterFlux(14 + k, 6 + k, 0.005, 2, 96_000, 1.2);
        Assert.InRange(autumn, 15, 40);
        // The sun's heat goes into the water, not the air.
        var lake = HeightField.Sample(-200, -200, 20, 21, 21, (_, _) => 560f);
        var sun = SolarPosition.At(46.7, 7.9, new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc));
        var fluxes = SurfaceHeating.Fluxes(lake, Enumerable.Repeat(LandCover.Water, 21 * 21).ToArray(), sun);
        Assert.All(fluxes, f => Assert.Equal(0, f.Sensible));
        Assert.True(fluxes[0].Sunlight > 700);
    }

    [Fact]
    public void SunnySlopes_HeatMore_AndShadowedOnesLess()
    {
        // A ridge along X: its south face slopes towards +Z.
        var ridge = HeightField.Sample(-1000, -1000, 20, 101, 101, (x, z) => (float)(1000 + Math.Max(0, 400 - Math.Abs(z)) * 0.8));
        var sun = SolarPosition.At(46.7, 7.9, new DateTime(2026, 4, 20, 11, 0, 0, DateTimeKind.Utc));
        var fluxes = SurfaceHeating.Fluxes(ridge, null, sun);
        int Index(double x, double z) => (int)Math.Round((z + 1000) / 20) * 101 + (int)Math.Round((x + 1000) / 20);
        float south = fluxes[Index(0, 200)].Sensible, north = fluxes[Index(0, -200)].Sensible, flat = fluxes[Index(0, 800)].Sensible;
        Assert.True(south > flat + 20, $"south {south}, flat {flat}");
        Assert.True(north < flat - 30, $"north {north}, flat {flat}");

        // An evening sun from the west: the ground east of a wall lies in its shadow, 640 m long.
        var wall = HeightField.Sample(-1000, -1000, 20, 101, 101, (x, z) => Math.Abs(x) < 40 ? 1300f : 1000f);
        var evening = new SunPosition(270, 25);
        var shadows = SurfaceHeating.Shadows(wall, evening);
        Assert.True(shadows[Index(400, 0)]);
        Assert.False(shadows[Index(-400, 0)]);
        Assert.False(shadows[Index(900, 0)]);
    }

    [Fact]
    public void Presets_HaveDistinctIds_AndFindFallsBackToTheClassicDay()
    {
        Assert.Equal(SoundingPresets.All.Count, SoundingPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal("summer-classic", SoundingPresets.Find("nonsense").Id);
        Assert.All(SoundingPresets.All, p => Assert.True(p.Sounding.Levels.Count >= 8));
    }
}
