# Features format

The Feature Detector finds what stands and runs on a terrain, for a game to place its 3D models by: **trees and
bushes**, **roads and paths** as a network of center lines with their width, surface and kind, **buildings** with their
outline, height and roof, **boulders**, **areas** of forest, rock, scree, grassland, fields, water …, **lines**
(waterways, power lines and aerialways with their towers, railways, fences and walls, cliffs), **landmarks** (peaks,
paragliding takeoffs and landings, masts, huts, signposts …) and a **ground cover map**. It reads the terrain's aerial
images, its terrain model and, where there is one, a surface model (the top of trees and buildings), and
**OpenStreetMap**, which gives the roads, the buildings' outlines, the areas, lines and landmarks everywhere, so a
terrain in the United States (no surface model) gets its buildings too.

Every feature says where it was found: `"origin": "map"` (OpenStreetMap) or `"image"` (the images and the surface
model; the default where the field is absent). Map features carry their OpenStreetMap `class` (`"landuse=forest"`,
`"track"`, `"house"` …) and `tags` (name, species, voltage, hiking difficulty …) for a finer choice of model.

"Export features" writes a folder:

```
features.json            the features (this document)
landcover.png            the ground cover map: 8-bit gray, the value is the class number
landcover_colors.png     the same in colors, to look at
features.geojson         the features in longitude/latitude, for QGIS, geojson.io …
ATTRIBUTION.txt          the credits of the data the features were found in
```

