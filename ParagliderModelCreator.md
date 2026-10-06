## Paraglider Model Creator
This tool takes as input a bunch of parameter and creates from it a high resolution textured paraglider mesh. That can be saved in a format that Blender and/or Godot can open.

### High resolution mesh
The high resolution mesh should show fabric curvature, cell ballooning and seam creasing. It should also include suspension lines, risers, break lines and break toggles. No harness though, just everything to make a realist looking paraglider.

### Low-Res Physics Proxy
Since we can't run the physics simulation on that high resolution mesh we need a low-res proxy. Something that we can use to simulate tucks, asymmetric collapses, spins and stalls.
Maybe a low res grid with point passes. Lines acting as tension-only distance constraints. Aerodynamic forces (lift, drag, internal ram-air pressure) are applied per quad/triangle.
And we need to use that low res proxy to deform our high res mesh. Using cage deformers / skin wrapping or bone weights tied to the coarse vertices.
We may want to be able to save that low res proxy in multiple formats. Maybe something we can import into Blender and use it to animate the high res mesh with. And maybe a json format that we can then use to import into our game to run the simulation with. And/or whatever you think would make the most sense.

### Necessary parameters

#### Planform
- Flat Span ($b$): Total tip-to-tip width when laid out flat (e.g., 10–15 m).
- Flat Area ($S$): Surface area of the flat canopy (e.g., 22–30 m²).
- Aspect Ratio: Calculated or specified directly ($AR = b^2 / S$, typically 4.5 for beginners up to 7.0+ for competition wings).
- Chord Distribution Curve $c(y)$: A normalized profile or Bézier curve describing chord length from center ($y=0$) to wingtip ($y=1$).
- Sweep Angle / Leading-Edge Curve: The backwards curvature of the leading edge along the span (paraglider tips sweep slightly rearward).

#### Canopy Arc & Dihedral (3D Projection)
These define how the flat 2D planform bends into an arch in front-view elevation.
- Anhedral / Arc Curve $z(y)$: The vertical droop curve from center to tip (often elliptical or parabolic). Defines the projected span versus flat span.
- Arch Flattening / Tip Cant: The outward/inward angle of the wingtip ribs (stabilizes roll damping and tip vortex behavior).
- Spanwise Twist (Washout): Local aerodynamic washout—the geometric rotation (pitch change) of the airfoil profile from center to wingtip to prevent abrupt tip stalls.

#### Airfoil Profile & Thickness
Paragliders use cambered reflex airfoils with hollow ram-air cell interiors.
- Airfoil Profile Data / Curves: 2D splines or normalized point sets for both the Upper Surface and Lower Surface (often modified NACA or specialized reflex profiles like HQ or custom paraglider sections). 
- Thickness Ratio ($t/c$): Maximum foil thickness as a percentage of local chord (usually 14%–18% at the center, tapering toward the tips).
- Air Inlet Cutout:
    - Inlet Start & End Percentages: Position on the lower leading edge where the ram-air opening sits (e.g., 2% upper to 12% lower surface).
    - Inlet Height / Shape: Cross-sectional opening geometry for the cells that stay inflated.
    - Look into Sharknoses.

#### Cell & Internal Rib Topology
A paraglider is a collection of pressurized fabric chambers running chordwise.
- Cell Count ($N_{\text{cells}}$): Number of cells across the span (entry wings ~38–45 cells; high-performance wings 65–85+ cells).
- Mini-Ribs (Boolean / Count): Extra half-ribs along the trailing edge between main ribs to reduce drag and keep the exit profile clean.
- Cross-Vent Holes (Internal): Radii and positions of circular ventilation holes in internal ribs (needed if generating internal rib geometry for transparency or interior views).

#### Fabric Billowing & Tension (Visual Realism)
- A pressurized ram-air wing does not have flat panels between ribs; internal pressure puffs the fabric outwards between seam lines.
- Cell Ballooning Factor ($B_{\text{scale}}$): The maximum displacement normal to the panel surface between two adjacent ribs, causing the characteristic "pillowed" look.
- Ballooning Decay Curve: How ballooning attenuates near the rigid leading edge and pinches flat at the trailing edge seam.
- Trailing Edge Tensioning: Pucker/gathering factor applied along the trailing edge where the brake lines attach.

#### Rigging & Line Attachment Points
Parameters needed for the generator to places lin tabs and constructs the suspension lines.
- Attachment Point Rows: Chordwise locations of the anchor tabs (traditionally rows A, B, C, D from leading to trailing edge).
- Spanwise Attachment Frequency: Which ribs have line tabs attached (e.g., tabs every 2nd or 3rd rib).
- Pilot / Carabiner Anchor Offset: Vector offset $[0, -y_{\text{harness}}, -z_{\text{harness}}]$ for the riser carabiners to connect line geometries.

#### Simulation Proxy Parameters
We want to be able create games from low poly arcade up to high realism sims.
- Need to be able to adjust the complexity of the simulation proxy that gets created.

### 3D Preview
We want to be able to see the high resolution and simulatin proxy that gets created as we change the parameters. So we need some kind of accelerated rending control that can render a textured paraglider in a preview workspace. Can you create a control in the Atelier repo for that.

### Saving/Exports
We need to be able to save all the parameters as part of the project. And be able to export all the necessary files as described above.

We want to create very high quality models, so take your time to figure out and research all the details that make up a paraglider.