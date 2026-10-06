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
  "format": "ParagliderToolbox.Proxy", "formatVersion": 2,
  "coordinates": "glTF: meters, +Y up, +Z forward (flight direction), +X pilot's left",
  "complexity": "Medium",
  "nodes":       [ { "id": 0, "name": "S00_LE", "kind": "camber", "position": [x,y,z], "mass": 0.004, "section": 0, "chord": 0, "side": -1 }, ... ],
  "constraints": [ { "a": 0, "b": 1, "kind": "chordwise", "restLength": 0.05, "compliance": 2e-7, "compressionCompliance": 4e-4, "deflatedCompliance": 0.05,
                     "tensionOnly": false, "name": null, "dragDiameter": 0 }, ... ],
  "sections":    [ { "eta": -1, "chord": 0.73, "leadingEdge": 0, "trailingEdge": 9, "upper": [..], "lower": [..], "stations": [0.065, 0.13, ...] }, ... ],
  "strips":      [ { "sectionA": 0, "sectionB": 1, "area": 0.31, "hasInlet": false, "surface": [i, j, k, ...] }, ... ],
  "controls":    [ { "name": "CollapseLeft", "constraints": [..], "travel": [..], "strips": [..] }, ... ],
  "polar":       { "alphaDegrees": [-180 … 180], "lift": [..], "drag": [..], "moment": [..] },
  "pilot": 492, "pilotDragArea": 0.4, "lineDragCoefficient": 0.95,
  "trimAirspeed": 10.0, "trimFlightPathAngle": 6.0,
  "flatArea": 24, "projectedAspectRatio": 4.48, "inducedAspectRatio": 4.96, "spanEfficiency": 1,
  "internalPressureCoefficient": 0.75, "inletClosingAlpha": -1.5, "cellPressureTimeConstant": 0.6, "cellDeflationTimeConstant": 0.25
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
| `linePoint` | a point along a long line (main lines, and every brake line), so slack lines sag and bow |

`name` is also the joint name in the glTF skin. `section` and `chord` locate canopy nodes.

### Constraints

Distance constraints for an XPBD solver. `compliance` (m/N) applies when stretched, `compressionCompliance` when
compressed; **`tensionOnly`** constraints (lines, risers) are skipped while slack. `kind`:
`chordwise`, `spanwise`, `shear`, `rib` (across the profile, including the diagonal ribs), `bend`, `line`, `riser`,
`harness` (rigid pilot/carabiner/pulley frame; the maillons of a side), `hand` (the pilot's hand holding a toggle:
its distances to the pilot and both carabiners, which the brakes change). Lines carry `name` (e.g. "A main L1";
all segments of a line share it) and `dragDiameter`.

**`deflatedCompliance`** (canopy constraints of a double-surface proxy): an inflated cell holds its shape, an empty one
is limp fabric. The solver blends the compliance against compression (and for `bend` in both directions) from
`compressionCompliance` when the cells around the constraint are inflated to `deflatedCompliance` when they are empty
(see *Fabric firmness* below). 0 means it doesn't depend on the pressure (single-surface proxies, the leading edge rods).

### Sections and strips

A **section** is a profile at a rib, nodes from nose to tail: `leadingEdge`, `upper[k]`/`lower[k]` at `stations[k]`,
`trailingEdge`. A **strip** is the cell volume between two neighboring sections: `surface` lists its outward-facing
triangles (upper and lower skin, plus the end caps at the tips). Inner rib walls are omitted: the pressure on them
cancels between neighbors.

### Controls

Each control shortens its constraints' rest lengths by `travel[i] × input` (input 0…1; a negative travel lengthens),
never below 5% of the rest length:

- `BrakeLeft`, `BrakeRight`: the brake main lines (all their segments, by length; the travel includes the slack the
  lines have at rest) and the side's three `hand` constraints, which move the toggle from below the pulley down along
  the brake line to the hip.
- `SpeedBar`: shortens the front risers (A by the design's speed bar travel, the rows behind proportionally less, the
  last not at all) and lengthens the maillon links between them accordingly.
- `CollapseLeft`, `CollapseRight`: the A lines holding the outer half of the half span, each by the share of the
  canopy it holds there; `BigEarsLeft`, `BigEarsRight`: the same for the outer quarter; `Frontal`: all A lines.
- **`strips`**: the cells whose inlets the control closes, by its input. A pulled A line folds the leading edge under,
  which strip aerodynamics can't resolve; the cells it holds empty while it is pulled.

Weight shift lengthens one pilot–carabiner harness constraint and shortens the other (±12% in the reference simulator).

