# Renders the README images from an exported .glb with Cycles: Nishita sky, procedural terrain with distance haze,
# a ground bounce light and translucent canopy fabric.
# blender -b --python docs/images/render.py -- <model.glb> <output prefix> <shots, e.g. hero,side,backlit,nose,evening> <samples> <scale>
import bpy, sys, math, mathutils
argv = sys.argv[sys.argv.index("--") + 1:]
glb, out, shots, samples, scale = argv[0], argv[1], argv[2].split(","), int(argv[3]), float(argv[4])
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
scene = bpy.context.scene
for o in bpy.data.objects:
    if o.name in ("ProxyCage", "Icosphere"): o.hide_render = True
scene.render.engine = 'CYCLES'
prefs = bpy.context.preferences.addons["cycles"].preferences
for kind in ("OPTIX", "CUDA"):
    try:
        prefs.compute_device_type = kind; prefs.get_devices()
        if any(d.type == kind for d in prefs.devices):
            for d in prefs.devices: d.use = d.type == kind
            scene.cycles.device = 'GPU'; break
    except TypeError: pass
scene.cycles.samples = samples
scene.cycles.use_denoising = True
scene.render.resolution_x, scene.render.resolution_y = int(1600 * scale), int(900 * scale)
scene.view_settings.view_transform = 'AgX'
scene.view_settings.look = 'AgX - Punchy'
scene.view_settings.exposure = -0.9

world = bpy.data.worlds.new("sky"); scene.world = world; world.use_nodes = True
nt = world.node_tree
sky = nt.nodes.new("ShaderNodeTexSky"); sky.sky_type = 'NISHITA'
sky.altitude = 1500; sky.air_density = 1.0; sky.dust_density = 0.4

bg = nt.nodes["Background"]; nt.links.new(sky.outputs[0], bg.inputs[0])
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN')); scene.collection.objects.link(sun)
sun.data.angle = math.radians(0.6)

def set_sun(elevation, azimuth, strength=0.35, energy=4.0, color=(1, 1, 1)):
    sky.sun_elevation = math.radians(elevation); sky.sun_rotation = math.radians(azimuth)
    bg.inputs[1].default_value = strength
    sun.data.energy = energy; sun.data.color = color
    # Point the sun lamp along the sky's sun direction.
    d = mathutils.Vector((math.sin(math.radians(azimuth)) * math.cos(math.radians(elevation)),
                          -math.cos(math.radians(azimuth)) * math.cos(math.radians(elevation)) * -1,
                          math.sin(math.radians(elevation))))
    sun.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()


