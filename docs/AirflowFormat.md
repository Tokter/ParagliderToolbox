# Airflow format and solver

The Airflow Simulator computes how the air moves over a terrain for a weather (a morning sounding and a wind) and a
time of day: the mean wind with its venturi effects in passes, lift on windward slopes and sink in the lee, turbulence
behind sharp edges, thermals from the sun-heated ground capped by the sounding's stability, and clouds. "Export
airflow" writes the result as an `.airflow` file for a game; the toolbox keeps the same bytes in its project file.

The reference implementation is `ParagliderToolbox.Airflow` (C#, no dependencies besides .NET), usable in a game as it
is: `AirflowField.Load(stream)` reads a file, `field.Sample(x, y, z)` gives the airflow at any point, and
`AirflowSimulation` runs the solver over a game's own heightmap.

```csharp
using var file = File.OpenRead("Interlaken.airflow");
var field = AirflowField.Load(file);
AirflowSample air = field.Sample(x, y, z);   // air.Wind (m/s), air.Turbulence, air.LiftChance, air.LiftStrength …
foreach (Thermal thermal in field.Thermals) SpawnThermal(thermal.X, thermal.Z, thermal.Ground, thermal.Top, thermal.Climb);
```

## Conventions

- Meters, seconds. **+X east, +Y up, +Z south** (north is −Z): the terrain's frame (see
  [TerrainFormat.md](TerrainFormat.md)): an airflow over an exported terrain lines up with its tiles. **Y is the height
  above sea level.**
- The grid: `nx × nz` columns of `cellSize` from (`origin[0]`, `origin[2]`), `ny` levels of `layerHeight` from
  `origin[1]` (below the lowest ground). Values are at the cells' centers: cell (i, j, k) is centered at
  `origin + ((i + ½)·cellSize, (j + ½)·layerHeight, (k + ½)·cellSize)`.
- Cells inside the terrain hold zeros. `ground` gives each column's mean ground height; below it there is no air.
- Wind directions in `info` are meteorological: where the wind comes from, clockwise from north (270 is a west wind).

## The file

All numbers little-endian.

| Bytes | Content |
|---|---|
| 4 | `PTAF` |
| 2 | format version (1) |
| 2 | 0 |
| 4 | the header's length *n* |
| *n* | the header, UTF-8 JSON |
| … | the arrays, in the header's order (`fields`, then `maps`): each a 4-byte length *m* and *m* bytes of zlib (RFC 1950) |

A decompressed array is the values of its `type` (`float32`, `int16` or `uint8`) in its order; multiply by `scale` for
the value. 3D fields go level by level from the bottom, each level row by row from north to south, each row from west
to east: index `(j · nz + k) · nx + i` (every level is a north-up image). Maps go row by row: `k · nx + i`.

```json
{
  "format": "ParagliderToolbox.Airflow", "version": 1,
  "grid": { "nx": 102, "ny": 82, "nz": 102, "cellSize": 100, "layerHeight": 50, "origin": [-5100, 500, -5100],
            "order3D": "(j * nz + k) * nx + i", "order2D": "k * nx + i" },
  "info": { "latitude": 46.6863, "longitude": 7.8632, "windDirection": 270, "windSpeed": 4.17, "localTime": "2026-07-15T14:00:00",
            "utcOffset": 2, "sounding": "Summer, classic", "sunAzimuth": 194, "sunElevation": 64, "simulatedTime": 1800,
            "averagedTime": 600, "steps": 450, "computeTime": 2.2, "device": "NVIDIA GeForce RTX 4090", "created": "…" },
  "fields": [ { "name": "windX", "type": "int16", "scale": 0.01 }, … ],
  "maps": [ { "name": "ground", "type": "float32", "scale": 1 }, … ],
  "landCovers": [ "meadow", "forest", "field", "rock", "urban", "water", "snow" ],
  "thermals": [ { "x": -673, "z": -690, "ground": 775, "top": 2350, "climb": 5.5, "cloudBase": 2313,
                  "core": [ { "x": -673, "y": 850, "z": -690 }, { "x": -648, "y": 862, "z": -688 }, … ] }, … ],
  "triggers": [ { "x": -550, "z": 4850, "height": 1412, "score": 2.1, "heat": 241, "cover": "meadow" }, … ],
  "convection": { "…": "the thermals and cumulus followed, when the run followed them: see below" }
}
```

