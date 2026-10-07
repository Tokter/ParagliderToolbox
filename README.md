# ParagliderToolbox

Tools for paraglider simulation and games, built with the [Atelier](https://github.com/Tokter/Atelier) UI framework.

![A generated paraglider rendered in Blender Cycles](docs/images/render-blue-side.jpg)

![The paraglider editor: project tree, 3D preview and design parameters](docs/images/ui-editor.png)

## The application shell

- **Project tree** (left): every object in the project. Right-click for Add, Rename, Duplicate, Delete and Move.
- **Detail view** (middle): the selected object's own UI.
- **Properties** (right): the selected object's properties.
- **Workspaces**: Blender-style areas that split, join and swap, each showing any of the views; workspaces in tabs.
  The layout is remembered.
- **Projects** are saved as JSON (`*.pgtproj`) from the File menu in the title bar, with recent files.
- **Command palette** (Ctrl+Shift+P) and **keyboard shortcuts editor** (Ctrl+K, Ctrl+S).

| Shortcut | Action |
|---|---|
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | New, open, save, save as |
| Ctrl+Shift+P | Command palette |
| Ctrl+K, Ctrl+S | Customize keyboard shortcuts |
| Ctrl+Shift+N | Add a folder |
| F2 / Ctrl+D | Rename / duplicate the selected object |
| Delete, Alt+Up, Alt+Down | Delete or move the selected object (in the project tree) |
| Ctrl+Space | Maximize the area under the pointer |
| F12 | Developer tools (Debug builds) |

## Paraglider Model Creator

Add a paraglider (Ctrl+Shift+G, or Edit > Add) and shape it in the properties; the detail view regenerates the model
as you edit (a few hundred milliseconds) and can fly it.

Adding a paraglider asks for a starting point:

- **Wing class**: EN-A (school, aspect ratio 4.9, 40 cells), low EN-B (5.3, 48 cells), high EN-B (5.9, 58 cells), EN-C
  (two-liner, 6.3, 66 cells) or EN-D (two-liner, 6.9, 78 cells). Each preset sets every design parameter after current
  production wings of its class: planform and arc, profile thickness, shark nose and reflex, line rows, cascades and
  diameters, brake travel, pilot harness drag.
- **Model detail**: Low poly (a few hundred triangles for games: skin panels spanning several cells, lines as ribbons,
  a 1024 texture), Medium (tens of thousands for real time) or High (hundreds of thousands for renders). It can be
  changed later (Mesh > Detail), and editing any mesh setting makes it Custom.

- **Design parameters**, in the order of the design steps:
  - **Planform**: flat area, aspect ratio and span, chord distribution curve, straight chord line, leading edge sweep.
  - **Arc**: tip angle or a projected span ratio to solve for, the arc distribution, tip cant, washout and its axis.
  - **Airfoil**: a parametric reflexed section (NACA modified four-digit thickness: leading edge radius, maximum
    thickness position; camber, its position, reflex) or imported Selig/Lednicer `.dat` coordinates; root and tip
    thickness with a distribution curve.
  - **Inlet**: start and end, rim sag, shark nose, leading edge rods.
  - **Cells**: cell count, width distribution, mini-ribs, cross-vents, diagonal ribs.
  - **Ballooning**: amount and chordwise distribution, lower surface factor, seam creases, tab wrinkles, trailing edge
    gathering.
  - **Rigging**: 2, 3 or 4 rows and their positions, tab and brake tab spacing, cascades, line diameters, carabiner
    position and spacing, risers, brake slack and travel, trim.
  - **Appearance**: pattern, colors, brand text, texture size, fabric translucency.
  - **Physics proxy**: complexity from Arcade to High, or custom settings; masses and drag.
  - **Mesh**: detail (Low poly, Medium, High or Custom), resolution, cells per segment, ribs, rigging, line and hardware detail.
- Distributions are curves: drag their points in the curve editor (Edit… next to the curve).
- **High resolution model** (about 240k triangles by default): ballooned skin with seam creases, wrinkles and a gathered
  trailing edge, air inlets with sagging rims, internal ribs with cross-vents, mini-ribs and diagonal ribs, tip panels,
  the line cascades as tubes in their row colors, risers, maillons, brake pulleys and toggles, carabiners; procedural
  base color and normal map textures (pattern, panel seams, stitching, tapes, ripstop, brand text).
- **Physics proxy**: mass points on the upper and lower surface (or the camber surface), fabric, rib and bending
  constraints, tension-only lines that sag and bow when slack, risers, the pilot and the pilot's hands on the brake
  toggles, aerodynamic strips with ram-air pressure cells, the section polar and the pilot's controls. The high
  resolution model is skinned to it.
- **Simulation** in the detail view (P): trim glide, brakes (the toggles move down to the hips), speed bar, weight
  shift, asymmetric and frontal collapses, big ears, gusts, stalls and spins, with the high resolution model following
  the proxy. The canopy is fabric held in shape by its pressure: empty cells go limp, so collapses fold the canopy under,
  big ears fold the tips, a frontal folds the span into a horseshoe and a full stall crumples it; it reopens as the cells
  refill. The **Forces** view (5) draws the lift, canopy drag, line drag and pilot drag as arrows from where the
  simulation applies them, as long as they are strong: per node, or summed per strip at its center of pressure; with
  the totals and the force balance's L/D. It also shows the load the pilot hangs in the harness with (weight and
  inertia: down, and out of a turn), and the telemetry shows the G load.
