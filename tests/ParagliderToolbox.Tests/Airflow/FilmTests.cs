using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Tests.Airflow;

public class FilmTests
{
    // Two columns, three frames half a minute apart.
    internal static AirflowFilm SmallFilm()
    {
        var film = new AirflowFilm(2, 1, 100, -100, -50, [1000, 1100]);
        for (int n = 0; n < 3; n++)
        {
            int heights = AirflowFilm.LiftHeights.Length;
            var lift = new sbyte[heights * 2];
            // The first column climbs 1 m/s more at every kept height (and every frame), the second sinks 1 m/s.
            for (int h = 0; h < heights; h++)
            {
                lift[h * 2] = (sbyte)(10 * (h + n));
                lift[h * 2 + 1] = -10;
            }
            ushort cloud = (ushort)(n == 1 ? 2400 : 0);
            film.Add(new AirflowFilmFrame(30 + n * 30, lift, [cloud, 0], [(ushort)(cloud == 0 ? 0 : 2900), 0], [(byte)(n == 1 ? 50 : 0), 0],
                [255, (byte)(n == 1 ? 40 : 255)]));
        }
        return film;
    }

    [Fact]
    public void AFilmFrame_InterpolatesTheLiftBetweenItsHeights_AndTheFilmFindsTheNearestFrame()
    {
        var film = SmallFilm();
        Assert.Equal(3, film.Count);
        var frame = film[0];
        // 100, 200, 300, 500 m kept: 0, 1, 2, 3 m/s; 400 m is between the last two.
        Assert.Equal(0, frame.LiftAt(0, 50), 3);
        Assert.Equal(2, frame.LiftAt(0, 300), 3);
        Assert.Equal(2.5, frame.LiftAt(0, 400), 3);
        Assert.Equal(6, frame.LiftAt(0, 5000), 3);
        Assert.Equal(-1, frame.LiftAt(1, 400), 3);

        Assert.Same(film[0], film.At(-100));
        Assert.Same(film[0], film.At(44));
        Assert.Same(film[1], film.At(46));
        Assert.Same(film[2], film.At(1000));
        Assert.Null(new AirflowFilm(1, 1, 100, 0, 0, [0]).At(0));

        // Columns by position, the nearest at the edges.
        Assert.Equal(0, film.Column(-150, -50));
        Assert.Equal(1, film.Column(50, 40));
        Assert.Equal(1, film.Column(500, 500));
    }

    [Fact]
    public void ARun_KeepsAFilm_OfItsLiftCloudsAndSunshine()
    {
        var terrain = HeightField.Sample(-1500, -1500, 25, 121, 121, (x, z) => (float)(800 + 300 * Math.Exp(-(x * x + z * z) / (2 * 500.0 * 500))));
        var settings = new AirflowSettings
        {
            WindDirection = 270, WindSpeed = 4, Duration = 160, AveragingTime = 80, TopClearance = 1500, LocalTime = new DateTime(2026, 7, 15, 14, 0, 0),
            Sounding = SoundingPresets.Find("summer-classic").Sounding, CloudShadows = true,
        };
        using var simulation = new AirflowSimulation(terrain, null, settings) { FilmInterval = 40 };
        simulation.Run();

        var film = Assert.IsType<AirflowFilm>(simulation.Film);
        var grid = simulation.Grid;
        Assert.Equal((grid.Nx, grid.Nz, grid.CellSize), (film.Nx, film.Nz, film.CellSize));
        Assert.Equal(grid.GroundHeight, film.GroundHeight);
        // A frame after the first step, then every 40 s to the end.
        Assert.InRange(film.Count, 5, 6);
        for (int n = 1; n < film.Count; n++) Assert.True(film[n].Time > film[n - 1].Time);
        Assert.Equal(160, film[film.Count - 1].Time, 3);

        // The last frame's lift is the flow's at its end, 300 m above the ground, column by column.
        var last = film[film.Count - 1];
        var snapshot = simulation.Snapshot();
        double largest = 0;
        for (int column = 0; column < film.Nx * film.Nz; column += 7)
        {
            double x = film.X0 + (column % film.Nx + 0.5) * film.CellSize, z = film.Z0 + (column / film.Nx + 0.5) * film.CellSize;
            float expected = snapshot.Sample(x, film.GroundHeight[column] + 300, z).Wind.Y;
            Assert.Equal(expected, last.LiftAt(column, 300), 0.15f);
            largest = Math.Max(largest, Math.Abs(expected));
        }
        Assert.True(largest > 0.3, $"the hill lifts the wind ({largest:0.00} m/s)");
        // No clouds in under three minutes: no cloud, full sun.
        Assert.All(last.CloudBase, b => Assert.Equal(0, b));
        Assert.All(last.Sunshine, s => Assert.InRange(s, (byte)250, (byte)255));
    }
}
