using ParagliderToolbox.Terrain;

namespace ParagliderToolbox.Tests.Terrain;

public class TerrainLayoutTests
{
    [Fact]
    public void RoundsTheSizeToWholeTiles_AndKeepsTheResolution()
    {
        var layout = new TerrainLayout(new TerrainSettings { Size = 5000, Resolution = 2, TileSamples = 257 });
        Assert.Equal(512, layout.TileSize);
        Assert.Equal(10, layout.TilesAcross);
        Assert.Equal(5120, layout.Extent);
        Assert.Equal(2, layout.Spacing);
    }

    [Fact]
    public void AutomaticLevels_ReachASingleRoot_CutBackWhereTheAreaEnds()
    {
        var layout = new TerrainLayout(new TerrainSettings { Size = 5000, Resolution = 2, TileSamples = 257 });
        // 10 tiles across: levels of 10, 5, 3, 2 and 1 tiles.
        Assert.Equal(5, layout.LevelCount);
        var root = Assert.Single(layout.Roots);
        var rect = layout.Rect(root);
        Assert.Equal((-2560, -2560, 5120, 5120), (rect.X0, rect.Z0, rect.Width, rect.Depth));
        // 10 of 16 finest tiles' worth at a sixteenth of the resolution: 10 × 256 / 16 + 1 samples.
        Assert.Equal((161, 161), (rect.Columns, rect.Rows));
        Assert.Equal(32, rect.Spacing);

        var lastOfLevel2 = layout.Rect(new TileKey(2, 2, 0));
        Assert.Equal(1024, lastOfLevel2.Width);
        Assert.Equal(2048, lastOfLevel2.Depth);
        Assert.Equal(129, lastOfLevel2.Columns);
        Assert.Equal(257, lastOfLevel2.Rows);
        Assert.Equal(10 * 10 + 5 * 5 + 3 * 3 + 2 * 2 + 1, layout.Tiles.Count);
    }

    [Fact]
    public void Children_CoverTheirParentExactly()
    {
        var layout = new TerrainLayout(new TerrainSettings { Size = 7000, Resolution = 4, TileSamples = 65 });
        foreach (var key in layout.Tiles.Where(layout.IsSplit))
        {
            var parent = layout.Rect(key);
            var children = layout.Children(key).Select(layout.Rect).ToList();
            Assert.Equal(parent.Width * parent.Depth, children.Sum(c => c.Width * c.Depth), 6);
            Assert.All(children, c =>
            {
                Assert.InRange(c.X0, parent.X0, parent.X0 + parent.Width);
                Assert.InRange(c.Z0 + c.Depth, parent.Z0, parent.Z0 + parent.Depth + 1e-9);
                Assert.Equal(parent.Spacing / 2, c.Spacing);
            });
        }
    }

    [Fact]
    public void LimitedLevels_KeepSeveralRoots()
    {
        var layout = new TerrainLayout(new TerrainSettings { Size = 8192, Resolution = 4, TileSamples = 129, LodLevels = 2 });
        Assert.Equal(2, layout.LevelCount);
        // 16 finest tiles across, so 8 × 8 tiles of level 1.
        Assert.Equal(64, layout.Roots.Count);
    }

    [Fact]
    public void DetailSize_KeepsTheFinestLevelNearTheCenter_AndTheLeavesCoverEverything()
    {
        var full = new TerrainLayout(new TerrainSettings { Size = 40960, Resolution = 4, TileSamples = 129 });
        var detail = new TerrainLayout(new TerrainSettings { Size = 40960, Resolution = 4, TileSamples = 129, DetailSize = 2048 });

        Assert.True(detail.SampleCount < full.SampleCount / 20);
        var finest = detail.Tiles.Where(k => k.Level == 0).Select(detail.Rect).ToList();
        Assert.NotEmpty(finest);
        Assert.All(finest, r => Assert.True(Math.Abs(r.CenterX) < 2048 && Math.Abs(r.CenterZ) < 2048));

        double leafArea = detail.Tiles.Where(k => !detail.IsSplit(k)).Select(detail.Rect).Sum(r => r.Width * r.Depth);
        Assert.Equal(detail.Extent * detail.Extent, leafArea, 3);
        // A split tile has all its children (four, or fewer where the area ends): together they cover it.
        Assert.All(detail.Tiles.Where(detail.IsSplit), k =>
        {
            var parent = detail.Rect(k);
            Assert.Equal(parent.Width * parent.Depth, detail.Children(k).Select(detail.Rect).Sum(c => c.Width * c.Depth), 3);
        });
    }

    [Fact]
    public void TextureSize_FollowsTheTile_CutBackWhereTheAreaEnds()
    {
        var layout = new TerrainLayout(new TerrainSettings { Size = 3000, Resolution = 4, TileSamples = 129, TextureSize = 500 });
        Assert.Equal(512, layout.TextureSize);
        Assert.Equal((512, 512), layout.TextureSizeOf(new TileKey(0, 0, 0)));
        // 6 tiles across; level 2 has a tile over the last two of them.
        Assert.Equal((256, 512), layout.TextureSizeOf(new TileKey(2, 1, 0)));
    }
}