- **Recorded flights** (Record, Ctrl+R): stopping a recording adds it under the paraglider, like a polar. Select it to
  replay it: play, pause, scrub the timeline, step frame by frame, slow it down to a tenth, and orbit and zoom around the
  glider meanwhile, with the flight data of every moment (airspeed, vario, G, angle of attack, deflated cells, the
  pilot's inputs). It keeps the design it was flown with (the paraglider can be edited on), the proxy's motion (30 frames
  a second, about 2.5 MB a minute in the project for a Medium proxy) and the pilot's inputs from the start of the flight,
  so the simulator can fly it again exactly: save the project to share something strange. Export it as a glTF animation.

  | Simulation: an asymmetric collapse | The physics proxy the high resolution model is skinned to |
  |---|---|
  | ![Simulation of an asymmetric collapse](docs/images/ui-simulation.png) | ![The physics proxy](docs/images/ui-proxy.png) |
- **Polar recorder** (Polar button, Ctrl+P): flies the proxy headless and deterministically through the speed range
  (trim, the speed bar steps, then symmetric brake steps until it stalls), settling and then measuring each setting,
  and plots the samples live. A dialog sets it up first: the number of speed bar and brake steps (4 and 10 by default:
  25 % and 10 % apart), the maximum brake, start, ramp, settle and measure times, samples per second, and the stall and
  steadiness thresholds, with the resulting flight time. Sink rates are total-energy compensated, like a TE vario. The
  result is stored as a polar under the paraglider (with the
  design and settings it was recorded with and every sample) and shown on a zoomable chart with trim, full speed, best
  glide (tangent from the origin to the fitted curve), min sink, min speed, max sink and the stall labeled, plus a table of every setting.
  Export it as CSV. `PolarRecorder` is UI-free, the building block for an optimizer that tweaks parameters and
  compares polars.

  ![A recorded polar with its key figures labeled](docs/images/ui-polar.png)
- **Exports** (File > Export paraglider, Ctrl+E, or the Tools menu): glTF binary with the skinned model, materials and
  textures (Blender, Godot), the proxy alone as glTF, the proxy JSON for a game ([format and algorithm](docs/ProxyFormat.md)),
  OBJ + MTL + PNG, and a line plan CSV.

| In the detail view | Action |
|---|---|
| Middle drag / Shift+middle drag / wheel | Orbit / pan / zoom |
| Home, numpad 1 3 7, numpad 5, Shift+Z | Frame, views, orthographic, wireframe |
| 1 2 3 4 | Show canopy, rigging, proxy, ribs |
| 5, 6, D, - and = | Show the aerodynamic forces; per node or per strip; drag ×5; shorter and longer arrows |
| P / R | Simulate or pause / reset |
| Q, E, F, B, G | Collapse left, right, frontal, big ears, gust |
| Ctrl+R | Record the flight (again to stop: it is added under the paraglider) |
| Ctrl+P | Record the polar (again to cancel) |
| Game controller (Xbox) | Left and right trigger: brakes; left stick: weight shift (left/right) and speed bar (forward); A: simulate or pause; left and right bumper: collapse left and right |
| In a recorded flight: P / R, comma and period, - and = | Play or pause / restart, step a frame back and on, slower and faster |

### Renders

Exported glTF models rendered in Blender 4.4 with Cycles (sky, procedural terrain and lighting added in Blender;
the canopy, ribs, rigging, textures and normal maps come straight from the export). `docs/images/render.py` renders
them again: `blender -b --python docs/images/render.py -- model.glb out hero,side,backlit,nose 256 1`.

| | |
|---|---|
| ![Front three-quarter view](docs/images/render-blue-hero.jpg) | ![Stripes pattern in evening light](docs/images/render-sunset-evening.jpg) |
| ![Backlit from behind, seen from below](docs/images/render-blue-backlit.jpg) | ![Leading edge: inlets, shark nose and ballooned cells](docs/images/render-blue-nose.jpg) |

## Building

Atelier is referenced as source and expected next to this repository:

```
D:\GitHub\Atelier
D:\GitHub\ParagliderToolbox
```

```bash
dotnet build ParagliderToolbox.slnx
dotnet test
dotnet run --project src/ParagliderToolbox
```

Settings (theme, recent files, shortcuts, layout) are kept in `%APPDATA%\ParagliderToolbox`.

## Extending

Features are modules (`IToolboxModule`) that register node types, detail views, workspace views, property editors,
JSON converters and menu items with the `Toolbox`. See [CLAUDE.md](CLAUDE.md) for the architecture.