The reference implementation is `ParagliderToolbox.Terrain.Features` (C#): `FeatureDetector` (the search),
`GroundCovers` (the pixel classes), `TreeFinder`, `RoadFinder`, `MapRoads` and `MapFeatures` (OpenStreetMap's tags to
features), `Polygons`, and `FeatureFiles` (these files; its `FromJson` reads `features.json` back into a `FeatureSet`).

## Conventions

The terrain's frame (see [TerrainFormat.md](TerrainFormat.md)): meters, **+X east, +Y up, +Z south**, a transverse
Mercator projection centered on `origin` (the terrain's center, x = z = 0). Heights (y) are above sea level, the
terrain model's (bare ground) at the point, from the finest data (0.5–1 m), not from the game's coarser terrain mesh:
**drop the models onto your terrain** (a ray down) rather than placing them at y.

2D points are `[x, z]`; 3D points `[x, y, z]`. Rings (outlines, footprints) are not closed (the last point isn't the
first again); outlines run **counterclockwise seen from above** (north up, east right), holes clockwise. In (x, z) the
shoelace sum Σ(xᵢ·zᵢ₊₁ − xᵢ₊₁·zᵢ) of an outline is negative (z points south).

## features.json

```jsonc
{
  "format": "paraglider-toolbox-features", "version": 1, "generator": "Paraglider Toolbox",
  "origin": { "latitude": 46.6863, "longitude": 7.8632 },
  "frame": { "projection": "...", "axes": "x east, y up, z south; meters; y is the height above sea level" },
  "area": { "x0": -500, "z0": -500, "width": 1000, "depth": 1000 },   // where was looked (x0/z0: west/north edge)
  "spacing": 0.5,              // the pixel size the images were read at (m)
  "surfaceShare": 1,           // the share of the area a surface model covered (0–1)
  "elapsed": 52.3,             // seconds
  "attribution": "© swisstopo",
  "notes": [ "..." ],          // what to know: no surface model, images too coarse …
  "trees":     [ { "x": 12.5, "y": 1104.2, "z": -37.1, "height": 24.3, "crownRadius": 3.4, "kind": "conifer" },
                 { ..., "kind": "broadleaf", "origin": "map", "tags": { "genus": "Quercus", "leaf_type": "broadleaved" } }, ... ],
  "roads":     [ { "kind": "track", "surface": "gravel", "width": 3, "length": 212.7, "start": 3, "end": -1,
                   "origin": "map", "class": "track", "tags": { "highway": "track", "tracktype": "grade2", "name": "..." },
                   "points": [ [x, y, z], ... ] },
                 { "kind": "road", ..., "structure": "bridge", ... }, ... ],
  "junctions": [ [x, y, z], ... ],
  "buildings": [ { "x": 37.5, "y": 1098.6, "z": -45, "length": 15.2, "width": 10.1, "angle": 87.5,
                   "height": 8.3, "eaves": 5.9, "roof": "pitched", "origin": "map", "class": "house",
                   "tags": { "building:levels": "2" }, "footprint": [ [x, z], ... ] }, ... ],
  "rocks":     [ { "x": 60.1, "y": 1120.4, "z": 59.8, "radius": 1.4, "height": 0.9 }, ... ],
  "areas":     [ { "kind": "forest", "size": 21530, "origin": "map", "class": "landuse=forest", "tags": { "leaf_type": "needleleaved" },
                   "outline": [ [x, z], ... ], "holes": [ [ [x, z], ... ], ... ] }, ... ],
  "lines":     [ { "kind": "powerLine", "width": 0, "height": 35, "length": 812.4, "class": "power=line",
                   "tags": { "voltage": "132000" }, "points": [ [x, y, z], ... ] }, ... ],
  "landmarks": [ { "x": 410.2, "y": 1960.1, "z": -220.7, "kind": "takeoff", "name": "Niederhorn", "elevation": 1950,
                   "class": "sport=free_flying", "tags": { "free_flying:site": "takeoff", ... } }, ... ],
  "landCover": { "x0": -500, "z0": -500, "spacing": 1, "columns": 1000, "rows": 1000,
                 "classes": [ "none", "tree", "shrub", "grass", "soil", "paved", "gravel", "rock", "building", "water", "snow", "shadow" ],
                 "file": "landcover.png" }
}
```

Names are camelCase; read unknown fields and values leniently (later versions may add some).

### Trees

| Field | |
|---|---|
| `x`, `y`, `z` | Where the trunk stands (the crown's top seen from above), y the ground there. |
| `height` | Above the ground (m): measured with a surface model, else estimated from the crown (`"estimated": true`). |
| `crownRadius` | The radius of a circle as large as the crown (m). |
| `kind` | `conifer` (a pointed crown: spruce, fir, larch, pine), `broadleaf` (rounded), `bush` (lower than the least tree height, 3 m by default, at least 1.2 m) or `unknown` (found in the image alone where the map's forest doesn't tell its `leaf_type`). |
| `estimated` | Present and true where no surface model measured the tree (and, for the map's trees, its `height` tag doesn't give it: then 10 m). |
| `origin`, `tags` | The map's single trees (`natural=tree`) and rows of trees (`natural=tree_row`, a tree every 8 m) are added where no tree was found within their crown (2.5 m at least), with their tags (`genus`, `species`, `leaf_type`, `height`, `diameter_crown` …). |

Scale a tree model to `height` and its crown to `crownRadius`, turn it at random. Where trees stand close (forests)
their crowns touch; under a closed canopy only the tallest trees are seen. Without a surface model, the trees found in
the images within an OpenStreetMap forest tagged `leaf_type=needleleaved` (or `broadleaved`) take that kind; crowns on
the map's roofs aren't trees.

### Roads and paths

A road runs between two **junctions** (indices into `junctions`; −1 for a dead end or where it leaves the area), so
the roads form a network, their end points at exactly the junction's position. At a junction three or more roads
meet, or (from the map) two roads of different kinds or tags meet end to end. A road that closes on itself (a loop)
starts and ends at the same junction.

The roads come from **OpenStreetMap** by default (`"origin": "map"`): its ways tagged `highway`, split where they share
a node and joined where two of the same tags meet end to end. Where OpenStreetMap can't be reached (and isn't cached),
they are traced in the aerial images (`"origin": "image"`); with "Roads from: Both", the images add the roads they see
more than 5 m from any mapped road (unmapped tracks and trails, as a network of their own).

| Field | |
|---|---|
| `points` | The center line, every point on the ground (y): from the map at most 5 m apart; traced, about 0.5 m apart where it curves. On a bridge or in a tunnel, y runs straight between the ends. |
| `width` | The width (m). From the map: its `width` tag, else 3.25 m per lane, else the usual width of its class (track 3 m, path 1 m, residential 5.5 m …). Traced: measured in the image (± about half a meter). |
| `surface` | `paved` (asphalt, concrete, paving stones), `gravel` (gravel, compacted), `dirt` (ground, grass, a worn trail). From the map: its `surface` tag, else its `tracktype` (grade 2–3 gravel, 4–5 dirt), else what the images show along it (where at least 40% of it shows a road surface), else the class's usual surface. |
| `kind` | From the map's class: motorways to secondary roads `mainRoad`; tertiary, residential, service roads … `road` (or `track` where unpaved); `track`; paths, footways, cycleways, bridleways, steps `path`. Traced, by surface and width: `mainRoad` (paved, 6 m or more), `road` (paved, 3–6 m), `track` (unpaved, 3 m or more), `path` (narrower). |
| `length` | Along the center line (m, horizontally). |
| `start`, `end` | The junctions at the first and last point, −1 for none. |
| `origin` | `map` or `image`. |
| `structure` | `bridge` or `tunnel` (also a passage through a building); absent on the ground. Draw tunnels underground (or not at all) and bridges at their own heights. |
| `class` | The map's class (OpenStreetMap's `highway`: `track`, `path`, `residential`, `secondary` …), for a finer choice of road model. |
| `tags` | The map's tags a game may want: `name`, `ref`, `surface`, `tracktype`, `smoothness`, `width`, `lanes`, `oneway`, `bridge`, `tunnel`, `layer`, `sac_scale` (hiking difficulty), `trail_visibility`, `access`, `motor_vehicle`, `foot`, `bicycle`, `mtb:scale`, `incline`, `service`. |

Make road meshes by sweeping a cross section along the center line (projected onto your terrain), widened by a meter
or two of verge. Traced roads miss faint trails across meadows and wobble where a road meets a yard or parking.

### Buildings

From OpenStreetMap (`"origin": "map"`): its buildings' outlines (closed ways and multipolygons, with courtyards as
holes), each belonging to the area by its center. Their heights are measured where a surface model is (Switzerland:
swissSURFACE3D; `"measured"` absent or true): the 95th and 15th percentiles of what stands on the ground within the
outline. Elsewhere (`"measured": false`) they come from the tags: `height`, `roof:height`, `building:levels` (3 m a
floor and a 3 m roof on top), `roof:shape`; else from the class's usual height and roof (house 7 m pitched, garage 3 m
flat, apartments 13 m, industrial 9 m flat, commercial 10 m flat, church 14 m …).

