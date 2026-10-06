# Paraglider proxy format and simulation

The Paraglider Model Creator exports a low resolution **physics proxy** of each glider (`<name>_proxy.json`) and a
high resolution glTF model (`<name>.glb`) whose mesh is skinned to a skeleton with **one joint per proxy node**. A game
simulates the proxy and moves the joints; the high resolution canopy, ribs, lines and risers follow.

The reference simulator is `ParagliderToolbox.Paraglider.Simulation.GliderSimulator` (C#, no dependencies besides
.NET); port it or load the library directly. Everything below describes what it does.

## Conventions

- glTF coordinates: meters, **+Y up, +Z forward** (flight direction), **+X to the pilot's left**. Kilograms, seconds.
- In the rest pose the carabiners' midpoint is at the origin and the canopy flies above in its trim attitude.
- The JSON uses camelCase names and lowercase enum strings; vectors are `[x, y, z]`.

## File

```jsonc
{
  "format": "ParagliderToolbox.Proxy", "formatVersion": 1,
  "coordinates": "glTF: meters, +Y up, +Z forward (flight direction), +X pilot's left",
  "complexity": "Medium",
  "nodes":       [ { "id": 0, "name": "S00_LE", "kind": "camber", "position": [x,y,z], "mass": 0.004, "section": 0, "chord": 0, "side": -1 }, ... ],
  "constraints": [ { "a": 0, "b": 1, "kind": "chordwise", "restLength": 0.05, "compliance": 2e-7, "compressionCompliance": 4e-4,
                     "tensionOnly": false, "name": null, "dragDiameter": 0 }, ... ],
  "sections":    [ { "eta": -1, "chord": 0.73, "leadingEdge": 0, "trailingEdge": 9, "upper": [..], "lower": [..], "stations": [0.065, 0.13, ...] }, ... ],
  "strips":      [ { "sectionA": 0, "sectionB": 1, "area": 0.31, "hasInlet": false, "surface": [i, j, k, ...] }, ... ],
  "controls":    [ { "name": "BrakeLeft", "constraints": [..], "travel": [..] }, ... ],
  "polar":       { "alphaDegrees": [-180 … 180], "lift": [..], "drag": [..], "moment": [..] },
  "pilot": 492, "pilotDragArea": 0.4, "lineDragCoefficient": 0.95,
  "trimAirspeed": 10.0, "trimFlightPathAngle": 6.0,
  "flatArea": 24, "projectedAspectRatio": 4.48, "inducedAspectRatio": 4.96, "spanEfficiency": 1,
  "internalPressureCoefficient": 0.75, "inletClosingAlpha": -1.5, "cellPressureTimeConstant": 0.6
}
```

### Nodes

| kind | meaning |
|---|---|
| `upper`, `lower` | canopy surface points (upper/lower skin) of a section at a chord station |
| `camber` | the shared nose and trailing edge points; all surface points of a single-surface (arcade) proxy |
| `knot` | a line cascade knot |
| `riserTop` | a riser's maillon, where the main lines attach |
| `carabiner`, `pulley`, `toggle` | the harness connection, the brake pulley, the brake handle |
| `pilot` | pilot and harness (the heavy node) |

`name` is also the joint name in the glTF skin. `section` and `chord` locate canopy nodes.

### Constraints

Distance constraints for an XPBD solver. `compliance` (m/N) applies when stretched, `compressionCompliance` when
compressed; **`tensionOnly`** constraints (lines, risers) are skipped while slack. `kind`:
`chordwise`, `spanwise`, `shear`, `rib` (across the profile, including the diagonal ribs), `bend`, `line`, `riser`,
`harness` (rigid pilot/carabiner/pulley frame). Lines carry `name` (e.g. "A main L1") and `dragDiameter`.

### Sections and strips

A **section** is a profile at a rib, nodes from nose to tail: `leadingEdge`, `upper[k]`/`lower[k]` at `stations[k]`,
`trailingEdge`. A **strip** is the cell volume between two neighboring sections: `surface` lists its outward-facing
triangles (upper and lower skin, plus the end caps at the tips). Inner rib walls are omitted: the pressure on them
cancels between neighbors.

### Controls

Each control shortens its constraints' rest lengths by `travel[i] × input` (input 0…1):
`BrakeLeft`, `BrakeRight` (brake lines at the pulley, including the slack), `SpeedBar` (A/B risers),
`CollapseLeft`, `CollapseRight` (outer A lines: asymmetric collapse), `Frontal` (all A lines), `BigEarsLeft`,
`BigEarsRight` (outermost A lines). Weight shift lengthens one pilot–carabiner harness constraint and shortens the
other (±12% in the reference simulator).

## Simulation algorithm

Per frame (e.g. 1/60 s), `substeps` times (16 for Medium; more for High):

