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
  `SubtreeChanged`; `ProjectDocument` uses that for `IsModified`. `NodeTypeRegistry` maps each node class to a stable
  type id (saved as `$type`), display name, icon and category.
- **Serialization** (`Framework/Serialization/ProjectSerializer`): System.Text.Json with a contract modifier. Saved:
  public get/set properties (camelCase) and `Children`; get-only properties are dropped automatically; `[JsonIgnore]`
  for the rest. Children are polymorphic through the registry's type ids. File = `{ format, version, project }`;
  upgrade older formats in `Deserialize`. `Clone` duplicates subtrees with new ids.
- **Toolbox** (`Framework/Modules/Toolbox`): shared state (open `Document`, `SelectedNode`) and the extension points
  (also `GlobalCommands` for command groups active anywhere, `PropertyCategoryOrder`)
  modules fill in `IToolboxModule.Register`: `NodeTypes`, `Views` (Atelier `AreaEditorRegistry`: the views workspace
  areas can show), `DetailViews` (object specific UI per node class, resolved by base type), `PropertyEditors`
  (custom `PropertyGrid` editors), `JsonConverters`, `Menus` (main menu items), `DefaultWorkspaces`,
  `WorkspaceTemplates`. Modules are listed in `Program.Modules`.
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
  `PolarRecorder` shows the polar effect. For visual checks of shapes, a headless renderer of the proxy (orthographic
  wire or shaded views over time) is far quicker than the app.
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
  `AfterStep` around every `GliderSimulator.Step`): the inputs of every step from the start (as changes), and while
  recording frames every second step (the proxy's node positions relative to where the recording started, 1 mm int16
  offsets from the pilot, each the difference to the frame before, deflated, base64; plus `FlightTelemetry`).
  `FlightRecording` keeps the design, the inputs and the frames; `Decode()` gives the frames. To examine a shared one:
  load the project file, take the `RecordingNode`'s recording, generate its design's proxy, and fly a new simulator with
  `InputsAt(step)` for every step: it reproduces the frames (deterministic; `FlightRecordingTests`).
- **Module**: `ParagliderNode` (a `ContainerNode` holding its `PolarNode`s and `RecordingNode`s; the parameters as [Inspectable] properties forwarding to a `GliderDesign`; colors saved as
  hex; `Snapshot()` for the generator thread; `MeshDetail` writes its settings into the explicit mesh properties, and
  editing one of those switches to Custom; `MeshDetail` is saved after them, `[JsonPropertyOrder(1)]`, so loading
  doesn't switch), `NewParagliderOptions`/`NewParagliderView` (the new-paraglider dialog: class cards, detail segments,
  triangle counts measured in the background; `ParagliderModule.AskNewParagliderAsync`), `ParagliderPreview` (debounced background generation, Scene3D, simulation
  loop driven by `Viewport3D.Rendered`; the Forces view draws `GliderSimulator.RecordForces`' per-node and per-strip
  lift and drag as one vertex-colored line mesh; Record adds a `RecordingNode` when stopped, without selecting it),
  `GliderScene` (the model's parts and materials skinned to the proxy, the proxy meshes, the part toggles; shared by the
  preview and the replay), `RecordingPlayer`/`RecordingDetailView` (the replay: generates the recorded design, plays the
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
  350 MB). `SwissAlti3DSource`/`SwissImageSource` (`SwissTopoCatalog`: the STAC API v1 in 8 km cells, the newest
  version per 1 km tile; the asset by gsd, then the overview by spacing; the 2 m files have only 2 and 4 m images),
  `SwissAltiRegioSource` (one BigTIFF COG, 10 m with overviews, Switzerland and ≥ 100 km around it, LV95; credits
  the neighbors' data), `CopernicusDemSource` (AWS bucket, 1° COGs; missing
  tiles are sea at 0), `Sentinel2CloudlessSource` (EOX's 2016 layer, the only CC BY one; `WebTileLevel`). Measured:
  Copernicus is a surface model, median 1–4 m and in forests 15–25 m above swissALTI3D.
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
  30 ms budget per frame, half-texel UV inset because the viewport's textures repeat), `TerrainDetailView`,
  `TerrainActions` (exports reuse the preview's model when its settings match, else build; clear the cache),
  `TerrainFlight` (WASD/E/Q fly the orbit camera, target and all, at 0.8 × the height above the ground per second,
  Shift ×4, eased, above the ground; right drag looks around in place). The fly keys are commands of the "Terrain
  flight" group (rebindable; run from the palette they step a second's worth): the detail view takes the keys bound
  to them in `OnPreviewKeyDown` while held and releases them when the focus leaves (GLFW releases keys on window
  deactivation). Posted window messages don't reach GLFW: drive the app with real input (keybd_event).
  The cache is `%LOCALAPPDATA%\ParagliderToolbox\TerrainCache` (`PARAGLIDERTOOLBOX_TERRAIN_CACHE`). Tests use fake
  sources and synthetic TIFFs (`TestTiff`, with an LZW encoder as libtiff writes it); nothing in the tests uses the
  network.

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
