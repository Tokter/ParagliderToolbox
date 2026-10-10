# Terrain tile set format

The Terrain Generator exports a terrain as a **tile set**: a folder with a manifest (`terrain.json`) and, per tile, a
glTF mesh, a 16-bit heightmap and a JPEG texture. The tiles form a **quadtree of levels of detail**: a game draws a
coarse tile far away and its finer children close by. "Export terrain glTF" writes the most detailed tiles instead as
one `.glb` (each tile a node), for Blender or a small map.

The reference implementation is `ParagliderToolbox.Terrain` (C#, SkiaSharp only): `TerrainLayout` (the quadtree),
`TerrainBuilder` (sampling the sources), `TerrainMesh` (the meshes) and `TerrainExporter` (the files).

## Conventions

- Meters. **+X east, +Y up, +Z south** (north is −Z): the glTF frame, right-handed.
- The frame is a **transverse Mercator projection** of WGS84 whose central meridian runs through the origin
  (`origin` in the manifest: the terrain's center, at x = z = 0), with scale 1. Distances are true to 0.003% at 25 km
  from the origin; grid north is true north on the central meridian.
- **Y is the height above sea level** of the sources (swissALTI3D: LN02; Copernicus: EGM2008), not shifted: a launch
  at 1950 m is at y = 1950.
- The terrain is a square from −extent/2 to +extent/2 in x and z.

## The quadtree

Level 0 is the finest. A tile of level L has `tileSamples` samples per side (2^k + 1) spaced `spacing × 2^L`, so it is
`(tileSamples − 1) × spacing × 2^L` meters wide; its children are the 2 × 2 tiles of level L − 1 inside it. Tile
`(level, x, y)` counts columns from the west and rows from the north.

- The area is a whole number of level-0 tiles (`tilesAcross`). Where it ends, tiles of coarser levels are **cut back** to
  it: fewer samples (`samples` in the manifest), the same spacing, fewer than four children.
- A split tile always has all of its children, so a renderer draws either a tile or all its children, never a mix.
- With `detailSize` > 0, a tile is split only where its children reach into a square around the origin: `detailSize`
  for level 0, twice as large for each coarser level. Far from the center the leaves are coarse.
- `roots` are the coarsest tiles (one, unless the levels are limited).

### Choosing tiles (what the toolbox's preview does)

Start at the roots. Draw a tile unless its samples would be further apart on screen than a few pixels, then visit its
children instead:

```
projected = tile.spacing / distance(camera, tile bounds) × viewportHeight / fieldOfView
if projected > 6 px and tile.children are loaded: visit children   else: draw tile
```

The bounds are the tile's x/z rectangle and `minHeight`…`maxHeight`.

### Skirts

Neighboring tiles of different levels don't share all their edge vertices, so small cracks would open between them.
Every mesh has a **skirt**: a band hanging `skirt` meters straight down from each edge (4 × the level's spacing, at
least 2 m), facing outwards, with the edge's normals and texture coordinates. It hides the cracks. Engines that stitch
the edges themselves can drop it (the heightmaps have none).

## Files

```
terrain.json
ATTRIBUTION.txt
tiles/L{level}/{x}_{y}.glb
heightmaps/L{level}/{x}_{y}.png    (or .raw)
textures/L{level}/{x}_{y}.jpg
```

### terrain.json

```jsonc
{
  "format": "paraglider-toolbox-terrain", "version": 1, "generator": "Paraglider Toolbox",
  "origin": { "latitude": 46.6863, "longitude": 7.8632 },
  "frame": { "projection": "transverse Mercator on WGS84, central meridian through the origin, scale 1",
             "axes": "x east, y up, z south; meters; y is the height above sea level" },
  "extent": 5120, "spacing": 4, "tileSamples": 129, "tileSize": 512, "tilesAcross": 10, "levels": 5, "detailSize": 0,
  "shading": "smooth",                       // or "faceted" (low poly: vertex colors, no texture)
  "heights": { "min": 557.2, "max": 2210.4, "heightmap": "height = min + value / 65535 × (max − min)" },
  "texture": { "size": 512, "format": "jpeg", "content": "imagery" },     // null without textures
  "roots": ["L4_0_0"],
  "tiles": [
    { "key": "L4_0_0", "level": 4, "x": 0, "y": 0,
      "bounds": [x0, z0, x1, z1],             // x0/z0: west/north edge
      "samples": [161, 161], "spacing": 64,   // columns, rows
      "minHeight": 557.2, "maxHeight": 2210.4, "skirt": 256,
      "textureSize": [320, 320],
      "children": ["L3_0_0", "L3_1_0", "L3_0_1", "L3_1_1"],
      "mesh": "tiles/L4/0_0.glb", "heightmap": "heightmaps/L4/0_0.png", "texture": "textures/L4/0_0.jpg" },
    ...
  ],
  "sources": {
    "elevation": [ { "id": "swissalti3d", "name": "swissALTI3D (swisstopo)", "share": 0.58, "attribution": "© swisstopo", "license": "..." }, ... ],
    "imagery":   [ ... ]
  },
  "attribution": "© swisstopo\nProduced using Copernicus WorldDEM-30 ..."
}
```

### Meshes (.glb)

One node named after the tile (`L0_3_4`) with its translation at the tile's center (x, 0, z) and `extras`
(`level`, `x`, `y`); positions relative to it, heights absolute. `POSITION`, `NORMAL` (from the neighbors' samples
too, so lighting is continuous across edges), and either `TEXCOORD_0` with the embedded JPEG as base color (smooth) or
`COLOR_0` (faceted). Texture coordinates run 0 to 1 over the tile, (0, 0) at its north-west corner; the sampler
clamps. Triangles are counter-clockwise seen from above, the material rough and non-metallic.

### Heightmaps

`columns × rows` samples, row by row from the north-west corner, **the samples on both edges** (neighbors repeat their
shared edge), no skirt. One height range for the whole terrain (`heights` in the manifest):

- **PNG**: 16-bit grayscale (Unreal's landscape import, Godot's Terrain3D, most tools).
- **RAW**: 16-bit little-endian without a header (Unity's terrain import: set the resolution to the tile's samples,
  the terrain height to max − min and place it at min).

With the default 2^k + 1 samples, every level-0 tile is a valid Unity, Unreal or Godot heightmap.

### Textures

`textureSize` pixels, north up, each pixel covering an equal part of the tile (centers half a pixel inside the edges).
Every level has the same pixels per whole tile, so coarser levels are coarser.

## Sources

Each point of the terrain takes the finest data there is at it (the property editor can put a source first). Regional
sources come before global ones; among them a level takes the coarsest source that is still fine enough for its sample
spacing (it has the same detail there with far less to read: a 30 km tile would read part of every swissALTI3D file),
then the finer ones where it has no data:

| Source | Covers | Data | Credit (required) | License |
|---|---|---|---|---|
| swissALTI3D (swisstopo) | Switzerland, Liechtenstein | Lidar terrain model, bare ground, 0.5 m and 2 m | © swisstopo | Free, including commercial use |
| swissALTIRegio (swisstopo) | Switzerland and 100 km and more around it | Terrain model, bare ground, 10 m | Bundesamt für Landestopografie swisstopo; TINITALY/1.1 (INGV, doi:10.13127/tinitaly/1.1); DGM Österreich, geoland.at; DGM1, Bayerische Vermessungsverwaltung – www.geodaten.bayern.de; DGM1 Baden-Württemberg: LGL, www.lgl-bw.de; RGEAlti, Institut National de l'information géographique et forestière | Free, including commercial use |
| SWISSIMAGE (swisstopo) | Switzerland | Orthophotos, 10 cm (25 cm in the Alps) | © swisstopo | Free, including commercial use |
| Copernicus DEM GLO-30 | World | Surface model (with forests and buildings), 30 m | Produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved | Free, including commercial use; credit and liability disclaimer |
| Sentinel-2 cloudless 2016 (EOX) | World | Cloud-free satellite mosaic, 10 m | Sentinel-2 cloudless 2016 by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016 & 2017) | CC BY 4.0 |

Ship `ATTRIBUTION.txt` (or the manifest's `attribution`) with a game that uses the terrain.

Where a finer source's data ends (at the Swiss border, or swisstopo's 1 km tiles), its heights ease into the next
source's over 200 m: Copernicus is a surface model and stands 15–25 m above swissALTI3D's bare ground in forests. The
imagery changes from 10 cm to 10 m there.

The toolbox reads the sources' Cloud Optimized GeoTIFFs (and map tiles) with byte range requests (up to 16 at once,
over HTTP/2 where the server offers it), so only the parts a terrain needs are downloaded, and keeps them in `%LOCALAPPDATA%\ParagliderToolbox\TerrainCache`
(`PARAGLIDERTOOLBOX_TERRAIN_CACHE` overrides it; Tools > Clear terrain cache empties it). A terrain built once builds
again offline.

### Adding a source

Implement `IElevationSource` or `IImagerySource` (`FillAsync` fills the samples of a `SampleRequest` that are still
empty) and add it in `TerrainSources.CreateDefault`. For a dataset of Cloud Optimized GeoTIFFs, project the samples into
its coordinate system (`SampleRequest.Project` with an `ICoordinateSystem`), open the files with `CogFiles`, pick an
overview with `CogFiles.ChooseImage` and let `RasterFill` sample them (see `SwissAlti3DSource` and
`CopernicusDemSource`). Good candidates: Austria's 1 m ALS DTM (BEV, CC BY 4.0, COG in EPSG:3035), France's
LiDAR HD and BD ORTHO (IGN, open licence), Bavaria's DGM1 and DOP40, and the USGS 3DEP and NAIP (public domain).