The reference simulator applies the inputs at **hand speed**: each follows the pilot's at up to 2.5 full travels per
second (letting go twice as fast), since hands and feet don't move instantly.

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
   `α = C < 0 ? compressionCompliance : compliance`, blended with `deflatedCompliance` by the fabric firmness (below);
   `α̃ = α / h²`; `λ = −C / (wₐ + w_b + α̃)`; `a += wₐ·λ·n`, `b −= w_b·λ·n` with `n = (a−b)/|a−b|`, `w = 1/m`.
4. **Keep the skins apart** (double-surface proxies): for every section and chord station, the upper node `U` must
   stay above the lower node `L` across the local fabric. The fabric normal is `n̂ = span × chord` from midpoints
   `(U+L)/2`: `chord` from the previous station (or the leading edge) to the next (or the trailing edge), `span` from
   the same station on the neighboring sections (the section itself at the tips), signed so the rest pose is
   positive. Skip the station when `|span × chord|` is below a quarter of its rest value (crumpled fabric: no reliable
   frame). If `g = (U − L)·n̂` is below 15% of its rest value, move `U` and `L` apart along `n̂` by the difference,
   weighted by `1/m`. Without this, limp empty cells let one skin pass through the other, and the pressure on the
   inverted pocket holds it there.
5. **Velocities**: `v = (x − x_prev)/h`; damp only the motion relative to the center of mass.
6. Keep the glider near the origin (shift all positions, accumulate the offset in double precision): far from the
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
- **Pitch damping**: the strip velocity is sampled at the center of pressure, so it doesn't see the strip rotating.
  Add the quasi-steady thin-airfoil damping `M = −π/16 · ρ·V·S·c² · ω` with `ω = ((v_LE − v_TE)·û)/c` (nose-up
  positive) and `S` the strip area, as a couple: `−M/(2c)·û` on each leading-edge node and `+M/(2c)·û` on each
  trailing-edge node (no net force; its power `−M·ω` is never positive). Without it a sudden brake input yanks the light
  canopy back into a stall.

### Cell pressure (ram air)

Each strip's pressure `p` (−0.25 sucked in … 1 inflated) moves toward a target: with `cellDeflationTimeConstant`
when it falls, and with `cellPressureTimeConstant · clamp(10 / V, 0.5, 3)` when it rises (the inlet takes in more air
faster). The target of a cell with an inlet (and V > 2 m/s) depends on the **inlet's angle of attack** `α_inlet`: the
strip's `α` corrected by how far the nose has turned against the rest of the profile (the direction from the leading
edge to the first nodes behind it, compared with the rest pose), so a nose folded under closes the inlet even when the
rest of the cell still looks like a profile:

| `α_inlet` | target |
|---|---|
| below `inletClosingAlpha − 3°` | −0.25 (flow over the nose: sucked empty) |
| up to `inletClosingAlpha` | ramps to 1 |
| up to 35° | 1 |
| 35° … 60° | ramps to 0.35 (deep stall: the flow from below meets the inlets side-on) |
| 60° … 110° | 0.35 |
| 110° … 150° | ramps to −0.25 (flow from behind) |

The target is scaled by how open the inlet is: the profile height just behind the nose relative to its rest height
(closed below 25%, open above 75%, smoothstep between). A control closing the strip (pulled A lines) blends the target
toward −0.25 by its input. Tip cells without an inlet fill from their neighbors; cross-vents even out neighbors
(exchange with a 0.8 s time constant). The pressure force on each `surface` triangle is
`p · internalPressureCoefficient · ½ρV² · (area-weighted outward normal)`, a third to each corner.

### Fabric firmness

Pressure is what makes fabric a wing. Per strip, every substep:

- `inflation = smoothstep((p − 0.15) / 0.7)`: limp below about a sixth of the full pressure, fully firm from 85%;
- `stiffening = clamp((½ρV² / ½ρV_trim²)^0.25, 0.5, 4)`: the internal pressure follows the dynamic pressure.

A canopy node takes the mean of the strips on either side of its section; a constraint the mean of its two nodes. The
compliance of a constraint with `deflatedCompliance` (against compression, and for `bend` in both directions) is then
`exp(f·ln(α/stiffening) + (1−f)·ln(deflatedCompliance))` with `f` = inflation: a geometric blend, so a half-inflated
cell is noticeably soft. Empty cells fold under the line and air loads: that is how tucks, collapses, big ears and the
horseshoe of a full stall happen, and how cells refill and reopen.

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