# Mountains far below, fading into the haze with distance (aerial perspective).
bpy.ops.mesh.primitive_grid_add(x_subdivisions=500, y_subdivisions=500, size=40000, location=(0, 0, -1600))
terrain = bpy.context.active_object; terrain.name = "Terrain"
tex = bpy.data.textures.new("mountains", 'CLOUDS'); tex.noise_scale = 1.4; tex.noise_depth = 6; tex.noise_basis = 'BLENDER_ORIGINAL'
disp = terrain.modifiers.new("disp", 'DISPLACE'); disp.texture = tex; disp.strength = 1800; disp.texture_coords = 'GLOBAL'
tex.noise_scale = 4500
bpy.ops.object.shade_smooth()
tm = bpy.data.materials.new("Terrain"); tm.use_nodes = True; terrain.data.materials.append(tm)
n = tm.node_tree.nodes; l = tm.node_tree.links
tb = n["Principled BSDF"]; tb.inputs["Roughness"].default_value = 0.95
geo = n.new("ShaderNodeNewGeometry"); sz = n.new("ShaderNodeSeparateXYZ"); l.new(geo.outputs["Position"], sz.inputs[0])
hz = n.new("ShaderNodeMapRange"); hz.inputs[1].default_value = -1500; hz.inputs[2].default_value = -150; l.new(sz.outputs[2], hz.inputs[0])
nz = n.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 0.01; nz.inputs["Detail"].default_value = 10
mixh = n.new("ShaderNodeMath"); mixh.operation = 'ADD'; l.new(hz.outputs[0], mixh.inputs[0])
nzs = n.new("ShaderNodeMath"); nzs.operation = 'MULTIPLY'; nzs.inputs[1].default_value = 0.35; l.new(nz.outputs[0], nzs.inputs[0]); l.new(nzs.outputs[0], mixh.inputs[1])
ramp = n.new("ShaderNodeValToRGB"); l.new(mixh.outputs[0], ramp.inputs[0]); cr = ramp.color_ramp
cr.elements[0].position = 0.2; cr.elements[0].color = (0.035, 0.06, 0.02, 1)
cr.elements[1].position = 0.75; cr.elements[1].color = (0.12, 0.11, 0.09, 1)
e = cr.elements.new(0.95); e.color = (0.85, 0.87, 0.9, 1)
l.new(ramp.outputs[0], tb.inputs["Base Color"])
cd = n.new("ShaderNodeCameraData"); fog = n.new("ShaderNodeMapRange"); fog.inputs[1].default_value = 800; fog.inputs[2].default_value = 30000
l.new(cd.outputs["View Distance"], fog.inputs[0])
fogc = n.new("ShaderNodeMath"); fogc.operation = 'POWER'; fogc.inputs[1].default_value = 0.8; l.new(fog.outputs[0], fogc.inputs[0])
em = n.new("ShaderNodeEmission"); em.inputs[0].default_value = (0.75, 0.85, 1.0, 1); em.inputs[1].default_value = 1.0
ms = n.new("ShaderNodeMixShader"); l.new(fogc.outputs[0], ms.inputs[0]); l.new(tb.outputs[0], ms.inputs[1]); l.new(em.outputs[0], ms.inputs[2])
l.new(ms.outputs[0], n["Material Output"].inputs[0])
terrain.visible_shadow = False
terrain.visible_diffuse = False; terrain.visible_glossy = False; terrain.visible_transmission = False
bounce = bpy.data.objects.new("bounce", bpy.data.lights.new("bounce", "AREA")); scene.collection.objects.link(bounce)
bounce.data.shape = "SQUARE"; bounce.data.size = 200; bounce.data.energy = 25000; bounce.data.color = (0.85, 0.88, 0.8)
bounce.location = (0, 0, -60); bounce.rotation_euler = (math.pi, 0, 0); bounce.visible_camera = False; bounce.visible_glossy = False
# Thin ripstop: light comes through the cells.
canopy = bpy.data.materials["Canopy"]; t = canopy.node_tree
bsdf = next(n for n in t.nodes if n.type == 'BSDF_PRINCIPLED')
out_node = next(n for n in t.nodes if n.type == 'OUTPUT_MATERIAL')
trans = t.nodes.new("ShaderNodeBsdfTranslucent"); mix = t.nodes.new("ShaderNodeMixShader")
mix.inputs[0].default_value = 0.12
base = bsdf.inputs["Base Color"].links[0].from_socket if bsdf.inputs["Base Color"].is_linked else None
if base: t.links.new(base, trans.inputs["Color"])
t.links.new(bsdf.outputs[0], mix.inputs[1]); t.links.new(trans.outputs[0], mix.inputs[2]); t.links.new(mix.outputs[0], out_node.inputs[0])
bsdf.inputs["Roughness"].default_value = 0.55
bsdf.inputs["Sheen Weight"].default_value = 0.1

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); scene.collection.objects.link(cam); scene.camera = cam
cam.data.clip_start = 0.05; cam.data.clip_end = 30000
def shoot(name, loc, target, lens=35, dof=None, fstop=2.8):
    cam.location = loc; cam.data.lens = lens
    d = mathutils.Vector(target) - mathutils.Vector(loc)
    cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    cam.data.dof.use_dof = dof is not None
    if dof is not None: cam.data.dof.focus_distance = dof; cam.data.dof.aperture_fstop = fstop
    scene.render.filepath = f"{out}_{name}.png"; bpy.ops.render.render(write_still=True)

# Blender frame: the glider flies toward -Y, canopy around z = 7, carabiners at the origin.
S = {
  "hero":     lambda: (set_sun(42, 130), shoot("hero", (10.5, -13.5, 8.6), (0, -0.4, 6.0), 38)),
  "top":      lambda: (set_sun(50, 100), shoot("top", (-4.0, -7.0, 13.5), (0, -0.2, 7.0), 30)),
  "side":     lambda: (set_sun(35, 120), shoot("side", (14.0, -3.0, 6.0), (0, -0.3, 4.2), 32)),
  "below":    lambda: (set_sun(55, 200, 0.3, 4.5), shoot("below", (1.2, 1.5, -6.5), (0, -0.6, 6.4), 20)),
  "backlit":  lambda: (set_sun(14, 345, 0.45, 3.0, (1, 0.85, 0.7)), shoot("backlit", (-6.5, 9.0, 4.0), (0, -0.5, 6.3), 32)),
  "nose":     lambda: (set_sun(40, 150), shoot("nose", (1.6, -3.4, 6.4), (0.2, -1.1, 7.45), 50, dof=2.6, fstop=2.0)),
  "risers":   lambda: (set_sun(40, 150), shoot("risers", (0.9, -1.5, 0.6), (0.1, 0.0, 0.35), 45, dof=1.6, fstop=2.0)),
}
S["evening"] = lambda: (set_sun(9, 250, 0.45, 3.5, (1, 0.8, 0.6)), shoot("evening", (-11.0, -9.0, 7.5), (0, -0.4, 5.6), 36))
for s in shots: S[s]()
