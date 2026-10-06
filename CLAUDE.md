# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

Paraglider Toolbox: a desktop app (C#, .NET 9) with tools for paraglider simulation and games. The UI is built with
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
  node type gets a runtime-registered `Add<TypeId>` command. Commands use `IShellDialogs` (implemented by `MainView`,
  faked in tests) instead of UI code.
- **Views** (`Framework/Views`): `MainView` (title bar menu, `WorkspaceView`, dialogs), `ProjectExplorerView`
  (TreeView synced with `Toolbox.SelectedNode`), `PropertiesView` (PropertyGrid), `DetailView` (hosts the node's
  detail view). Views subscribe to the toolbox in `OnAttachedToVisualTree` and unsubscribe when detached. `MainView`
  handles the window's `Closing` event (title bar, Alt+F4, taskbar), deferring it to the unsaved-changes prompt;
  `IShellDialogs.CloseWindow` closes with `force: true` after the commands already asked.
- **Settings** (`Framework/Settings/AppSettings`): `%APPDATA%\ParagliderToolbox`: `settings.json` (theme, recent
  files), `keybindings.json` (command customizations), `workspaces.json` (layout).

## Paraglider Model Creator

Two parts: `src/ParagliderToolbox.Paraglider` (a UI-free library, also usable by a game) and
`src/ParagliderToolbox/Modules/Paraglider` (the toolbox module). `docs/ProxyFormat.md` documents the proxy JSON and the
simulation algorithm for game developers; keep it in sync with the simulator.

- **Design** (`Design/GliderDesign`): every parameter (meters, degrees, chord fractions, span fractions η). `Curve`
  (`Mathematics/Curve`, monotone cubic, immutable, JSON-serializable) for distributions.
- **Geometry**: `Airfoil` (NACA modified four-digit thickness + reflexed camber, or Selig/Lednicer .dat), `GliderShape`
  (planform, arc integrated from roll angles so arc length = flat span, washout, thickness, shark nose, rib layout,
  trim attitude; glTF frame: +Y up, +Z forward, +X pilot's left, carabiners at the origin; profile parameter
  t ∈ [−1, 1], x = 1 − cos(π|t|/2)), `CanopyBuilder` (skin grid with ballooning arcs, creases, wrinkles, pucker,
  inlets; ribs, mini-ribs, diagonal ribs, tip panels; displacements vanish on ribs so ribs close the cells),
  `MeshPart` (every vertex carries a `VertexBind` for skinning).
- **Rigging**: `RiggingLayout` (tabs, cascades, risers, brakes; straight lines), `RiggingBuilder` (tubes, straps, rings).
- **Texturing**: `CanopyTextureGenerator` paints base color + normal map in the canopy UV layout (u = (η+1)/2, v = (t+1)/2).
- **Proxy**: `ProxyBuilder` (sections on tab ribs, stations include the line rows, fabric/rib/bend/diagonal-rib
  constraints, tension-only lines, strips with outward pressure surfaces, controls, sampled `SectionPolar`),
  `SkinBinder` (≤ 4 joints per vertex), `ProxyDeformer` (node frames → linear blend skinning).
- **Simulation** (`GliderSimulator`): XPBD with forces recomputed every substep, one fixed-order Gauss–Seidel pass per
  substep (alternating the order destabilizes it), origin rebasing (float precision), load-weighted strip velocities
  (lift does no work), lifting-line induced angle by fixed-point iteration, flap lift for brakes, ram-air pressure cells.
  Tests in `SimulationTests` pin the flight envelope (trim, brakes, speed bar, stall, frontal recovery); rerun them
  after any physics change.
- **Export** (`GliderExporter`, `GltfWriter`): .glb (skinned, PBR, embedded PNGs, optional proxy cage and baked
  animation), proxy JSON, OBJ/MTL/PNG, line plan CSV. Validate .glb changes with the Khronos glTF validator and a
  Blender import (Blender 4.4 is installed: `blender -b --python script.py`).
- **Polar** (`Polar/PolarRecorder`): flies a fresh `GliderSimulator` headless at a fixed step through trim, the speed
  bar steps and the brake steps (stopping at the first stall), ramp → settle → measure per setting; deterministic, so
  two recordings of one design are identical (an optimizer can compare them). `Summarize` fits a cubic sink(v) through
  the steady points for min sink and best glide (tangent from the origin). `PolarRecording` keeps the design, the
  settings, every sample and the points; `PolarCsv` writes them. Tests in `PolarRecorderTests`.
- **Module**: `ParagliderNode` (a `ContainerNode` holding its `PolarNode`s; the parameters as [Inspectable] properties forwarding to a `GliderDesign`; colors saved as
  hex; `Snapshot()` for the generator thread), `ParagliderPreview` (debounced background generation, Scene3D, simulation
  loop driven by `Viewport3D.Rendered`), `ParagliderDetailView`, `ParagliderActions` (global export commands),
  `CurveEditor`/`CurvePropertyEditor`, `PolarDetailView` and `PolarChart` (plots a polar on Atelier.Charts' `XYChart`; also used live while recording). The 3D view is Atelier's `Atelier.Graphics3D.Viewport3D` (OpenGL on the window's
  context, composited into Skia).

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
