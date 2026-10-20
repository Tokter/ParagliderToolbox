# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

Paraglider Toolbox: a desktop app (C#, .NET 10) with tools for paraglider simulation and games. The UI is built with
[Atelier](https://github.com/Tokter/Atelier), the author's own UI framework, which is expected as a sibling checkout at
`..\Atelier` (override with `/p:AtelierRoot=...`). Atelier's own `CLAUDE.md` and `README.md` describe its controls,
markup API, property system, commands and workspaces; read them before using an Atelier feature for the first time.

## Commands

```bash
dotnet build ParagliderToolbox.slnx
dotnet test                                    # xUnit, tests/ParagliderToolbox.Tests
dotnet run --project src/ParagliderToolbox     # the app; PARAGLIDERTOOLBOX_SETTINGS=<folder> keeps settings elsewhere
```

## Architecture

`src/ParagliderToolbox/Framework` is the application shell; `src/ParagliderToolbox/Modules/<Feature>` holds features.

- **Model** (`Framework/Model`): every object in a project is a `ProjectNode` (`ObservableObject`): `Id`, `Name`,
  `Parent`, `Children`. `ContainerNode` holds children (`FolderNode`, `Project`). Property changes bubble up as
  `SubtreeChanged`; `ProjectDocument` uses that for `IsModified` (a computed value that changes while the node doesn't,
  e.g. an estimate once the graphics card is found: `OnComputedPropertyChanged`). `NodeTypeRegistry` maps each node class
  to a stable type id (saved as `$type`), display name, icon and category; loading drops nodes of `Retired` ids.
- **Serialization** (`Framework/Serialization/ProjectSerializer`): System.Text.Json with a contract modifier. Saved:
  public get/set properties (camelCase) and `Children`; get-only properties are dropped automatically; `[JsonIgnore]`
  for the rest. Children are polymorphic through the registry's type ids. File = `{ format, version, project }`;
  upgrade older formats in `Deserialize`. `Clone` duplicates subtrees with new ids.
- **Toolbox** (`Framework/Modules/Toolbox`): shared state (open `Document`, `SelectedNode`) and the extension points
  (also `GlobalCommands` for command groups active anywhere, `PropertyCategoryOrder`)
  modules fill in `IToolboxModule.Register`: `NodeTypes`, `Views` (Atelier `AreaEditorRegistry`: the views workspace
  areas can show), `DetailViews` (object specific UI per node class, resolved by base type), `PropertyEditors`
  (custom `PropertyGrid` editors), `JsonConverters`, `Menus` (main menu items), `DefaultWorkspaces`,
  `WorkspaceTemplates`. Modules are listed in `Program.Modules` (one that needs another takes it in its constructor:
  the airflow module the terrain module).
- **Commands** (`Framework/Commands`): `[RelayCommand]` + `[property: Command(name, group, ...)]` on view models; the
  Atelier generator registers them, so menus, the command palette and the keybinding editor pick them up. Groups:
  `Application` (`ShellViewModel`: files, palette, shortcuts, theme, layout), `Project` (`ProjectCommands`: add/rename/
  duplicate, anywhere in the window), `Project explorer` (delete/move, only while the tree has focus). Each creatable
  node type gets a runtime-registered `Add<TypeId>` command; a type registered with `createInteractively` (e.g. a
  dialog asking how to set the node up, null on cancel) is added through it (`ProjectCommands.AddNodeInteractivelyAsync`;
  `AddNode` uses the default constructor). Commands use `IShellDialogs` (implemented by `MainView`, faked in tests)
  instead of UI code; `ShowDialogAsync` shows a module's own content with Cancel/OK.
- **Views** (`Framework/Views`): `MainView` (title bar menu, `WorkspaceView`, dialogs), `ProjectExplorerView`
  (TreeView synced with `Toolbox.SelectedNode`), `PropertiesView` (PropertyGrid), `DetailView` (hosts the node's
  detail view). Views subscribe to the toolbox in `OnAttachedToVisualTree` and unsubscribe when detached. `MainView`
  handles the window's `Closing` event (title bar, Alt+F4, taskbar), deferring it to the unsaved-changes prompt;
  `IShellDialogs.CloseWindow` closes with `force: true` after the commands already asked.
- **Settings** (`Framework/Settings/AppSettings`): `%APPDATA%\ParagliderToolbox`: `settings.json` (theme, recent
  files), `keybindings.json` (command customizations), `workspaces.json` (layout).
- **App icon** (`Assets/Icons`): the title bar's Material Symbols "Paragliding" glyph (U+E50F of Atelier's
  `MaterialSymbolsRounded` font, default axes) in white on a rounded square in the theme primary (#6750A4, gradient
  #7A63C0 → #5A4594), rendered with SkiaSharp: `ParagliderToolbox.ico` (16–256 px, PNG entries) is the executable's
  `ApplicationIcon`; `ParagliderToolbox.png` (64 px) is embedded and set as the window icon in `Program`.

## Paraglider Model Creator

Two parts: `src/ParagliderToolbox.Paraglider` (a UI-free library, also usable by a game) and
`src/ParagliderToolbox/Modules/Paraglider` (the toolbox module). `docs/ProxyFormat.md` documents the proxy JSON and the
simulation algorithm for game developers; keep it in sync with the simulator.

- **Design** (`Design/GliderDesign`): every parameter (meters, degrees, chord fractions, span fractions η). `Curve`
  (`Mathematics/Curve`, monotone cubic, immutable, JSON-serializable) for distributions. `GliderPresets` creates the
  designs of the classes EN-A … EN-D (`WingClass`); keep `new GliderDesign()` unchanged (tests depend on it).
  `MeshSettings.FromDesign` resolves `MeshDetail` (LowPoly/Medium/High override the explicit mesh settings, Custom —
  the default of a new design and of old project files — uses them); generators read `MeshSettings`, not the explicit
  fields. Low poly: `CanopyBuilder` skin on every n-th rib without displacements or inlets, ribbon lines, diamond
  hardware. `RowCount` 2 is a two-liner (A, B).
- **Geometry**: `Airfoil` (NACA modified four-digit thickness + reflexed camber, or Selig/Lednicer .dat), `GliderShape`
  (planform, arc integrated from roll angles so arc length = flat span, washout, thickness, shark nose, rib layout,
  trim attitude; glTF frame: +Y up, +Z forward, +X pilot's left, carabiners at the origin; profile parameter
  t ∈ [−1, 1], x = 1 − cos(π|t|/2)), `CanopyBuilder` (skin grid with ballooning arcs, creases, wrinkles, pucker,
  inlets; ribs, mini-ribs, diagonal ribs, tip panels; displacements vanish on ribs so ribs close the cells),
  `MeshPart` (every vertex carries a `VertexBind` for skinning).
- **Rigging**: `RiggingLayout` (tabs, cascades, risers, brakes; straight lines), `RiggingBuilder` (tubes, straps, rings).
- **Texturing**: `CanopyTextureGenerator` paints base color + normal map in the canopy UV layout (u = (η+1)/2, v = (t+1)/2).
- **Proxy**: `ProxyBuilder` (sections on tab ribs, stations include the line rows, fabric/rib/bend/diagonal-rib
  constraints with a `ProxyMaterial`, tension-only lines with `LinePoint`s along the long ones so they sag, the pilot's
  `Hand` constraints holding the toggles, strips with outward pressure surfaces, controls (with the cells they close),
  sampled `SectionPolar`), `SkinBinder` (≤ 4 joints per vertex; lines follow their proxy path via
  `ProxyBuild.LinePaths`/`TabPaths`), `ProxyDeformer` (node frames → linear blend skinning). Joints are the nodes,
  then `GliderModel.SkinAttachments`: translation-only copies of the canopy nodes the rigging hangs from (never skin
  rigging to a rotating canopy joint: the lines swing with the fabric and zig-zag). Size skin arrays by
  `ProxyDeformer.JointCount`. Bump
  `ProxyModel.CurrentFormatVersion` and update `docs/ProxyFormat.md` when the proxy or the algorithm changes.
- **Simulation** (`GliderSimulator`): XPBD with forces recomputed every substep, one fixed-order Gauss–Seidel pass per
  substep (alternating the order destabilizes it), origin rebasing (float precision), load-weighted strip velocities
  (lift does no work), lifting-line induced angle by fixed-point iteration, flap lift for brakes, ram-air pressure cells
  whose inlets see the nose's own angle of attack and close when it flattens, and take in the flow by how they face it
  (3D, so spanwise flow passes them by: full within `InletCaptureAngle`, nothing across, suction from behind; a full
  stall empties the cells, a deep stall keeps them), and **fabric firmness**: canopy
  constraints blend from stiff (inflated) to limp (`DeflatedCompliance`) by their cells' pressure, so the wing folds
  like fabric in collapses, big ears and stalls; a one-sided pass after the constraints keeps the upper skin above the
  lower one at every station (`SurfaceSeparation`; limp cells would otherwise invert and the pressure would hold them
  inverted). Tunables in `SimulatorSettings` (`InletCaptureAngle`,
  `FirmnessAirspeedExponent`, `DeflatedDrag`, `EnclosedAir` — off; on the High proxy it needs `FabricDamping`;
  `PitchDamping`, thin-airfoil pitch damping per strip as a couple; `HandSpeed`, the rate the inputs follow the pilot;
  opt-in `LineDamping`/`FabricDamping`, velocity-level damping of taut constraints; `SubstepsFollowLoad`: substeps ×
  the pilot's G load, so spirals don't snap the outer tip at 2 G; scaling with airspeed instead either missed the
  spiral entry or, starting at trim speed, changed the full speed bar polar). Measured dead ends, don't retry
  blindly: XPBD position-level constraint damping softens the line chains in our single Gauss–Seidel pass and pitches
  the canopy back; velocity-level damping is neutral in flight (+10–25% time); enclosed air (+3 kg, no weight) changes
  little and costs glide. The phugoid itself is physical (damping ratio ≈ 0.09 ≈ 1/(√2·L/D)); the missing physics for
  violent collapse exits is the canopy's apparent mass (~40 kg normal to the canopy), which needs anisotropic inverse
  masses in both the integration and the constraint projection.
  **Calibration**: drag (`SectionPolar` Cd0 = 0.0075 + 0.04·ballooning, k = 0.004; `SpanEfficiency` 1.25; pilot drag
  area 0.33) is fitted to Flybubble's class polars (EN-B: trim 36 km/h at 1.11 m/s, top 48 km/h at 1.90 m/s); the
  presets' trim angles and (effective) speed bar travel set their trim and top speeds. The polar recorder compensates
  for speed changes (total-energy sink); the force balance (canopy lift / total drag) must match the measured glide.
  **Brakes** are calibrated to the symmetric stall at about 80% of each class's travel (EN-A 64 cm … EN-D 48 cm, past
  the EN 926-2 minimums) at 22–27 km/h: the angle of attack is measured against the profile's front (nose to 60% node),
  the flap adds camber lift (`FlapEffectiveness` 0.4), a stall shift (`FlapStallShift` 0.15) and drag (`FlapDrag` 0.4,
  on the rear nodes), the polar stalls at 16.5° + 60·(thickness − 0.14), and the trailing edge bay is soft against
  compression (`ProxyMaterial.TrailingEdgeCompression` 0.005). Lessons: with a stiff rear the brakes pitched the whole
  profile up about the A tabs (the C lines went slack) instead of curling the trailing edge, so every class stalled at
  30–40 cm and spun at 60% one-sided brake; what blocked the curl was the rear skin's shear diagonals (stiff in
  compression), not the chordwise fabric or the pressure; the flap's lift acts near the quarter chord (on the rear nodes
  it pitched the wing nose-down into tucks); softer than 0.005 a collapse tumbled; measure the stall progressively
  (2.5% steps): a brake yank from trim zooms the wing into a sticky deep stall. The bench's progressive stall measure
  and the one-sided brake sweep (turn, spiral or spin per class) are the calibration loop.
  **Float precision**: the simulator re-centers the system (pilot–canopy middle) within a meter of the origin after
  every step. At 16 m, rounding in the stiff constraints made the sink wander ±10% over tens of seconds and depend on
  the flight direction; `SimulatorSettings.EnergyDiagnostics` (energy per substep stage) is how that was found.
  Tests in `SimulationTests` pin the flight envelope and the shapes (trim, brakes and handles, slack brake lines, speed
  bar, collapse, big ears, frontal span fold, full stall and recovery); rerun them after any physics change.
  **Self-collision** (`SelfCollisionDistance` 0.4 m, `SelfCollisionExclusion` 1 m; Müller's Ten Minute Physics 15
  particle method): canopy nodes keep 0.4 m apart unless they are within 1 m at rest. Measured with a census of the
  proxy's surface triangles passing through each other (`SelfCollisionTests`): full stall 78 → 2.5 crossing pairs per
  frame on Medium (275 → 30 on High), spin 23 → 5 (252 → 36), collapses, frontals and big ears had none; +10% time on
  Medium, +20–30% on High. Lessons, don't retry blindly: Müller's `min(thickness, rest distance)` rule would stop cells
  from emptying (a cell's skins are 0.1–0.3 m apart at rest), hence the exclusion; 0.2 m lets nodes slip through the
  sparse proxy; a candidate margin from the fastest node made every pair a candidate on High (a few jittery light
  nodes), capping it at the distance let spins through, the 98th percentile works. Still there: skins crossing within
  a cell or its neighbor (about 200 pairs per frame in a stall, a few in big ears, the laid-out launch wing starts with
  ~380 in its bunched horseshoe); particles can't see them (excluded), and node–triangle contact (3 cm, the side at the
  substep start, crossings tested at the crossing time, rest-coplanar pairs excluded) removed them in collapses and big
  ears but not in stalls and spins and made frontals worse: on the coarse proxy most such crossings are edges through
  edges, and its pushes made new ones. It would need edge–edge contact too (and a clean launch layout). On the High
  proxy, the launch blew up (NaN after 4.6 s) without self-collision.
  `PolarRecorder` shows the polar effect. For visual checks of shapes, a headless renderer of the proxy (orthographic
  wire or shaded views over time) is far quicker than the app.
