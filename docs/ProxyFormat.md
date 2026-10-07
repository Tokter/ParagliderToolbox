# Paraglider proxy format and simulation

The Paraglider Model Creator exports a low resolution **physics proxy** of each glider (`<name>_proxy.json`) and a
high resolution glTF model (`<name>.glb`) whose mesh is skinned to a skeleton with **one joint per proxy node** (plus
translation-only attachment joints for the lines, see below). A game simulates the proxy and moves the joints; the high
resolution canopy, ribs, lines and risers follow.

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
In the trailing edge bay (behind the last line row, and at least the rear 40% of the chord) `compressionCompliance` is
soft even for inflated cells (0.005): the thin rear of the profile wrinkles, so the brakes curl the trailing edge down
(camber) instead of pitching the whole profile up. (Softer still, a collapsed wing tumbled instead of reopening.)

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

Per frame (e.g. 1/60 s), `substeps` times: 16, times the pilot's load factor `n` when it is above 1 (at most 64).
`n = |g − a| / |g|`, with `a` the pilot's acceleration over the previous frames (smoothed with a 0.3 s time constant).
One constraint pass per substep resolves a heavily loaded line set too softly: with a fixed 16, a spiral dive snapped
the outer tip once it pulled about 2 G and flipped into a reversal or a spin. Straight flight keeps 16 (and the
calibrated polar).

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
6. **Keep the glider at the origin**: after every step, shift all positions by whole meters (exact in floats) so the
   middle of the system (between the pilot and the canopy) stays within a meter of the origin, and accumulate the
   offset in double precision. Float precision matters even a few meters out: allowing 16 m, rounding in the stiff
   constraints biased the sink rate by ±10%, wandering over tens of seconds and depending on the flight direction.

Start each flight with every node moving at `trimAirspeed` along a path `trimFlightPathAngle` below the horizon.

### Strip aerodynamics

For each strip (sections A and B):

- nose `LE` and tail `TE` = averages of the two sections' nodes; span direction `ŝ` from A's to B's quarter chord;
  chord direction `ĉ` = `TE − LE` without its span component; `û = ŝ × ĉ` (upper side; sections run right to left).
- **Load weights** `wₖ` over the strip's nodes: each node's share of the strip area, tilted linearly along the chord so
  the weighted centroid is the strip's center of pressure (start at 0.3). The strip velocity is the **weighted average**
  of its nodes' velocities with the same weights; this way lift does no work on the strip's own rotation.
- `air = wind − v`; `flow` = air without its span component; `V = |flow|`; `α_chord = atan2(flow·û, flow·ĉ)`.
- Flap (brakes): angle between the front (nose → 60% chord node) and rear (60% → tail) chord segments, minus its rest
  value: `δ`.
- **Angle of attack against the front of the profile**: `α = α_chord − (φ − φ_rest)` with `φ` the angle of the front
  segment (nose → 60% node) in the (`ĉ`, `û`) frame. A trailing edge pulled down turns the chord but not the front, so
  the deflection counts once, as the flap, not also as angle of attack. (The inlet below uses `α_chord`.)
- Induced angle by a few damped fixed-point iterations: `αᵢ ← ½αᵢ + ½·(Cl(αₑ) + ΔCl_flap) / (π·AR·e)` with
  `AR = inducedAspectRatio`, `e = spanEfficiency` and the effective angle `αₑ = α − αᵢ + 0.15·δ`: a braked profile
  stalls earlier against its front (part of the deflection acts like angle of attack).
- Coefficients from the **sampled polar** at `αₑ` (linear interpolation); add the flap's camber lift:
  `ΔCl = slope · 0.4 · δ · attached` (attached = `1/(1+exp((αₑ − α_stall)/2.5))`; it raises the maximum lift),
  `Cm −= 0.07·ΔCl` (a flap of the rear 38% lifts at about 0.32 chord), `Cd += 0.4·δ² + Cl·αᵢ`. Calibrated so each
  class stalls with both brakes at about 80% of its travel (EN-A 64 cm … EN-D 48 cm) at 22–27 km/h.
