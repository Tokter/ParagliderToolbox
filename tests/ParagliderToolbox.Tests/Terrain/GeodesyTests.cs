using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Tests.Terrain;

public class GeodesyTests
{
    [Fact]
    public void TransverseMercator_MeasuresTheMeridianArcToTheMillimeter()
    {
        // WGS84's meridian arc from the equator to 45°.
        var projection = new TransverseMercator(0);
        var (e, n) = projection.Project(new GeoPoint(45, 0));
        Assert.Equal(0, e, 6);
        Assert.Equal(4984944.378, n, 2);
    }

    [Theory]
    [InlineData(46.6863, 7.8632)]
    [InlineData(-33.9, 18.4)]
    [InlineData(64.1, -21.9)]
    public void TransverseMercator_RoundTripsWithinAHundredKilometers(double latitude, double longitude)
    {
        var projection = new TransverseMercator(longitude, latitude);
        foreach (var (dLat, dLon) in new[] { (0.0, 0.0), (0.4, 0.6), (-0.7, -0.9), (0.9, -0.3) })
        {
            var point = new GeoPoint(latitude + dLat, longitude + dLon);
            var (x, y) = projection.Project(point);
            var back = projection.Unproject(x, y);
            Assert.Equal(point.Latitude, back.Latitude, 9);
            Assert.Equal(point.Longitude, back.Longitude, 9);
        }
    }

    [Fact]
    public void Utm_PutsTheCentralMeridianAt500km()
    {
        var zone32 = TransverseMercator.Utm(32);
        var (e, n) = zone32.Project(new GeoPoint(47, 9));
        Assert.Equal(500000, e, 3);
        // 0.9996 × the meridian arc to 47° (5'207'247 m).
        Assert.InRange(n, 5_205_163, 5_205_166);
    }

    [Fact]
    public void SwissGrid_MatchesTheCornersOfSwisstoposTiles()
    {
        // The corners of swissalti3d_2019_2632-1169 as swisstopo's catalog gives them.
        AssertNear(new GeoPoint(46.6714625, 7.8568172), SwissGrid.ToWgs84(2632000, 1169000));
        AssertNear(new GeoPoint(46.6714139, 7.8698851), SwissGrid.ToWgs84(2633000, 1169000));
        AssertNear(new GeoPoint(46.6804093, 7.8699568), SwissGrid.ToWgs84(2633000, 1170000));

        var (e, n) = SwissGrid.ToLv95(new GeoPoint(46.6804579, 7.8568868));
        Assert.Equal(2632000, e, 0);
        Assert.Equal(1170000, n, 0);
    }

    private static void AssertNear(GeoPoint expected, GeoPoint actual)
    {
        // A meter is about 0.00001°.
        Assert.InRange(actual.Latitude - expected.Latitude, -2e-5, 2e-5);
        Assert.InRange(actual.Longitude - expected.Longitude, -2e-5, 2e-5);
    }

    [Fact]
    public void LocalFrame_PutsTheOriginAtZero_AndNorthAtMinusZ()
    {
        var frame = new LocalFrame(new GeoPoint(46.6863, 7.8632));
        var (x0, z0) = frame.FromGeo(frame.Origin);
        Assert.Equal(0, x0, 6);
        Assert.Equal(0, z0, 6);

        // 0.009° north is about a kilometer.
        var (x, z) = frame.FromGeo(new GeoPoint(46.6953, 7.8632));
        Assert.Equal(0, x, 3);
        Assert.InRange(z, -1001, -999);
        var (east, _) = frame.FromGeo(new GeoPoint(46.6863, 7.8763));
        Assert.InRange(east, 990, 1010);
        var back = frame.ToGeo(x, z);
        Assert.Equal(46.6953, back.Latitude, 9);
    }

    [Theory]
    [InlineData("46.6863, 7.8632", 46.6863, 7.8632)]
    [InlineData("46.6863 7.8632", 46.6863, 7.8632)]
    [InlineData("-33.9;18.4", -33.9, 18.4)]
    [InlineData("46°41'10.7\"N 7°51'47.5\"E", 46.686306, 7.863194)]
    [InlineData("46° 41′ 10.7″ N, 7° 51′ 47.5″ E", 46.686306, 7.863194)]
    public void GeoPoint_ReadsDecimalAndSexagesimalDegrees(string text, double latitude, double longitude)
    {
        Assert.True(GeoPoint.TryParse(text, out var point));
        Assert.Equal(latitude, point.Latitude, 5);
        Assert.Equal(longitude, point.Longitude, 5);
    }

    [Theory]
    [InlineData("2'632'000, 1'169'000")]
    [InlineData("2632000 1169000")]
    [InlineData("632000 169000")]
    public void GeoPoint_ReadsSwissGridCoordinates(string text)
    {
        Assert.True(GeoPoint.TryParse(text, out var point));
        AssertNear(new GeoPoint(46.6714625, 7.8568172), point);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Interlaken")]
    [InlineData("95, 7")]
    [InlineData("46.6")]
    public void GeoPoint_RejectsWhatIsntAPosition(string text) => Assert.False(GeoPoint.TryParse(text, out _));
}