### Fields (3D)

| Name | Type, scale | Meaning |
|---|---|---|
| `windX`, `windY`, `windZ` | int16, 0.01 m/s | The mean wind, averaged over the end of the simulation (`averagedTime`). `windY` is the vertical wind: lift and sink. |
| `turbulence` | int16, 0.01 m/s | The standard deviation of the wind around its mean (the resolved gusts and the solver's sub-grid turbulence): calm air is below 0.3, a lee rotor 2–4. |
| `liftChance` | uint8, 1/255 | The share of the time the air rises at 1 m/s or more: where thermals pass now and then. |
| `liftStrength` | int16, 0.01 m/s | The mean climb of the air while it rises at 1 m/s or more. |
| `cloud` | uint8, 1/255 | The share of the time the cell is in cloud. |
| `temperature` | int16, 0.01 K | How much warmer the air is than the morning sounding (potential temperature): the afternoon's mixed layer, the thermals' warm cores. |
| `vapor` | int16, 10⁻⁶ kg/kg | The water vapor (mixing ratio); missing in files written before it was added (read as 0). |
| `baseWindX`, `baseWindY`, `baseWindZ` | int16, 0.01 m/s | Only from a run that followed its thermals: the base flow, the mean wind of the air between them (see [Thermals and cumulus followed](#thermals-and-cumulus-followed)). |

### Maps (2D)

| Name | Type, scale | Meaning |
|---|---|---|
| `ground` | float32, m | The column's mean ground height. |
| `surfaceHeat` | int16, 1 W/m² | The sensible heat the ground gives the air at the end: sunny slopes, dry fields and rock most, forests little; shade and lakes colder than the air cool it (negative). |
| `boundaryLayerTop` | float32, m | How high the convective boundary layer reaches: the thermals' top. |
| `cloudBase` | float32, m | Where air rising from the ground condenses; below `boundaryLayerTop` the thermals carry cumulus. |
| `thermalVelocity` | int16, 0.01 m/s | The convective velocity scale w* (Deardorff): the typical climb of the column's thermals. |
| `landCover` | uint8 | The column's main land cover, an index into `landCovers`. |

### Thermals and triggers

`thermals` lists where the air rises on average within the boundary layer with a warm core (ridge lift and waves rise
too, but are cooler): where it leaves the ground (`x`, `z`, `ground`), how high it reaches, its mean climb where it is
strongest (before a glider's own sink), its cumulus' base (`NaN` for blue thermals) and its `core`, a point per quarter
layer from the ground up (`{ "x", "y", "z" }`): thermals lean with the wind, so a thermal rising from a town on a
shore is over the lake higher up. The core is the path the rising air takes through the mean wind, followed back down
to where it comes within one and a half layers of the ground (below, the warm air flows up along the slope) and up to
where it climbs at less than 1 m/s (above, it carries no glider; `top` is there). None rises from water colder than
the air. A game can anchor its thermal models at them, lean them along their cores and let them drift with
`windX`/`windZ`.

`triggers` lists likely thermal triggers: ground hotter than its surroundings, and knolls and ridges that stand out in
the sun (`score` above 1 is a strong one).

## The simulation

**Inputs.** A heightfield in the terrain's frame (the toolbox samples its terrain at a quarter of the cell size), the
aerial images' colors on it (optional), the morning sounding, the wind (direction and speed about 800 m above the
ground, logarithmic below), the date and local time, the latitude and longitude.

**Land cover.** Every heightfield sample's color is classified with its slope, height and relief: snow (bright, gray),
water (bluish and flat: elevation models are flat to centimeters over lakes, so even bright turquoise shallows count),
forest (dark green), meadow (green), fields (brown), rock (gray and steep or high), towns (gray and flat). Each land
cover has an albedo, a share of its available energy that heats the air (the rest evaporates water), a share of the
net radiation that goes into the ground, and a roughness length.

**Sun.** NOAA's solar position for the time; Meinel's clear-sky beam through Kasten and Young's air mass (thinner in the
mountains) plus diffuse light. Every sample gets the beam by the angle of its slope to the sun, unless the terrain
shades it (traced towards the sun), and the diffuse light by how much sky it sees. Net radiation = absorbed sunlight −
90 W/m² of longwave loss; the cover splits it into ground storage, sensible heat and evaporation. The columns take
their samples' means.

**Water.** Lakes store the sunlight in their depth; their surface stays at the water's temperature: the season's
(lowland lakes: 4–5 °C in winter, 21 °C in July; 6 K colder per kilometer higher; `AirflowSettings.WaterTemperature`
sets it). Every step the water's heat and vapor follow from the bulk formulas with the air just above it
(C = 1.1·10⁻³, the wind at least 1 m/s): a lake under warmer afternoon air cools it (−10 to −40 W/m²), stabilizes it,
stops thermals and makes the air sink over it; a lake warmer than the air, as in autumn, warms it. The air over water
colder than the afternoon air starts with a cool layer 60 m deep, at the water half way to its temperature.

**The day so far.** The heat of every column from the morning until the simulation starts (in 20 minute steps, with
the sun and the shadows of each) is spread over about 3 km and grows the sounding's mixed layer (encroachment: the
heat warms the lowest air until it is well mixed up to where the sounding is as warm; inversions take much heat to
break through). The solver starts from that afternoon state, with the wind's profile everywhere.

**Solver.** The incompressible Navier–Stokes equations with the Boussinesq approximation on a staggered Cartesian grid
(velocities on the faces, temperature and moisture in the cells). The terrain is cut into the cells: every face knows
the share of it that is open air, measured on the heightfield's sub-cell samples, and the pressure equation weights
the faces by it (a variational cut-cell method), so the flow follows slopes rather than steps. Cells with less than 15%
air count as ground. A step:

1. MacCormack advection (semi-Lagrangian back and forth along the midpoint-rule path, corrected, limited to the
   values it interpolated) of the velocity, the potential temperature and the vapor and cloud water. The potential
   temperature is advected as a whole, so rising air ends up colder than the warmer layers above it: stable air
   resists vertical motion, which caps thermals and makes lee waves.
2. Buoyancy from the warming, the vapor and the cloud water, against each level's horizontal mean.
3. Smagorinsky viscosity (C = 0.18), scaled by the gradient Richardson number (none above 0.25, up to three times
   in unstable air); turbulent Prandtl number 1/3.
4. Where the ground passes through a cell: logarithmic friction by the cover's roughness, and the column's heat and
   vapor.
5. Condensation and evaporation (saturation adjustment, with the latent heat); heavy cloud water rains out slowly.
6. Relaxation towards the inflow near the upwind sides (and weakly near the others), and a sponge in the top quarter
   that keeps gravity waves from reflecting off the lid.
7. The boundaries: the upwind sides take the inflow, the others let the flow out (air entering there takes the
   inflow's), the lid is closed.
8. The pressure projection: conjugate gradients preconditioned with a multigrid V-cycle (red–black Gauss–Seidel,
   coarse levels by aggregating 2 × 2 × 2 cells with their faces' weights summed), from the last step's pressure.

Time steps are up to 4 s (Courant number 1.2). The result averages the last `averagedTime` (10 minutes by default) of
the simulated time (30 minutes); thermals are then found as columns where the mean climb peaks among their neighbors
with a warm core.

**On a graphics card.** `ParagliderToolbox.Airflow.Gpu` (depends on [ILGPU](https://ilgpu.net): CUDA, else OpenCL)
runs the same steps as kernels, one thread per face, cell or column, with the state and the running averages in the
card's memory: `new AirflowSimulation(heights, colors, settings, solver: AirflowGpu.Factory)` (`Factory` is null
without a card, and falls back to the processor for a grid the card can't hold). It agrees with the processor's
solver to rounding (single precision, fast math); the flow is turbulent, so over a long run the details drift apart
while the averages, the thermals and their climbs stay the same. An RTX 4090 runs Interlaken's 0.85 million cells at
5 ms per step (30 minutes in 2 s, 20 times the processor's speed), 6.8 million in 45 ms; the info's `device` names
where a result was computed.

**Limits.** No Coriolis force, no radiation in the air, no rain; the boundary layer starts from a horizontally smoothed
encroachment estimate rather than a simulated morning. Thermals are as wide as a few cells (100 m cells resolve big
thermals; their cores' climb is a cell's mean). The domain's edges cut the terrain: the wind enters over the edge's
ground as it is, so the first kilometer downwind of an upwind edge shows forced lift of its own.

## Over time

At cells of 75 m and finer the solver resolves the thermals themselves: they rise from their triggers, grow cumulus at
the condensation level, the cumulus' shadows cut the ground's heat under them and the thermals die, and new ones take
over elsewhere. `AirflowSettings.CloudShadows` shades the ground: the cloud water along the sun's ray from every
column's ground cuts its direct sunlight (extinction 150 m²/kg), and the ground's heat follows within about three
minutes. `InflowTurbulence` (0.3 K at these cells) sets the air entering on the upwind sides convecting at once: random
warmth in 8-cell blocks just inside the upwind sides, renewed as the wind crosses them (the cell perturbation method).
The toolbox's Thermals detail runs about 200 cells across (50 m over 10 km, 25 m layers) for 90 minutes, averages the
last hour, and reaches 3.5 km above the highest ground: the cumulus grow to the sounding's stable layer, and the damping
layer in the grid's top quarter mustn't cut them.

The first half hour is a spin-up: the encroachment start has the whole boundary layer ready at once, so convection
breaks out everywhere together, overshoots and makes a burst of cloud (a fifth of the sky over Interlaken) that a real
morning spreads over hours. After it the cumulus come and go over a few percent of the area. On an RTX 4090 Interlaken's
8.4 million cells (6.8 million under 2.5 km of air) run the 90 minutes in two to three minutes, the census included.

`AirflowSimulation.FilmInterval` keeps a frame of the run every so many seconds of simulated time in
`AirflowSimulation.Film` (an `AirflowFilm`, for viewers; it isn't part of the `.airflow` file). A frame has, per column:
the vertical wind 100, 200, 300, 500, 800, 1200 and 1700 m above the ground (0.1 m/s steps; `LiftAt` interpolates
between the heights), the cloud's base and top (the lowest and highest level with cloud water over 0.01 g/kg) and its
water path (2 g/m² steps), and the share of the sun's heat its ground gets. A frame of 200 × 200 columns takes 0.5 MB.

```csharp
using var simulation = new AirflowSimulation(heights, colors, settings with { CloudShadows = true, InflowTurbulence = 0.3 },
    solver: AirflowGpu.Factory) { FilmInterval = 30 };
var mean = simulation.Run();
AirflowFilmFrame frame = simulation.Film!.At(45 * 60)!;
float climb = frame.LiftAt(simulation.Film.Column(x, z), 300);
```

## Thermals and cumulus followed

A run that resolves its thermals (`AirflowSettings.TrackConvection`; the toolbox turns it on for cells of 75 m or finer)
follows them and its cumulus through the averaged time, every `AirflowSimulation.CensusInterval` (30 s) of it, with a
`ConvectionCensus` (ParagliderToolbox.Airflow.Convection). The file keeps what it found as `convection` in the header,
and the base flow as three more fields.

**Thermals.** Every frame the vertical wind is averaged over 3 × 3 columns at every level (a single cell's is noisy).
Updrafts grow from the cells rising at 2 m/s or more, strongest first, through their neighbors rising at half the
seed's climb or more: thermals side by side stay apart, where a ridge's line of weak lift would join them all. Then
each grows straight up and down its own columns through the air rising at 0.75 m/s, down to where it leaves the ground
and up to its top. An updraft counts from 20 cells and 100 m deep, and is a thermal when its air is warmer than a ring
three cells wide around it at the same levels by 0.05 K (virtual potential temperature: vapor lightens the air, cloud
water weighs it down), or holds cloud. Air forced up a ridge is cooler than the air beside it.

**Following.** From one frame to the next, the earlier updraft is moved on by its own mean wind and widened by a column.
The largest overlaps of footprints (the columns an updraft fills) continue. A new updraft that overlaps one that
continued elsewhere split off it and belongs to its thermal: a thermal pulses, its bubbles parting and rising on their
own. One that overlaps none rose on its own. A thermal's strongest piece stands for it at every frame. Thermals seen in
fewer than two frames, warm less than half the time, never 300 m deep or never 150 m wide are left out.

**Cumulus.** The columns holding cloud water (over 0.01 g/kg), connected through their sides and corners, followed the
same way; a cloud's pieces add up. A cloud is fed by the thermals whose tops reach within two levels of its base, under
it or beside it.

**Base flow.** Per cell, the mean wind of the frames in which no thermal fills it (nor rises at 0.3 m/s within two cells
of a thermal's core) and no cloud. A cell they fill more than 80% of the time takes the base flow around it at its level.
It is the air a glider glides through between thermals: the slopes' lift that thermals carried is gone, and it sinks
where they rise on average. A game plays its own thermals over it (`AirflowField.BaseFlow()`, `BaseWindAt`).

```json
"convection": {
  "start": 1800, "end": 5400, "interval": 30, "area": 104857600,
  "thermals": [ { "id": 0, "start": 1830, "duration": 420, "x": -3702, "z": 2615, "ground": 558, "bottom": 30, "top": 1736,
                  "climb": 2.2, "peak": 4.2, "diameter": 335, "leanX": 0.2, "leanZ": -0.05, "driftX": 2.6, "driftZ": -1,
                  "cloudId": -1, "cut": false }, … ],
  "clouds": [ { "id": 0, "start": 2100, "duration": 600, "x": -300, "z": 150, "base": 2550, "top": 2800, "diameter": 500,
                "water": 3.2, "growth": 240, "thermalId": 12, "cut": false, "driftX": 3.1, "driftZ": -0.8,
                "shape": { "columns": 12, "rows": 9, "cellSize": 50, "west": -325, "north": -225,
                           "appear": "…", "vanish": "…", "bottom": "…", "top": "…", "water": "…" } }, … ],
  "sites": [ { "x": -3715, "z": 2746, "ground": 570, "rate": 17, "duration": 432, "climb": 2.2, "peak": 4.2, "diameter": 335,
               "top": 1736, "cloudShare": 0.06, "leanX": 0.2, "leanZ": -0.05, "count": 18 }, … ],
  "thermalCycle": [ 0.95, 0.98, 1.03, 1.04, 1.06, 1.05, 1.03, 1.02, 0.98, 0.92 ],
  "cloudCycle": [ 0.12, 0.25, 0.46, 0.64, 0.75, 0.75, 0.70, 0.60, 0.41, 0.32 ]
}
```

| Value | Meaning |
|---|---|
| `start`, `end`, `interval`, `area` | The time watched (s of simulated time), between frames (s), and the area (m²). |
| thermal `start`, `duration` | When it was first seen and how long it lived (from its first frame to one interval past its last). `cut`: it lived before or after the census, so its duration is too short. |
| thermal `x`, `z`, `ground` | Where it rose from at first (the center of its lowest level) and the ground there. |
| thermal `bottom`, `top` | Its core's lowest point above the ground, on average (near 0 when fed from the ground), and the highest it reached (above sea level). |
| thermal `climb`, `peak`, `diameter` | Its core's climb (the mean of its stronger half) and width (of a circle of its median level), on average over its life; its strongest climb. |
| thermal `leanX`, `leanZ`, `driftX`, `driftZ` | How far its top stands beside its bottom per meter of height; how fast it moved over its life (m/s). |
| thermal `cloudId` | The cumulus it fed, or -1. |
| cloud `x`, `z`, `base`, `top`, `diameter`, `water` | Where it was when largest, its lowest base and highest top, its largest diameter (of a circle of its area) and most water (t). |
| cloud `growth`, `thermalId` | How long it grew to its most water (s); the thermal that fed it first, or -1. |
| cloud `driftX`, `driftZ` | How fast it moved over its life (m/s). |
| cloud `shape` | Its shape over its life: a grid of `columns` × `rows` columns `cellSize` wide around its center (which moved with it), its west and north edges `west` and `north` from the center (m); five base64 byte arrays, row by row from the north: when in its life each column became cloudy (`appear`) and cleared (`vanish`, 0–255 for 0–1; a wisp's few columns appear first, the cloud spreads and builds, and its columns clear as it decays), its lowest and highest cloud above the cloud's `base` (`bottom`, `top`, 10 m steps), its most water (`water`, 2 g/m² steps; 0 where it never was). Missing in files from before shapes. |
| site | A place thermals rose from again and again: the peaks of the thermals' roots' density (smoothed over 150 m), each thermal going to the nearest within 500 m, two or more. `rate` per hour (of those born in the census), their median `duration` (seen whole), mean `climb`, `peak`, `diameter`, `top`, lean, the share with a cumulus. |
| `thermalCycle`, `cloudCycle` | How a thermal's climb (against its mean) and a cumulus' water (against its most) go over their lives, in tenths of them, averaged over those seen whole in four frames or more. |

Over Interlaken on a classic July afternoon (50 m cells): about 170 thermals an hour over the 100 km², climbing 2 m/s
in the core (4 at their strongest), 250 m wide, most living a few minutes, the plumes of the busiest sites most of the
hour; 60–70 sites, the busiest with 10–17 an hour; 70 cumulus an hour, half the thermals feeding one, most of them small
and short-lived. Without them the air 300 m above the ground sinks 0.4 m/s on average (the mean field's rises a little).

### Playing it in a game

`ConvectionModel` plays thermals and cumulus over the base flow as the run had them; a game ticks it and asks it for the
wind:

```csharp
var model = new ConvectionModel(AirflowField.Load(stream), seed: 42);   // already an hour old: thermals of every age
// Every frame:
model.Advance(deltaTime);
Vector3 wind = model.WindAt(x, y, z);                                   // the base flow and the thermals' lift
foreach (var thermal in model.Thermals) { /* thermal.AxisAt(y), thermal.Diameter, thermal.Strength … */ }
foreach (var cloud in model.Clouds)
    foreach (var column in cloud.Columns()) DrawCloudColumn(column.X, column.Z, column.Bottom, column.Top, column.Water);
```

- **Thermals** rise at the run's rate (`thermalRate`, a Poisson process; `Activity` scales it), each like one of the
  thermals the run followed, drawn evenly: from where it rose (give or take 40 m), as long, strong, wide and high, leaning
  and drifting as it did (give or take 10%). Its climb follows `thermalCycle` over its life and fades in and out over a
  minute. Those the census cut lived longer than it saw: they are stretched alike so the rate times the mean life is as
  many thermals as the run had about (`LifeStretch`). A thermal drifts as far as its model moved while the census saw it,
  then stays over its trigger.
- **Its lift** is its climb at that moment along its leaning axis, a Gaussian across it (half at its core's radius), and
  over its height half near the ground, full over the lower fifth, nothing at the top (mean 1). Around the core a weak
  skirt out to three radii (`Skirt`, a share of the climb) carries the rest of what the run's updrafts carried above the
  base flow: it is set so that 300 m above the ground the thermals add to the base flow on average what the run's mean
  had above it (the run's updrafts are wider than their cores).
- **Cumulus**: a thermal like one that fed a cumulus grows one with `CloudChance`, so they form as often as the run's
  formed over thermals; at the run's delay after the thermal rose (half its life at the latest), where its axis reaches
  the cloud's base. It is one of the run's cumulus over thermals that formed within 1.5 km of where its thermal's model
  rose (all of them where none did), drawn evenly, so a ridge's banks of cloud stay over the ridge: its life, its shape, its water and its
  drift; its columns (`Columns()`: each one's place, bottom, top and water now) appear and clear over its life as the
  run's did, so it grows from a wisp, spreads, builds and decays as they did, its tops and water rising and falling with
  `cloudCycle` (`Share`). Drawn as the toolbox draws the run's (a box per column, the denser the more water) a game's
  cumulus look like the run's. Files from before shapes give a dome at most 2.5 times its thermal's core wide, drifting
  with the base flow at its base.
- **Deterministic**: the same field, seed and times give the same sky however the time is cut into `Advance` steps
  (births are drawn in order, positions follow from the time), so networked players see the same thermals.

Over Interlaken (the run above, 10 km): 178 thermals an hour, 40 about at once (the run 48), 4 cumulus (the run 5 over
thermals), the mean vertical wind 300 m above the ground 0.00 m/s (the run 0.05, its base flow −0.40); a million
`WindAt` take 0.3 s.