- A deflated cell is a crumpled bag: `Cl *= 0.25 + 0.75·p`, `Cd += 0.3·(1 − p)` with `p` = its pressure clamped to 0…1.
- `F = ½ρV² · |ĉ chord| · |span| · (Cl·L̂ + Cd·flow/V)` with `L̂` = `û` made perpendicular to the flow; spread with the
  load weights, except the flap drag (`½ρV²·S·0.4·δ²` along the flow), which acts on the deflected rear: the nodes behind
  the 60% node by their area share. Next center of pressure: `0.25 − Cm/Cl` (clamped 0.05…0.9), smoothed.
- **Pitch damping**: the strip velocity is sampled at the center of pressure, so it doesn't see the strip rotating.
  Add the quasi-steady thin-airfoil damping `M = −π/16 · ρ·V·S·c² · ω` with `ω = ((v_LE − v_TE)·û)/c` (nose-up
  positive) and `S` the strip area, as a couple: `−M/(2c)·û` on each leading-edge node and `+M/(2c)·û` on each
  trailing-edge node (no net force; its power `−M·ω` is never positive). Without it a sudden brake input yanks the light
  canopy back into a stall.

### Cell pressure (ram air)

Each strip's pressure `p` (−0.25 sucked in … 1 inflated) moves toward a target: with `cellDeflationTimeConstant`
when it falls, and with `cellPressureTimeConstant · clamp(10 / V, 0.5, 3)` when it rises (the inlet takes in more air
faster). The target of a cell with an inlet (and V > 2 m/s) depends on how the inlet faces the flow:

- The **inlet's angle of attack** `α_inlet`: the strip's `α` corrected by how far the nose has turned against the rest
  of the profile (the direction from the leading edge to the first nodes behind it, compared with the rest pose), so a
  nose folded under closes the inlet even when the rest of the cell still looks like a profile.
- The **facing angle** `ψ` between the direction the inlet faces and the oncoming flow, in 3D: at rest the inlet faces
  the trim flow (the strip's `α_trim`, from the rest pose and the trim glide path), and it turns with the nose, so
  `cos ψ = cos(α_inlet − α_trim) · V_strip / |v_air|` (`V_strip` the flow speed in the strip's plane, `v_air` the whole
  relative wind: flow along the span, past a folded tip or in sideslip, passes the inlet by).

The ram `r(ψ)` is 1 within the capture angle (30°, `SimulatorSettings.InletCaptureAngle`), then
`cos²(90° · (ψ − 30°) / 60°)`, falling to 0 when the flow passes across the inlet (ψ = 90°), then ramps to −0.25 (sucked
empty) at 120° and stays there for flow from behind. The flow coming over the nose closes the inlet whatever the facing:

| `α_inlet` | target |
|---|---|
| below `inletClosingAlpha − 3°` | −0.25 (flow over the nose: sucked empty) |
| up to `inletClosingAlpha` | ramps from −0.25 to `r(ψ)` |
| above | `r(ψ)` |

So a deeply stalled wing (flow from below, α ≈ 40°, ψ ≈ 30°) keeps its pressure, and a full stall (wing back, flow from
below and behind, ψ ≈ 80° and more) empties the cells.

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

After the node joints come the **attachments**, named `<node name> attachment`: canopy nodes again, the ones the lines
hang from (tabs, and the canopy nodes around a tab that has no node of its own). An attachment only translates: its
world transform is its node's position, with no rotation. The rigging is skinned to these instead of the canopy joints: a
canopy joint turns with the fabric, and a line skinned to it would swing with it, by up to its length times the angle
(a meter in a collapse), and zig-zag between the points along it.

- **Godot**: import the `.glb`; the joints become `Skeleton3D` bones in the proxy's node order, then the attachments.
  Set each bone's global pose from the simulated frames every physics frame.
- **Blender**: import the `.glb`; the armature has one bone per joint and the canopy is parented with weights.
  The toolbox can record a simulated flight and bake it into the joints (the preview's Record and Export recording),
  which Blender imports as an action.

The simulation proxy JSON and the `.glb` must come from the same design (same node count and order).