- **Launch** (`GliderSimulator.Ground`, `ResetOnGround`, `Reset(SimulationStart)`): flat ground with position-level
  Coulomb friction for every node (after `SeparateSurfaces`), the pilot standing at `StandingHeight` (one-sided), walking
  at `Inputs.Walk` with as much grip as the weight on the feet gives, the harness held upright by the heading (fading as
  the wing lifts the pilot), the heading turning toward the wing (`TurnRate`), the `ARiserLeft`/`ARiserRight` proxy
  controls (0.2 m), and empty fabric limper below `LimpAirspeed` (only with a ground). `ResetOnGround` lays the wing out
  (swung back about the carabiners, outer part pulled in, sections laid down toward the pilot) and settles it 2 s with
  damping 30/s. `LaunchTests` pin the layout, the wind filling the wing, the A pull, walking and turning.
  Measured lessons, don't undo: the deflated compliance (0.05) holds an empty cell's shape against its own weight, so
  without the limpness a laid-out wing stands up where its fabric is bunched (the horseshoe's tips) and pushes itself
  out until the lines stop it; limp only near the ground (not the whole canopy on the ground) made the rising wall half
  stiff and yaw; limper than 10× let the falling lines drag the trailing edges over the leading edges; sections laid
  square to the arc (instead of toward the pilot) or at the full arc's radius leave the outer lines too short once
  lowered (the lines lift the leading edges a meter); knots start at the risers' height and fall, which yanks the
  canopy into a heap without the settle damping. A 0.35 m A pull dropped the angle of attack about 19° and the wing
  shot up and overshot; at 0.2 m it comes up from a wall in about a second. The rise is still livelier than a real
  wing's (the canopy's apparent mass is missing, see above), the wing needs active flying to stay overhead (it yaws off
  to a side; a pilot turning fast with it, 90°/s, fed the yaw), and at 5–6 m/s the rising wing lifts and drags a pilot
  who doesn't step toward it. A scripted kiting pilot in the harness is hard to tune; flying it with a gamepad in the
  app is the real check.