Where a surface model is, it also finds buildings the map lacks (`"origin": "image"`): a part of what stands higher
than 2.5 m above the ground, is smooth (a roof, not a crown) and not green, at least 12 m², filling at least 45% of
the smallest rectangle around it and made of planes (the median bend of its top, |h(x ± 0.5 m) + h(z ± 0.5 m) − 4h|,
at most 0.2 m: dark red and purple trees aren't green and pass as smooth, but bend everywhere). With the map, it must
also lie mostly outside the mapped buildings (under 30% inside, under half within 1 m and nine tenths within 3 m of
one, and if smaller than 30 m², under 60% within 3 m: no slivers of a mapped roof, no parasols) and off the mapped roads,
railways and water (under 30%: bridges and trains stand above the terrain model too). In Interlaken (2 × 2 km, 2879
mapped buildings) this leaves 64: sheds, garages, carports, greenhouses, canopies, tents and a few unmapped houses.

| Field | |
|---|---|
| `x`, `y`, `z` | The center of the rectangle around its footprint; y the lowest ground under it (so a building on a slope doesn't float). |
| `length`, `width` | The rectangle's long and short sides (m). |
| `angle` | The long side's direction, clockwise from north (−z) toward east (+x), 0–180°. A box whose length runs along z is turned by **−angle about +y** (right-handed). |
| `height` | The top above the ground (m): the ridge, or the flat roof. |
| `eaves` | Where the roof begins (m). |
| `roof` | `pitched` (measured: the ridge 1.5 m or more above the eaves; the map's `roof:shape` where it has one) or `flat`. |
| `footprint` | The outline [x, z] (counterclockwise seen from above); found in the images, the rectangle's corners where the building nearly fills it (85%). |
| `origin`, `measured`, `class`, `tags` | Where the outline comes from, whether the heights are measured, the map's `building` value and tags (`name`, `building:levels`, `roof:shape`, `roof:colour`, `building:material` …). |

Pick a building model of about `length` × `width` × `height`, or extrude the footprint to the eaves and put a roof on.

### Boulders

Lone rocks (1.5–40 m² seen from above) in meadows and pastures: gray, rough parts of the image with little other rock,
no road or building and no forest around them, not standing above the terrain model (cars do), not on the map's roofs,
roads and parking lots; and the map's `natural=stone` (2 m) and `natural=rock` (1.5 m) where none was found within
3 m (`"origin": "map"`). Scree fields and rock faces are areas, not boulders.

| Field | |
|---|---|
| `x`, `y`, `z` | Its center on the ground. |
| `radius` | Its radius seen from above (m). |
| `height` | How far it stands out of the terrain model (m), at least half its radius. |

### Areas

The map's areas (`"origin": "map"`: closed ways and multipolygons with their holes, cut at the area's edges) come
first; the images add areas of their kinds where the map has none of the kind (within 5 m):

| Kind | From the map | Found in the images |
|---|---|---|
| `forest` | `landuse=forest`, `natural=wood` | Trees covering more than half the ground within 5 m, at least 1000 m²; clearings of 400 m² and more are holes. |
| `rock` | `natural=bare_rock` | Bare rock and scree less steep than 45°, at least 100 m². |
| `cliff` | | Rock 45° and steeper, at least 50 m². |
| `gravel` | `landuse=quarry` | Gravel and bare soil (riverbeds, gravel pits, fine scree) at least 6 m across (roads aren't areas), 200 m². |
| `paved` | `amenity=parking` | Asphalt and concrete at least 8 m across (squares, parking lots), 300 m². |
| `water` | `natural=water`, `landuse=reservoir`, `landuse=basin`, `waterway=riverbank` | Lakes and rivers, at least 100 m². |
| `snow` | `natural=glacier` | Snow and ice, at least 200 m². |
| `scree` | `natural=scree`, `natural=shingle` | |
| `scrub` | `natural=scrub`, `natural=heath` | |
| `grassland` | `natural=grassland`, `landuse=meadow`, `grass`, `village_green`, `recreation_ground` | |
| `farmland` | `landuse=farmland`, `allotments`, `plant_nursery` | |
| `orchard` | `landuse=orchard` | |
| `vineyard` | `landuse=vineyard` | |
| `wetland` | `natural=wetland` | |
| `sand` | `natural=sand`, `natural=beach` | |
| `builtUp` | `landuse=residential`, `commercial`, `industrial`, `retail`, `farmyard`, `garages`, `construction` | |

`size` is the area in m² without the holes. The images' outlines follow the ground cover map's cells, simplified to
about a meter. Areas may overlap (a forest in a built-up area). Scatter rocks over rock and scree, use the forest
outline for undergrowth and far-off tree impostors, orchards and vineyards for rows of trees and vines.

### Lines

From the map, cut at the area's edges, every point on the ground (y); on bridges and in tunnels (`structure`) y runs
straight between the ends.

| Kind | From the map | Points | Width, height |
|---|---|---|---|
| `river`, `stream`, `canal`, `ditch` | `waterway=…` (`drain` is a ditch) | at most 5 m apart | `width` tag, else 15, 2, 8, 1 m |
| `powerLine` | `power=line` | its towers | height: `height` tag, else by `voltage`: 50 m from 220 kV, 35 m from 110 kV, else 25 m |
| `minorPowerLine` | `power=minor_line` | its poles | height 10 m |
| `aerialway` | `aerialway=cable_car`, `gondola`, `chair_lift`, `drag_lift`, `t-bar`, `platter`, `rope_tow`, `zip_line` … | its stations and pylons | height: cable car 25 m, gondola 15 m, chair lift 10 m, drag lift 5 m |
| `railway` | `railway=rail`, `narrow_gauge`, `funicular`, `light_rail`, `tram`, `monorail` | at most 5 m apart | width 3 m |
| `fence`, `wall`, `hedge` | `barrier=fence`; `wall`, `city_wall`, `retaining_wall`; `hedge` | at most 5 m apart | height 1.2, 1.5, 1.8 m (or the `height` tag) |
| `cliff` | `natural=cliff` | at most 5 m apart | the cliff falls away to the right of the line's direction |

Power lines and aerialways are obstacles a paraglider must see: put a tower or pylon model at each point and hang the
wires (or cable) from tower to tower at `height`, with some sag. `class` and `tags` (`voltage`, `cables`,
`aerialway:occupancy`, `name` …) tell more.

### Landmarks

Points of the map worth a model or a label, on the ground (y), with their `name`, `elevation` (the map's `ele`, m),
`height` (the structure's, m), `class` and `tags`:

| Kind | From the map |
|---|---|
| `peak`, `saddle`, `spring`, `cave` | `natural=peak`, `saddle`, `spring`, `cave_entrance` |
| `takeoff`, `landing`, `flyingSite` | `sport=free_flying` with `free_flying:site=takeoff`, `landing` (or `toplanding`), else a site (as points or areas: at their center) |
| `mast`, `tower`, `cross` | `man_made=mast`; `tower` (`tower:type` tells: communication, observation …); `cross`, `summit_cross` |
| `hut`, `shelter` | `tourism=alpine_hut`, `wilderness_hut`; `amenity=shelter` |
| `guidepost`, `bench`, `viewpoint` | `information=guidepost`; `amenity=bench`; `tourism=viewpoint` |
| `station` | `aerialway=station` |

### Ground cover map

`landcover.png` is `columns × rows` cells of `spacing` meters, row by row from the north-west corner at (`x0`, `z0`):
the cell (c, r) covers x0 + c·spacing … x0 + (c + 1)·spacing. Each gray value is a class number, the most common class
of the cell's pixels:

| Value | Class | | Value | Class |
|---|---|---|---|---|
| 0 | none (no image) | | 6 | gravel |
| 1 | tree | | 7 | rock |
| 2 | shrub | | 8 | building |
| 3 | grass | | 9 | water |
| 4 | soil | | 10 | snow |
| 5 | paved | | 11 | shadow (too dark to tell, where nothing around tells either) |

Use it as a splat map for ground textures and for scattering grass, flowers and stones. The map is 1 m by default and
coarser for large areas (at most 4096 cells across). The map's water and glaciers, buildings (also where no surface
model tells them from the ground) and roads (as wide as they are, by their surface) are painted into it.

## How the features are found

Every pixel of the aerial image (read at `spacing`) is classified from its color, how much its brightness varies
within about a meter (texture), the slope, the altitude and, with a surface model, the height of what stands on the
ground and how rough that is: tall and green (or rough) is a tree, tall, smooth and not green a building, low green a
shrub; on the ground white is snow, flat and bluish water, green grass, gray and rough or steep rock, gray and smooth
asphalt (darker) or gravel (lighter), beige gravel, brown soil.

- **Trees** with a surface model: the canopy height model (surface less terrain, where trees and shrubs are) smoothed
  by half a meter; treetops are the highest points within a window growing with the height (0.6 m + 7%), crowns grow
  from them down the canopy to half the top's height. A crown falling off steeply from its top is a conifer. Without a
  surface model: crowns are green pixels darker than open grass (their leaves shade each other) but brighter than the
  cast shadows beside them; their parts are split where they narrow. Measured against the lidar in Switzerland, the
  image finds about as many trees, but only a third of them within 3 m of a lidar tree, the crowns' dark part about
  half as wide (scaled by 1.8) and the heights estimated (5.5 × the crown's radius).
- **OpenStreetMap**: one Overpass API query for the area's bounds rounded out to 0.01° (up to 0.1° a side; larger
  areas take several) asks for the roads, buildings, land use and natural areas, waterways, power lines, aerialways,
  railways, barriers, parking, and the nodes of trees, rocks and landmarks; the answer is cached for 30 days (a query
  takes about 30 s). The points of the map's features get the ground's height from the blocks they lie in, the roads
  the images' cover along them, and the buildings their measured heights from the block that holds their center.
- **Roads traced in the images**: the asphalt, gravel and soil pixels at 0.5 m, closed by a meter and their small holes filled, thinned to
  a skeleton, traced as a graph whose spurs are pruned. Not roads: steep lines (rock slabs), very wide or uneven ones
  away from buildings (bare patches), lines among rocks, lines in bare ground no smoother than the ground around them
  (scree), short asphalt away from buildings (dark rock streaks). The area is searched in blocks with 32 m of their
  neighbors around them; roads are cut at the blocks' edges and joined again.
- **Buildings**, **boulders**: parts of their pixels as above; **areas**: the ground cover map, smoothed, small parts
  dropped and small holes filled, outlined along the cells' edges.

## Data and licenses

| Source | Used for | Credit | License |
|---|---|---|---|
| SWISSIMAGE (swisstopo) | Images, Switzerland, 10 cm | © swisstopo | Free, including commercial use |
| swissALTI3D (swisstopo) | Terrain, Switzerland, 0.5 m | © swisstopo | Free, including commercial use |
| swissSURFACE3D Raster (swisstopo) | Surface model, Switzerland, 0.5 m: tree heights, buildings | © swisstopo | Free, including commercial use |
| NAIP (USDA) | Images, United States, 60 cm | USDA Farm Production and Conservation Business Center, NAIP | Public domain; credit asked |
| USGS 3DEP | Terrain, United States | U.S. Geological Survey | Public domain; credit asked |
| OpenStreetMap (Overpass API) | Roads and paths, buildings, areas, lines, landmarks, trees, worldwide | © OpenStreetMap contributors | Open Database License (ODbL) 1.0: free, including commercial use; data derived from it stays under the ODbL when shared |

Satellite images (Sentinel-2, 10 m) show neither trees nor paths: blocks with only those are classified but nothing is
looked for in them. A surface model for another country is an `IElevationSource` added to `TerrainSources.Surface`.
Ship `ATTRIBUTION.txt` with a game that uses the features. With features from OpenStreetMap, credit "© OpenStreetMap
contributors" where the game shows its credits; the ODbL's share-alike applies to the map data itself (offer
`features.json`'s map features, or how they were made, under the ODbL), not to the rest of the game.