1. **Forces** (recomputed every substep: the aerodynamic damping of the light canopy nodes is too stiff for longer steps)
   - gravity `m·g`;
   - line drag: for each line/riser, the air velocity normal to the line, `F = ½ρ|vₙ|vₙ · Cd · dragDiameter · length`,
     half to each end;
   - pilot drag `½ρ|v|v · pilotDragArea`;
   - **strip aerodynamics** (below) and **cell pressure** (below).
2. **Integrate**: `v += F/m · h`, `x_prev = x`, `x += v · h`.
3. **Solve** each constraint once, in a fixed order (alternating the order every substep makes stiff networks
   oscillate): `C = |a−b| − rest`; skip if `C < 0` and tension-only;
   `α̃ = (C < 0 ? compressionCompliance : compliance) / h²`; `λ = −C / (wₐ + w_b + α̃)`;
   `a += wₐ·λ·n`, `b −= w_b·λ·n` with `n = (a−b)/|a−b|`, `w = 1/m`.
4. **Velocities**: `v = (x − x_prev)/h`; damp only the motion relative to the center of mass.
5. Keep the glider near the origin (shift all positions, accumulate the offset in double precision): far from the
   origin floats lose the precision the stiff constraints need and the solver gains energy.

Start each flight with every node moving at `trimAirspeed` along a path `trimFlightPathAngle` below the horizon.

### Strip aerodynamics

For each strip (sections A and B):

- nose `LE` and tail `TE` = averages of the two sections' nodes; span direction `ŝ` from A's to B's quarter chord;
  chord direction `ĉ` = `TE − LE` without its span component; `û = ŝ × ĉ` (upper side; sections run right to left).
- **Load weights** `wₖ` over the strip's nodes: each node's share of the strip area, tilted linearly along the chord so
  the weighted centroid is the strip's center of pressure (start at 0.3). The strip velocity is the **weighted average**
  of its nodes' velocities with the same weights; this way lift does no work on the strip's own rotation.
- `air = wind − v`; `flow` = air without its span component; `V = |flow|`; `α = atan2(flow·û, flow·ĉ)`.
- Flap (brakes): angle between the front (nose → 60% chord node) and rear (60% → tail) chord segments, minus its rest
  value: `δ`.
- Induced angle by a few damped fixed-point iterations: `αᵢ ← ½αᵢ + ½·(Cl(α−αᵢ) + ΔCl_flap) / (π·AR·e)` with
  `AR = inducedAspectRatio`, `e = spanEfficiency`.
- Coefficients from the **sampled polar** at `α − αᵢ` (linear interpolation); add the flap:
  `ΔCl = slope · 0.5 · δ · attached` (attached = `1/(1+exp((α − α_stall)/2.5))`), `Cm −= 0.3·ΔCl`,
  `Cd += 0.6·δ² + Cl·αᵢ`.
- A deflated cell is a crumpled bag: `Cl *= 0.25 + 0.75·p`, `Cd += 0.3·(1 − p)` with `p` = its pressure clamped to 0…1.
- `F = ½ρV² · |ĉ chord| · |span| · (Cl·L̂ + Cd·flow/V)` with `L̂` = `û` made perpendicular to the flow; spread with the
  load weights. Next center of pressure: `0.25 − Cm/Cl` (clamped 0.05…0.9), smoothed.

### Cell pressure (ram air)

Each strip's pressure `p` (−0.25 sucked in … 1 inflated) moves toward a target with `cellPressureTimeConstant`:
target 1 while the strip has an inlet and `inletClosingAlpha < α < 100°` (and V > 2 m/s), otherwise −0.25; tip
cells without an inlet fill from their neighbors; cross-vents even out neighbors (exchange with a 0.8 s time
constant). The pressure force on each `surface` triangle is
`p · internalPressureCoefficient · ½ρV² · (area-weighted outward normal)`, a third to each corner. Without pressure the
cell folds under the line and air loads: that is how tucks and collapses happen, and how cells refill and reopen.

## Driving the high resolution model

The `.glb` contains the high resolution parts skinned to joints named after the proxy nodes (up to four weights per
vertex; canopy vertices follow the two sections and two chord stations around them). Each joint's rest transform is the
node's **frame**: its position, and for canopy nodes the axes from its chord and span neighbors (`x` along the chord,
`z = x × span`, `y = z × x`); other nodes only translate. To pose the model, compute each node's current frame the same
way and set the joint's world transform to it (`ProxyDeformer.Frame` in the reference code).

- **Godot**: import the `.glb`; the joints become `Skeleton3D` bones in the proxy's node order. Set each bone's global
  pose from the simulated frames every physics frame.
- **Blender**: import the `.glb`; the armature has one bone per proxy node and the canopy is parented with weights.
  The toolbox can record a simulated flight and bake it into the joints (the preview's Record and Export recording),
  which Blender imports as an action.

The simulation proxy JSON and the `.glb` must come from the same design (same node count and order).