- **Export** (`GliderExporter`, `GltfWriter`): .glb (skinned, PBR, embedded PNGs, optional proxy cage and baked
  animation), proxy JSON, OBJ/MTL/PNG, line plan CSV. Validate .glb changes with the Khronos glTF validator and a
  Blender import (Blender 4.4 is installed: `blender -b --python script.py`).
- **Polar** (`Polar/PolarRecorder`): flies a fresh `GliderSimulator` headless at a fixed step through trim, the speed
  bar steps and the brake steps (stopping at the first stall), ramp → settle → measure per setting; deterministic, so
  two recordings of one design are identical (an optimizer can compare them). `Summarize` fits a cubic sink(v) through
  the steady points for min sink and best glide (tangent from the origin). `PolarRecording` keeps the design, the
  settings, every sample and the points; `PolarCsv` writes them. Tests in `PolarRecorderTests`. In the module,
  `PolarRecorderOptions` is the editable form of the settings (step counts, times) shown by
  `ParagliderActions.AskPolarSettingsAsync` through `IShellDialogs.EditPropertiesAsync` (a property grid in a dialog).
- **Recorded flights** (`Simulation/FlightRecording`): `FlightRecorder` logs a simulated flight (call `BeforeStep`/
  `AfterStep` around every `GliderSimulator.Step`): the `Start` (flight or ground, with the `GroundHeight` in the frames'
  coordinates), the inputs of every step from the start (as changes), and while
  recording frames every second step (the proxy's node positions relative to where the recording started, 1 mm int16
  offsets from the pilot, each the difference to the frame before, deflated, base64; plus `FlightTelemetry`).
  `FlightRecording` keeps the design, the inputs and the frames; `Decode()` gives the frames. To examine a shared one:
  load the project file, take the `RecordingNode`'s recording, generate its design's proxy, `Reset(recording.Start)` a
  new simulator and fly it with `InputsAt(step)` for every step: it reproduces the frames (deterministic;
  `FlightRecordingTests`). `FrameVersion` 0 (older recordings) has 11 inputs in the frame telemetry, 1 adds the A risers
  and the walk.
- **Module**: `ParagliderNode` (a `ContainerNode` holding its `PolarNode`s and `RecordingNode`s; the parameters as [Inspectable] properties forwarding to a `GliderDesign`; colors saved as
  hex; `Snapshot()` for the generator thread; `MeshDetail` writes its settings into the explicit mesh properties, and
  editing one of those switches to Custom; `MeshDetail` is saved after them, `[JsonPropertyOrder(1)]`, so loading
  doesn't switch), `NewParagliderOptions`/`NewParagliderView` (the new-paraglider dialog: class cards, detail segments,
  triangle counts measured in the background; `ParagliderModule.AskNewParagliderAsync`), `ParagliderPreview` (debounced background generation, Scene3D, simulation
  loop driven by `Viewport3D.Rendered`; the Forces view draws `GliderSimulator.RecordForces`' per-node and per-strip
  lift and drag as one vertex-colored line mesh; Record adds a `RecordingNode` when stopped, without selecting it),
  `ParagliderPreview.Mode` (`SimulationMode` Flight or Launch, L): launching, the wind ramps to `MaxWind` at
  `WindRampRate`, the gamepad's triggers pull the A risers while the wing is low behind the standing pilot (until
  `LaunchedElevation`, back 10° below it; `GamepadPilot.TriggersPullARisers`: after a switch a held trigger acts again
  only once let go) and the left stick walks relative to `CameraYaw` while standing; the camera frames the layout from
  behind the wing and the grid is the ground,
  `GliderScene` (the model's parts and materials skinned to the proxy, the proxy meshes, the part toggles, the standing
  pilot figure for launches; shared by the preview and the replay), `RecordingPlayer`/`RecordingDetailView` (the replay: generates the recorded design, plays the
  decoded frames with play/pause, timeline, frame steps, speed; the animation export), `ParagliderDetailView`,
  `ParagliderActions` (global export commands; `AddPolar`, `AddRecording`, `ExportRecordingAsync`),
  `CurveEditor`/`CurvePropertyEditor`, `PolarDetailView` and `PolarChart` (plots a polar on Atelier.Charts' `XYChart`; also used live while recording). The 3D view is Atelier's `Atelier.Graphics3D.Viewport3D` (OpenGL on the window's
  context, composited into Skia).

## Terrain Generator

Terrains of real places, for paragliding games. Two parts: `src/ParagliderToolbox.Terrain` (UI-free; references the
paraglider library only for `GltfWriter`) and `src/ParagliderToolbox/Modules/Terrain`. `docs/TerrainFormat.md`
documents the tile set export for game developers; keep it in sync with the layout and the exporter.

- **Frame** (`Geodesy`): `LocalFrame`, a transverse Mercator (`TransverseMercator`, Krüger to n⁴, Newton for the
  latitude) centered on the terrain's center; +X east, +Y up (heights above sea level, not shifted), +Z south.
  `ICoordinateSystem` for the sources' systems: `GeographicCoordinates`, `WebMercator`, `SwissGrid` (LV95 with
  swisstopo's ~1 m approximate formulas). `GeoPoint.TryParse` reads decimal, DMS and Swiss coordinates.
- **Data** (`Data`): `DataCache` (whole resources with a max age and stale fallback when offline, 404s remembered;
  byte ranges of large files in 64 KiB chunk files, the folder naming the size: a terrain reads the headers of hundreds
  of files; consecutive misses in one request; 16 requests at once over HTTP/2, retries; reads of one file run in
  parallel, sharing the chunks in flight: serializing them per file made the coarse levels, which all read the one
  10 GB swissALTIRegio file, take minutes) and `GeoTiff`, an own COG reader (TIFF/BigTIFF, tiles/strips,
  none/LZW/Deflate/PackBits/JPEG with shared tables, both predictors, GeoKeys, GDAL no-data; overviews listed fine to
  coarse; block indexes beyond the 64 KiB header load when their image is first read). No GDAL on purpose: no native
  dependencies, and range reads fetch only the tiles and overviews a terrain needs.
- **Sampling** (`Sampling`): `RasterLevel<T>` (one image of a file or one map-tile zoom, read in blocks; heights as
  float with NaN, colors as RGBA uint with alpha 0 for none), `BlockCache<T>` (LRU, shared loads), `RasterMosaic<T>`
  (files side by side: bilinear across their edges), `RasterFill` (projects a `SampleRequest` into a system, exact on
  every 8th sample, loads the blocks, interpolates; averages up to 4 × 4 points where data is much finer).
- **Sources** (`Sources`): `IElevationSource`/`IImagerySource.FillAsync` fill only the still empty samples;
  `TerrainSources` orders them per area and spacing: preferred first, `SourceScope` Regional before Global, then the
  coarsest `Resolution` still fine enough for the spacing, then the finer ones (where it has no data), then coarser.
  So coarse levels read swissALTIRegio, not part of every swissALTI3D file (a Simulator root tile read 4257 ranges,
  350 MB). `TerrainSources.Surface` holds surface models (`SwissSurface3DSource`), which only the Feature Detector reads.
  `SwissAlti3DSource`/`SwissImageSource` (`SwissTopoCatalog`: the STAC API v1 in 8 km cells, the newest
  version per 1 km tile; the asset by gsd, then the overview by spacing; the 2 m files have only 2 and 4 m images),
  `SwissAltiRegioSource` (one BigTIFF COG, 10 m with overviews, Switzerland and ≥ 100 km around it, LV95; credits
  the neighbors' data), `CopernicusDemSource` (AWS bucket, 1° COGs; missing
  tiles are sea at 0), `Sentinel2CloudlessSource` (EOX's 2016 layer, the only CC BY one; `WebTileLevel`). Measured:
  Copernicus is a surface model, median 1–4 m and in forests 15–25 m above swissALTI3D. United States (public domain,
  `UsgsSources`, `NaipSource`): `Usgs3Dep1MeterSource` (lidar, 10 km UTM tiles in NAD83 found through the National
  Map's TNM Access API in 0.1° cells; projects overlap, so a fill pass per project, newest first; overviews to 32 m),
  `Usgs3Dep13Source` (1/3″ 1° tiles, `n39w123`, EPSG 4269), `NaipSource` (USDA aerial photos, 60 cm, RGB + near-infrared
  COGs found through the Planetary Computer's STAC API: the newest year per quarter quad; neighbors overlap with black
  borders, so passes by UTM zone and checkerboard square, black is no image). `EpsgSystems` maps a file's EPSG code to
  its system; `TiffImage.HasAlpha` reads ExtraSamples (NAIP's fourth sample is not alpha). Esri World Imagery is left
  out on purpose (its terms for offline use are unclear). Mount Tamalpais 10 km at 2 m: 4.6 min and 1.3 GB cold.
- **Layout** (`TerrainLayout`): a quadtree of tiles with `TileSamples` (2^k + 1) per side at every level, each level
  half as fine; the size rounds to whole level-0 tiles (the resolution stays as set); coarser tiles are cut back where
  the area ends; automatic levels reach one root; `DetailSize` splits tiles only near the center (the finest level
  within it, each coarser twice as far). A split tile has all its children.
- **Builder** (`TerrainBuilder`): per tile, coarse levels first, a few at once: heights with a one-sample border (from
  the neighbors, for normals) from the ordered sources; where a finer source ends, its heights ease into the next over
  `BlendDistance` (200 m; the coverage comes from a lattice aligned to the frame and reaching beyond the tile, so
  neighbors agree); holes take their neighbors' heights; the texture from the imagery sources, `ElevationColors`
  (height and slope) where there is none, as JPEG, read alongside the heights. Each level samples the sources at its
  own spacing (their overviews). `TerrainProgress` comes per tile and every `ProgressInterval` (0.5 s) while tiles
  build: level, its tiles, requests, bytes, elapsed (the root tile of a large terrain takes seconds on a cold cache).
- **Meshing/Export**: `TerrainMesh` (smooth with skirts 4 × the spacing deep, or faceted with per-face colors),
  `TerrainExporter` (tile folder: `terrain.json`, glb per tile with embedded JPEG, 16-bit PNG or RAW heightmaps,
  textures, `ATTRIBUTION.txt`; or the leaves down to a level as one glb), `Heightmaps` (own 16-bit PNG writer).
  `docs/images/render_terrain.py` renders an exported glb in Blender (the README's terrain renders; cameras by
  latitude/longitude from the root node's extras). README images of swisstopo data carry "© swisstopo".
- **Module**: `TerrainNode` (the settings as [Inspectable] properties on an immutable `TerrainSettings`; `Coordinates`
  parses pasted positions; source choices saved as ids, edited by `SourceChoiceEditor` listing the registered sources;
  read-only summary from the layout), `NewTerrainOptions`/`NewTerrainView` (location, sites, Arcade/Game/Simulator
  styles), `TerrainPreview` (builds on first show and on Generate, not on every edit: downloads; tiles appear as they
  come; `IsOutdated`; the status line names the level, tiles and downloads, the progress bar is indeterminate until
  the first tile), `TerrainScene` (LOD selection from the roots by projected sample spacing, meshes made within a
  30 ms budget per frame, half-texel UV inset because the viewport's textures repeat; `Clear` removes only its tiles,
  so overlays can share its `Scene`), `TerrainViewport` (the 3D view shared with the airflow view: LOD every frame,
  the flight, clipping planes that follow the camera, the altitude overlay, an `Animate` hook), `TerrainDetailView`,
  `TerrainActions` (exports reuse the preview's model when its settings match, else build; clear the cache),
  `TerrainFlight` (WASD/E/Q fly the orbit camera, target and all, at 0.8 × the height above the ground per second,
  Shift ×4, eased, above the ground; right drag looks around in place). The fly keys are commands of the "Terrain
  flight" group (rebindable; run from the palette they step a second's worth): the views take the keys bound to them
  in `OnPreviewKeyDown` (forwarded to `TerrainViewport.HandleKeyDown`) while held and release them when the focus
  leaves (GLFW releases keys on window deactivation). Posted window messages don't reach GLFW: drive the app with
  real input (keybd_event; Home needs the extended-key flag, else it is numpad 7). `TerrainNode.ChildTypes` lists
  what a terrain holds (modules add theirs); `TerrainModule.ModelAsync` gives a node's built model (the preview's when
  current); `TerrainModel.SampleColors` samples the textures on a grid (the coarsest level fine enough).
  The cache is `%LOCALAPPDATA%\ParagliderToolbox\TerrainCache` (`PARAGLIDERTOOLBOX_TERRAIN_CACHE`). Tests use fake
  sources and synthetic TIFFs (`TestTiff`, with an LZW encoder as libtiff writes it); nothing in the tests uses the
  network.

## Airflow Simulator

Airflow over a terrain for a sounding, a wind and a time of day, for games. Three parts: `src/ParagliderToolbox.Airflow`
(UI-free, no dependencies besides .NET, so a game can use it as it is), `src/ParagliderToolbox.Airflow.Gpu` (the
solver on a graphics card, ILGPU) and `src/ParagliderToolbox/Modules/Airflow` (runs on the card when there is one).
`docs/AirflowFormat.md` documents the `.airflow` file and the algorithm for game developers; keep it in sync.

- **Atmosphere**: `Thermodynamics` (moist air: θ, saturation, dew point, Bolton's condensation level), `Sounding`
  (levels of T and Td by height, tabulated every 10 m with hydrostatic pressure), `SoundingPresets` (Payerne-like
  morning soundings by season and stability; föhn with its south wind), `BoundaryLayer` (encroachment: the mixed layer
  the day's heat makes of the morning sounding; w*).
- **Sun/Surface**: `SolarPosition` (NOAA, clear-sky beam and diffuse), `HeightField` (the input terrain, row by row
  from north), `LandCovers` (RGB + slope + height + relief → meadow/forest/field/rock/urban/water/snow: flat and bluish
  is water, lakes are flat in the elevation models; albedo, sensible share, ground share, roughness), `SurfaceHeating`
  (slope incidence, shadows traced to the sun, energy partition, the day's accumulated heat; water gives no fixed
  flux: `WaterTemperature` by season and height, `WaterFlux` bulk formulas the solver applies every step).
- **Simulation**: `AirflowGrid` (Cartesian cells over the heightfield, cut-cell open shares of every face and air
  share of every cell from 4 × 4 sub-samples; cells under 15% air and faces under 10% open are ground; layouts
  `(k * Nx + i) * Ny + j`), `PressureSolver` (variational Poisson, PCG with an aggregation multigrid V-cycle, warm
  start), `AirflowSolver` (Boussinesq: MacCormack advection, buoyancy against level means, Smagorinsky with Richardson
  scaling, log-law friction and surface heat in the cells the ground passes through, saturation adjustment,
  relaxation bands and a top sponge, open outflow sides; each column's water share exchanges heat and vapor with the
  air above it by the water's temperature, `WaterHeat`), `AirflowAverages`, `AirflowSimulation` (land cover → sun →
  column fluxes → the day's heat spread over 3 km → mixed layers (a cool 60 m layer over water colder than the air) →
  solver → field: maps, thermals, triggers). Thermals: columns where the warm-core mean climb peaks, their core traced
  through the mean wind down to 1.5 layers above the ground (lower, the path follows the upslope wind to the valley
  floor) and up to 1 m/s; none from water colder than the air. Tracing the strongest neighbor cell per level instead
  wanders along the slopes' lift bands; placing a thermal at its strongest level put shore thermals over the lakes.
  Lakes once took the land's heat partition (+20–30 W/m² on a July afternoon): water must cool warmer air.
  Measured lessons, don't undo: advect the potential temperature as a whole (the perturbation plus the
  background at the departure height), else stable air doesn't resist rising and nothing caps thermals; slivers of
  air (1% cells, few-% faces) are hardly constrained by the pressure and carry wild velocities; an open side whose
  zero-gradient copies an inward flow feeds itself (air entering takes the inflow's, and all sides relax weakly);
  mixed layers grown per column from their own ground differ by kelvins at one height and overturn violently (grow
  them over the area's mean ground); heat a sliver's share into the cell above. Interlaken 10 km at 100 m cells
  (0.85M cells): about 0.1 s per step on the CPU (pressure 45%, advection 35%), 30 minutes in 45 s; `SolverTests` pin
  mass conservation, the venturi, ridge lift and lee sink, a thermal capped by an inversion and lee turbulence.
  `IAirflowSolver` is what `AirflowSimulation` drives (step, accumulate the averages, `Averages()`/`Snapshot()` as
  `AirflowCells`); `AirflowSetup` holds what every solver precomputes (sounding per level, inflow targets, surface
  fluxes, the pressure hierarchy); the `solver` factory of `AirflowSimulation` picks the implementation.
- **GPU** (`src/ParagliderToolbox.Airflow.Gpu`, ILGPU 1.5.3: CUDA, else OpenCL): `GpuAirflowSolver`, the same steps
  as `GpuKernels` (one thread per face/cell/column; the pressure PCG's dots and maxima are grouped reductions with
  one atomic per group, read back per iteration; the coarsest level in one group with barriers), state and averages
  on the card. `AirflowGpu.Factory` (null without a card; falls back to the CPU solver when the card can't take the
  grid). The kernels can't use `System.Numerics.Vector3` (.NET 10 builds it on intrinsics ILGPU can't compile: own
  `Float3`), and ILGPU's CPU accelerator emits types into the dynamic assembly `ILGPURuntime` (InternalsVisibleTo it).
  `GpuSolverTests` compare it with the CPU solver on ILGPU's CPU accelerator (1 mm/s after 12 steps) and, when there
  is a card, on it (fast math: a few cm/s after 40 steps). RTX 4090: Interlaken 10 km at 100 m 5 ms per step (30 min
  in 2.2 s, 20×), at 50 m (6.8M cells) 45 ms; the pressure solve is 75–85% of it, near the card's memory bandwidth.
- **Resolved convection**: at cells ≤ 75 m (`AirflowNode.ResolvedThermals`) the landscape run resolves thermals and
  their cumulus coming and going. Solver features for it, both solvers: cloud shadows (`ShadedHeat`, `SunDirection`:
  the cloud water along the sun's ray, the ground's heat following in `GroundResponseTime`), inflow noise
  (`InflowNoise`, the cell perturbation method: blocks just inside the upwind relaxation band, renewed as the wind
  crosses them); `AirflowSimulation` puts ±0.1 K of noise in the lowest 300 m of every start (an even start convects
  only after minutes, in rings from the sides). `AirflowSimulation.FilmInterval` keeps an `AirflowFilm` of the run (per
  column and frame: lift at `LiftHeights` above the ground, cloud base/top/water path, sunshine; in memory, not in the
  `.airflow` file). Measured (Interlaken 50 m, RTX 4090): 6.8M cells 90 min in 93 s, 8.4M (3.5 km clearance) with the
  census 2–3 min; the first ~30 min are a spin-up burst (convection breaks out everywhere at once from the encroachment
  start: ~20% cloud), then 3–5% cumulus that come and go; with 2.5 km clearance the cumulus over the high ridges hit the
  damping layer.
- **Convection** (`Convection/`): `ConvectionCensus` (with `AirflowSettings.TrackConvection`, fed every
  `CensusInterval` of the averaged time from the film's snapshot buffer): per frame w over 3 × 3 columns, updrafts grown
  from 2 m/s seeds strongest first through half the seed's climb, then straight up/down their columns through 0.75 m/s;
  thermal = warmer than a 3-cell ring at its levels (θv) or cloudy; clouds = cloudy columns (8-connected); both followed
  by footprint overlap (moved by their wind, widened a column), split-offs join the parent's family (= one thermal or
  cloud, its strongest piece per frame); a cloud links to thermals topping within two levels of its base; the base flow
  averages per cell the frames free of thermals (and 0.3 m/s within 2 cells of them) and clouds, filling cells free < 20%
  of the time from their level's neighbors. `ConvectionClimate` (`ThermalLife`, `CloudLife`, `ThermalSite` from the
  roots' density peaks, `ThermalCycle`/`CloudCycle` in tenths of a life) goes into `AirflowField.Convection` and the
  file's header; the base flow into `BaseWindX/Y/Z` (`BaseFlow()` shares the other arrays, `BaseWindAt`). Measured
  lessons, don't undo: a fixed 1 m/s core threshold fragments thermals into 1–3 min pieces; growing down to 0.75 m/s
  joins everything along a sunny ridge (its upslope band and convergence lines) into one 400k-cell updraft; families
  without the 300 m depth / 150 m width filter count every flicker. Interlaken: ~170 thermals/h over 100 km², core 2 m/s
  (peak 4), 250 m wide, 60–70 sites, ~70 cumulus/h, half the thermals feed one, the base flow sinks 0.4 m/s at 300 m.
  The scratch harness drew the sites with roots and leans, the lives as bars over time, mean vs base w maps, and
  consecutive frames of a window with the updrafts colored by family (dumped per frame): that is how to judge changes.
  The box thermals (one thermal at a time nested in the landscape at 25 m) were removed; the `thermal` node type is
  retired (`NodeTypeRegistry.Retired`: loading drops such nodes).
  `ConvectionModel` (the game side; `LiveThermal`, `LiveCloud`): Poisson births at `ThermalRate`, each a copy of a
  recorded `ThermalLife` drawn evenly (root ±40 m, the rest ±10%); climb by `ThermalCycle` with 60 s fades; Gaussian core
  (half at the radius) plus a skirt to 3 radii whose strength (`Skirt`) matches the run's mean minus base at 300 m above
  the ground; cut lives stretched so rate × mean life = the run's mean number alive (`LifeStretch`); drift only as far as
  the template moved while seen; cumulus with `CloudChance` (formation rate as the run's over thermals), the life,
  depth, water, drift and `CloudShape` of a fed cloud drawn evenly from those within 1.5 km of the template's root (an
  even draw from all put a ridge's km-wide banks over lakes) (`LiveCloud.Columns()`: the recorded columns that are
  cloudy at its life share, tops and water by `CloudCycle`; drawn as column boxes like the film; old files without shapes
  give domes ≤ 2.5 × the thermal's width). The census records a shape per cloud family: per column around the family's
  water-weighted center (snapped once: rounding each column's offset half-to-even dropped every other row) the first and
  last time cloudy, lowest/highest level, most water. Births in order and
  closed-form positions keep it deterministic whatever the `Advance` steps. Measured lessons, don't undo: copying each
  thermal's linked cloud made km-wide clouds (families of merged cumulus) and too many (a cloud fed by many thermals
  copied per thermal; linked clouds are biased to the long-lived fields); a cloud living as long as its long thermal
  tripled the clouds about; the cores alone carry a sixth of the run's lift above the base flow (the sky sank 0.3 m/s);
  stretched plumes drifting on left the terrain. Interlaken: 40 thermals about (run 48), 4 cumulus (run ~5 over
  thermals), mean w at 300 m 0.00 (run 0.05); a million `WindAt` 0.3 s. `ConvectionModelTests` pin the rate, the number
  about, the lift's shape, determinism, the cumulus' life and the skirt's calibration.
- **Field**: `AirflowField` (cell-centered 3D fields in game order `(j * Nz + k) * Nx + i`, maps, thermals, triggers,
  `Info`; `Sample`, `GroundAt`; a snapshot's clouds graded by the cloud water), `AirflowFile` (PTAF + JSON header + zlib
  arrays, int16/uint8 quantized), `Streamlines` (midpoint rule through the mean wind).
- **Module**: `AirflowNode` (under a `TerrainNode` only: `TerrainNode.ChildTypes` and `ContainerNode.PlacedTypes`;
  settings as [Inspectable] properties; `Detail` writes the cell size, times and top clearance from the terrain's
  extent, editing them makes it Custom; `IJsonOnDeserializing`/`OnDeserialized` keep the saved values while loading and
  mark the saved `Result` current; `Result` is saved through `AirflowFieldJsonConverter` as the file's bytes; detail
  Thermals is 200 cells across, 90 min, the last 60 averaged, 3.5 km above the terrain, `TrackConvection` and inflow
  noise for cells ≤ 75 m; `CloudShadows` on by default; `Film` is kept with the result while the app runs, `SetResult`
  sets both, a new `Result` drops it; "Over time" properties sum up the census), `NewAirflowOptions`/`NewAirflowView`
  (sounding cards with their expected thermal top at the chosen time, compass, detail cards whose estimates know the
  graphics card once `AirflowGpu.IsKnownAvailable`), `AirflowPreview` (the terrain via `TerrainModule.ModelAsync`, the
  run in the background with live snapshots every 3 s and a film frame every 30 s of simulated time, overlay options;
  over time (`OverTime`, on by default) the film's frame replaces the mean's slice (lift only), clouds and thermal
  columns: `FilmScene` builds the lift slice, the clouds as column boxes from base to top, the shadows and the census'
  thermals alive then (`ConvectionGeometry.Alive`: root + drift × age, leaning) off the UI thread; live, the view
  follows the newest frame unless scrubbed back; `Seek`/`Advance` for the timeline; group "Airflow view": R, L, P, H,
  V, T, C, G, U, B, N, Page Up/Down, O, M, Space, Home, Comma, Period, Minus, Equal; B shows the result's base flow;
  M the game: a `ConvectionModel` of the result played live (`GameSpeed` ×1–×120, Space/Home/−/= act on it while it
  shows; Home a new seed), exclusive with over time, the base flow under it, particles riding `model.WindAt` through
  `AirflowParticles.Wind`; `GameScene` draws its thermals as leaning tubes, cumulus as domes with puffs, their shadows
  along the sun, and the slice (the base flow's lift prepared once per height, the thermals' added only within their
  reach) on the UI thread ten times a second),
  `SunMarker` (the sun disc and its rays), `AirflowScene` (overlay geometry built off the UI thread: streamlines, slice,
  section, thermal tubes or the census' sites (`ConvectionGeometry.Sites`), cloud boxes, ground map, trigger pins;
  translucent overlays use a base alpha of 254 to go into the transparent pass, vertex alpha fades), `AirflowParticles`
  (3000 trails, 60× the wind, a trail point every 0.08 s; they start in a band around the slice's height, 1–10 m up at
  0, and around the camera's target as far as 1.2 × its distance, starting again beyond twice that, so a close look is
  dense; heights above the ground count from `AirflowScene.AirGround`: the field's smoothed columns stand above the
  terrain at a cliff's foot, and the wind below them is still; slices are drawn at least `SliceClearance` up),
  `AirflowColors` (the scales and legends), `AirflowActions`
  (export).

## Feature Detector

Features of a terrain for a game to place models by: trees and bushes, roads and paths (a network), buildings,
boulders, areas, lines (waterways, power lines, aerialways, railways, barriers, cliffs), landmarks, a ground cover map;
from the images, the surface model and OpenStreetMap. Two parts: `src/ParagliderToolbox.Terrain/Features` (UI-free) and
`src/ParagliderToolbox/Modules/Features`. `docs/FeaturesFormat.md` documents the export for game developers; keep it in
sync with `FeatureFiles` and the detector.

- **Data**: the terrain's sources at the chosen pixel size (0.25/0.5/1 m): imagery, terrain model, and the surface
  models of `TerrainSources.Surface` (`SwissSurface3DSource`, swissSURFACE3D Raster 0.5 m; never used to build a
  terrain). Surface less terrain is the height of what stands on the ground. Blocks with only imagery coarser than 2 m
  (Sentinel-2) are classified but nothing is looked for in them.
- **Classes** (`GroundCovers.Classify`, per pixel): color, texture (brightness deviation within ~0.6 m), slope,
  altitude, object height and its roughness. Tall + green or rough: tree; tall, smooth, not green: building; low green:
  shrub; white above 1600 m: snow; flat bluish: water; green: grass; gray + rough or steep: rock; gray + smooth: paved
  (luminance ≤ 0.68) or gravel; beige gravel, brown soil, dark shadow.
- **`FeatureDetector`**: blocks of `BlockPixels` with a 32 m margin, a few at once, nearest the middle first; a feature
  belongs to the block its center is in; road pieces are clipped to the block's own part (Liang–Barsky) and joined
  across blocks (cut ends within 2 m, nearest pairs first; block nodes keep their ids) into chains between junctions
  (≥ 3 roads) or dead ends; the cover map (majority per cell, shadows filled from neighbors) and the areas
  (`Outline`: per kind a box-mean share > 0.5, opened for gravel/paved so roads aren't areas, holes filled, small parts
  dropped, `Polygons.Trace`) are made after all blocks. Buildings come first in a block: trees within 2 m of one are
  dropped (roof edges are as rough as crowns).
- **`TreeFinder`**: `Find` on the canopy height model (treetops: maxima within 0.6 m + 7% of the height, crowns a
  watershed to half the top; peakedness > 0.75 conifer); `FindInImage` without a surface model.
- **OpenStreetMap** (`FeatureSettings.Map`, default on): `OpenStreetMapSource` (an `IMapSource` in
  `TerrainSources.Maps`; one Overpass query (`OpenStreetMapSource.Query`: highways, buildings, landuse/natural ways and
  multipolygons, waterways, parking, power lines, aerialways, railways, barriers, and nodes of trees, rocks, peaks,
  flying sites, masts …; `out geom`) for the area's bounds rounded out to 0.01°, up to 0.1° per query, cached 30 days;
  an answer whose remark reports an error is forgotten) → `MapData` (nodes, ways, multipolygon relations) →
  `MapFeatures.Build` (`MapContent`: roads via `MapRoads.Build`; buildings by their center, outlines of closed ways and
  multipolygons (`Assemble` joins member ways into rings, holes by containment); areas by `AreaKindOf`, clipped
  (`Polygons.ClipRing`); lines by `LineKindOf`, clipped, densified to 5 m except wires (towers are the points); tree
  nodes and tree rows (every 8 m), stones, landmarks by `LandmarkKindOf`; tags kept but source/note/fixme/addr …).
  The blocks give every map point the ground's height (`Probes`; roads also tally the images' covers for their surface)
  and measure the map's buildings whose centers they hold (`Polygons.Scan` of the footprint: lowest ground, 95th/15th
  percentile of what stands there). Then: `Finish` (roads: junctions where ≥ 2 ends meet, bridges/tunnels straight
  between their ends; buildings: measured else `BuildingHeights` from height/levels/roof:shape/class), detected
  buildings kept where `Unmapped` (not inside, along or beside mapped ones, not on roads, railways or water), `MapMask` drops image trees on mapped roofs and image boulders on roofs,
  roads and parking (without a surface model the images take roofs and cars for them), `Merge` adds map trees and
  stones where none was found near and gives image trees the `leaf_type` of the map forest they stand in, `Burn` paints
  the map's water, glaciers, buildings and roads into the cover map, `Outline` adds the images' areas only where the
  map has none of the kind within 5 m (scree and rock count for the images' rock, gravel and paved).
  `RoadSource` (OpenStreetMap default, Images, Both: traced roads ≥ 70% farther than 5 m from the map's) for roads.
  Unreachable map: the images, with a note. Measured: overpass-api.de answers in ~30 s whatever the query's size
  (Interlaken 2 × 2 km, everything: 2879 buildings, 2254 roads, 391 lines, 181 landmarks, 38 s cold), so one query per
  area; the public mirrors tried (private.coffee, kumi, mail.ru) timed out or refused. OSM is ODbL: the credit and the
  share-alike note go into the notes, the property's description and the docs.
- **`RoadFinder`** (roads from the images): road-like pixels at 0.5 m, edge pixels classified rock taken back (their texture sees the meadow),
  closed, holes < 40 m² filled, thinned (`Skeletons.Thin`), `SkeletonGraph` (nodes at ends and junction clusters,
  spurs pruned against the half width at their junction), filters, components < 15 m and short asphalt (< 80 m) away
  from buildings dropped; width = (2 × median distance − 1 px); kind by surface and width (`RoadFeature.KindOf`).
- **Polygons**: outlines along pixel edges (8-connected parts, right turn at diagonal corners), holes assigned by a probe
  pixel, `SimplifyRing`, `MinimumRectangle` (rotating calipers); buildings filling > 85% of it are squared to it.
- **Files** (`FeatureFiles`): `features.json` (Utf8JsonWriter, rounded), `landcover.png` (8-bit gray class numbers,
  `Heightmaps.ToPng(byte…)`), colors PNG, GeoJSON (WGS84), ATTRIBUTION; `ToBytes`/`FromBytes` (gzip, cover map inside)
  is what the project keeps (`FeatureSetJsonConverter`).
- **Module**: `FeaturesNode` (under a `TerrainNode` only; area center as pasted coordinates, empty for the terrain's
  center, and size, clamped into the terrain; detail; what to look for, roads from; outlook; result summaries;
  `IsOutdated` by `SettingsVersion`), `FeaturesPreview` (terrain via `TerrainModule.ModelAsync`, detection in the background, overlays
  built off the UI thread), `FeaturesScene` (area outline, cones/diamonds for trees, road ribbons and junction points,
  boxes with gable or flat roofs, boulders, area outlines, the cover map as a vertex-colored grid; all on the shown
  terrain's heights; lines: wires with sag from tower tops, ribbons, standing fences; landmarks as pins),
  `FeaturesDetailView` (group "Features view": R, T, P, B, K, O, L, I, M), `FeaturesActions` (export to a folder).
- Measured lessons, don't undo: Swiss summer asphalt is light (village streets 0.63–0.66 luminance, limestone gravel
  roads 0.70–0.77), so the paved/gravel split is 0.68. Scree breaks into gray "roads": absolute texture doesn't carry
  over between imageries (NAIP is noisier), the ratio does: a line's texture over that of the bare ground within 10 m
  is 0.3–0.4 for roads (SWISSIMAGE), 0.45–0.65 (NAIP), 0.7–1.1 in scree. The terrain model's bumpiness and the surface
  model's roughness don't separate scree from trails (the lidar ground is smooth, and DSM = DTM in open terrain). In
  the image alone, crowns found as the darkest green sat on their cast shadows: crown pixels are darker than 0.85 × the
  open grass but brighter than 0.35 × it (shadows are darker and gray); radii are about half the lidar crowns' (× 1.8).
  Image-only trees match lidar trees within 3 m only a third of the time, but their number is right (3190 vs 3232).
  Roof edges classify as trees (rough): grow buildings into tall, not green pixels and keep trees 2 m away. Dark red and
  purple trees (copper beeches) aren't green and pass the roughness test: the roof flatness test
  (`MaxRoofCurvature`, median |Laplacian| of the top at 0.5 m: roofs 0.03–0.11, those trees 0.25–0.7) drops them;
  bridges and trains stand above the terrain model like roofs (dropped by the map's roads, railways and water). Of 181
  buildings found beside Interlaken's 2879 mapped ones, 64 were left, all real structures but a parasol; the check
  drew contact sheets of the found buildings over the image with the mapped outlines. Faint
  meadow trails remain undetected (a Hessian ridge filter was dominated by the mowing texture).
- How to judge changes: a scratch harness that draws the features over the aerial image (`FeatureFiles` export plus
  SkiaSharp) on a village (Beatenberg 46.6975, 7.7990), an alpine area with scree and gravel roads (First 46.6635,
  8.0560) and an image-only area (Mount Tamalpais 37.9235, −122.5965); and per road its texture, bare surroundings and
  ratio. `FeatureTests` pin a synthetic scene (no network): 49 trees, the road network across blocks, the building's
  rectangle, the boulder, the forest outline, the image-only fallback, the files; with a fake map (`FeatureScene.MapWays`)
  the map's network (splits, joins, cuts, surfaces from the images, a bridge), Both, an unreachable map, and the map's
  buildings (measured, from tags), multipolygon forest with a hole, water burned into the cover map, a power line, a
  culvert, landmarks, map trees, stones, image trees taking the map forest's leaf type (`FeatureScene.Map`).

## Adding a feature

1. Node class: derive from `ProjectNode` or `ContainerNode` (never from another `[Inspectable]` class), mark it
   `[Inspectable] partial`, raise change notifications with `SetProperty`, annotate with `[InspectableProperty]`
   (and `[MultilineText]` for string properties edited as text areas; see `ToolboxPropertyEditors`).
2. A module (`Modules/<Feature>/<Feature>Module.cs`) registering the node type (stable id!), its detail view, and
   any views, property editors, converters or menu items. Add it to `Program.Modules`.
3. Commands on the feature's view models with `[Command]`; wrap a detail view in a `KeybindingHandler` for its group
   so its shortcuts and palette entries work while it has the focus.
4. Tests in `tests/ParagliderToolbox.Tests` (`TestToolbox.Create()` gives an initialized toolbox with fake dialogs).

## Conventions

- Match Atelier's style: fluent markup, XML doc comments on public API, `Nullable` and `ImplicitUsings` enabled.
- Test names read as sentences (`Loading_LinksEveryNodeToItsParent`). Tests run serially (static keybinding registry).
