# Renders a recorded flight (a .glb from a recording's Export animation) into a video with Cycles: the paraglider over
# procedural mountains under a Nishita sky, the camera following it (smoothed), slowly circling it, and backing off
# whenever the wing would leave the frame.
#
# blender -b --python docs/images/render_flight.py -- <flight.glb> <output folder> <mode> [samples] [scale] [fps]
#   mode: stills:F1,F2,... (a few frames, to check the look), frames (every frame as PNG; frames already rendered are
#         kept, so it can be resumed), encode (the PNGs into flight.mp4, H.264)
import bpy, sys, math, mathutils, os, glob
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
glb, out, mode = argv[0], argv[1], argv[2]
samples = int(argv[3]) if len(argv) > 3 else 64
scale = float(argv[4]) if len(argv) > 4 else 1.0
fps = int(argv[5]) if len(argv) > 5 else 30
os.makedirs(out, exist_ok=True)


def encode():
    # Blender's own FFmpeg, through the sequencer: the PNGs in order, at the frame rate they were rendered for.
    files = sorted(glob.glob(os.path.join(out, "frame_*.png")))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = fps
    scene.view_settings.view_transform = 'Standard'  # the PNGs are already graded
    scene.sequence_editor_create()
    strip = scene.sequence_editor.strips.new_image("flight", files[0], 1, 1)
    for f in files[1:]: strip.elements.append(os.path.basename(f))
    scene.frame_start, scene.frame_end = 1, len(files)
    import struct
    with open(files[0], "rb") as fh: fh.read(16); w, h = struct.unpack(">II", fh.read(8))
    scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = w, h, 100
    scene.render.image_settings.file_format = 'FFMPEG'
    scene.render.ffmpeg.format = 'MPEG4'
    scene.render.ffmpeg.codec = 'H264'
    scene.render.ffmpeg.constant_rate_factor = 'HIGH'
    scene.render.ffmpeg.ffmpeg_preset = 'GOOD'
    scene.render.filepath = os.path.join(out, "flight.mp4")
    bpy.ops.render.render(animation=True)
    print(f"wrote {scene.render.filepath} ({len(files)} frames at {fps} fps)")


if mode == "encode":
    encode()
    sys.exit(0)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps = fps  # the importer turns the animation's seconds into frames at this rate
bpy.ops.import_scene.gltf(filepath=glb)
for o in bpy.data.objects:
    if o.name in ("ProxyCage", "Icosphere"): o.hide_render = True
arm = next(o for o in scene.objects if o.type == 'ARMATURE')
action = bpy.data.actions[0]
first, last = int(action.frame_range[0]), int(action.frame_range[1])
scene.frame_start, scene.frame_end = first, last

# The glider's track: a few bones that span it (the pilot, the nose and tail of the middle section, both tips).
names = [b.name for b in arm.data.bones]
sections = sorted({n.split("_")[0] for n in names if n.startswith("S") and n.endswith("_LE")})
mid = sections[len(sections) // 2]
probes = ["Pilot", f"{mid}_LE", f"{mid}_TE", f"{sections[0]}_LE", f"{sections[-1]}_LE", f"{sections[0]}_TE", f"{sections[-1]}_TE"]
track = []
for f in range(first, last + 1):
    scene.frame_set(f)
    track.append([arm.matrix_world @ arm.pose.bones[p].head for p in probes])
centers = [(pts[0] + pts[1]) * 0.5 for pts in track]


def smooth(values, radius):
    # A centered Gaussian moving average (no lag), shorter windows at the ends.
    result = []
    for i in range(len(values)):
        total, weight = None, 0.0
        for k in range(max(0, i - radius), min(len(values), i + radius + 1)):
            w = math.exp(-((k - i) / (radius / 2)) ** 2)
            total = values[k] * w if total is None else total + values[k] * w
            weight += w
        result.append(total / weight)
    return result


targets = smooth(centers, int(fps * 0.6))

# The camera circles slowly (60° over the clip), a little above, looking at the smoothed center with a 35 mm lens.
lens, sensor = 35.0, 36.0
aspect = 16 / 9
tan_x = sensor / 2 / lens
tan_y = tan_x / aspect
heading = targets[min(len(targets) - 1, fps)] - targets[0]
start_azimuth = math.atan2(heading.y, heading.x) + math.radians(135)  # in front of the glider, off to its right
elevation = math.radians(12)


def placement(i, distance):
    t = i / max(1, len(targets) - 1)
    a = start_azimuth - math.radians(60) * t
    offset = mathutils.Vector((math.cos(elevation) * math.cos(a), math.cos(elevation) * math.sin(a), math.sin(elevation)))
    return targets[i] + offset * distance


# How far back the camera must be so every probe stays within the middle 80% of the frame.
base = 26.0
needed = []
for i, pts in enumerate(track):
    distance = base
    for _ in range(4):
        cam = placement(i, distance)
        forward = (targets[i] - cam).normalized()
        right = forward.cross(mathutils.Vector((0, 0, 1))).normalized()
        up = right.cross(forward)
        worst = 0.0
        for p in pts:
            d = p - cam
            depth = max(0.1, d.dot(forward))
            worst = max(worst, abs(d.dot(right)) / depth / tan_x / 0.8, abs(d.dot(up)) / depth / tan_y / 0.8)
        if worst <= 1: break
        distance *= worst
    needed.append(distance)
# Back off ahead of time and come back in slowly: the largest distance needed within a second, then smoothed.
window = fps
held = [max(needed[max(0, i - window // 2):i + window + 1]) for i in range(len(needed))]
distances = smooth(held, fps)

target = bpy.data.objects.new("target", None); scene.collection.objects.link(target)
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); scene.collection.objects.link(cam); scene.camera = cam
cam.data.lens = lens; cam.data.sensor_width = sensor; cam.data.clip_start = 0.1; cam.data.clip_end = 40000
look = cam.constraints.new('TRACK_TO'); look.target = target; look.track_axis = 'TRACK_NEGATIVE_Z'; look.up_axis = 'UP_Y'
for i, f in enumerate(range(first, last + 1)):
    target.location = targets[i]; target.keyframe_insert("location", frame=f)
    cam.location = placement(i, distances[i]); cam.keyframe_insert("location", frame=f)
for o in (target, cam):
    for fc in o.animation_data.action.fcurves:
        for k in fc.keyframe_points: k.interpolation = 'LINEAR'
print(f"camera distance {min(distances):.0f}–{max(distances):.0f} m")

# Render settings: Cycles on the GPU, denoised, AgX.
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
scene.render.use_persistent_data = True
scene.render.resolution_x, scene.render.resolution_y = int(1920 * scale), int(1080 * scale)
scene.view_settings.view_transform = 'AgX'
scene.view_settings.look = 'AgX - Punchy'
scene.view_settings.exposure = -0.9

# Sky and sun: an afternoon sun from behind the camera's side, so the wing is lit.
world = bpy.data.worlds.new("sky"); scene.world = world; world.use_nodes = True
nt = world.node_tree
sky = nt.nodes.new("ShaderNodeTexSky"); sky.sky_type = 'NISHITA'
sky.altitude = 1200; sky.air_density = 1.0; sky.dust_density = 0.5
bg = nt.nodes["Background"]; nt.links.new(sky.outputs[0], bg.inputs[0]); bg.inputs[1].default_value = 0.35
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN')); scene.collection.objects.link(sun)
sun.data.angle = math.radians(0.6); sun.data.energy = 4.0
sun_azimuth = start_azimuth - math.radians(30) + math.radians(40)  # toward the sun, seen from the glider
sun_elevation = math.radians(38)
to_sun = mathutils.Vector((math.cos(sun_elevation) * math.cos(sun_azimuth), math.cos(sun_elevation) * math.sin(sun_azimuth), math.sin(sun_elevation)))
sun.rotation_euler = (-to_sun).to_track_quat('-Z', 'Y').to_euler()
sky.sun_elevation = sun_elevation
sky.sun_rotation = math.atan2(to_sun.x, to_sun.y)  # Nishita measures the rotation from +Y toward +X

# Mountains a few hundred meters below: close enough that the ground moves past, fading into the haze far away. Ridged
# multifractal noise makes ridges and valleys.
lowest = min(min(p.z for p in pts) for pts in track)
relief = 900
center = targets[len(targets) // 2]
bpy.ops.mesh.primitive_grid_add(x_subdivisions=1000, y_subdivisions=1000, size=24000, location=(center.x, center.y, 0))
terrain = bpy.context.active_object; terrain.name = "Terrain"
tex = bpy.data.textures.new("mountains", 'MUSGRAVE'); tex.musgrave_type = 'RIDGED_MULTIFRACTAL'
tex.noise_basis = 'IMPROVED_PERLIN'; tex.noise_scale = 3500; tex.octaves = 7; tex.dimension_max = 1.0; tex.lacunarity = 2.0
tex.offset = 1.0; tex.gain = 2.0; tex.noise_intensity = 0.55
disp = terrain.modifiers.new("disp", 'DISPLACE'); disp.texture = tex; disp.strength = relief; disp.mid_level = 0; disp.texture_coords = 'LOCAL'
bpy.ops.object.shade_smooth()
# Lower it so the highest point within two kilometers is a few hundred meters below the glider (peaks further away may
# rise above it); the heights also set the color bands.
evaluated = terrain.evaluated_get(bpy.context.evaluated_depsgraph_get())
mesh = evaluated.to_mesh()
co = np.empty(len(mesh.vertices) * 3, dtype=np.float32); mesh.vertices.foreach_get("co", co); co = co.reshape(-1, 3)
evaluated.to_mesh_clear()
near = np.hypot(co[:, 0], co[:, 1]) < 2000
terrain.location.z = lowest - 250 - float(co[near, 2].max())
z_low, z_high = (terrain.location.z + float(v) for v in np.percentile(co[:, 2], [1, 99.7]))
print(f"terrain {z_low:.0f}..{z_high:.0f} m, glider down to {lowest:.0f} m")

# Forest and meadows on the gentle slopes, rock on the steep faces and high up, snow on the flatter high ground.
tm = bpy.data.materials.new("Terrain"); tm.use_nodes = True; terrain.data.materials.append(tm)
n = tm.node_tree.nodes; l = tm.node_tree.links
tb = n["Principled BSDF"]; tb.inputs["Roughness"].default_value = 0.95


def connect(value, socket):
    if isinstance(value, bpy.types.NodeSocket): l.new(value, socket)
    else: socket.default_value = value


def math_node(operation, a, b):
    m = n.new("ShaderNodeMath"); m.operation = operation; connect(a, m.inputs[0]); connect(b, m.inputs[1])
    return m.outputs[0]


def map_range(value, a, b):  # a → 0, b → 1, clamped
    m = n.new("ShaderNodeMapRange"); m.inputs[1].default_value = a; m.inputs[2].default_value = b; l.new(value, m.inputs[0])
    return m.outputs[0]


def ramp(value, stops):
    r = n.new("ShaderNodeValToRGB"); l.new(value, r.inputs[0]); cr = r.color_ramp
    for i, (position, color) in enumerate(stops):
        e = cr.elements[i] if i < 2 else cr.elements.new(position)
        e.position = position; e.color = (*color, 1)
    return r.outputs[0]


def mix_color(factor, a, b):
    m = n.new("ShaderNodeMix"); m.data_type = 'RGBA'; l.new(factor, m.inputs[0])
    connect(a, next(s for s in m.inputs if s.name == "A" and s.type == 'RGBA'))
    connect(b if isinstance(b, bpy.types.NodeSocket) else (*b, 1), next(s for s in m.inputs if s.name == "B" and s.type == 'RGBA'))
    return next(s for s in m.outputs if s.type == 'RGBA')


geo = n.new("ShaderNodeNewGeometry")
position = n.new("ShaderNodeSeparateXYZ"); l.new(geo.outputs["Position"], position.inputs[0])
normal = n.new("ShaderNodeSeparateXYZ"); l.new(geo.outputs["Normal"], normal.inputs[0])
noise = n.new("ShaderNodeTexNoise"); noise.inputs["Scale"].default_value = 0.004; noise.inputs["Detail"].default_value = 12
height = math_node('ADD', map_range(position.outputs[2], z_low, z_high), math_node('MULTIPLY', math_node('SUBTRACT', noise.outputs[0], 0.5), 0.25))
vegetation = ramp(height, [(0.0, (0.015, 0.04, 0.01)), (0.3, (0.025, 0.06, 0.012)), (0.45, (0.05, 0.09, 0.018)), (0.62, (0.075, 0.08, 0.035))])
rock = ramp(noise.outputs[0], [(0.35, (0.065, 0.06, 0.05)), (0.65, (0.13, 0.12, 0.105))])
rockiness = math_node('MAXIMUM', map_range(normal.outputs[2], 0.8, 0.66), map_range(height, 0.66, 0.8))
fine = n.new("ShaderNodeTexNoise"); fine.inputs["Scale"].default_value = 0.03; fine.inputs["Detail"].default_value = 8
snow_line = math_node('ADD', height, math_node('MULTIPLY', math_node('SUBTRACT', fine.outputs[0], 0.5), 0.2))
snow = math_node('MULTIPLY', map_range(snow_line, 0.84, 0.88), map_range(normal.outputs[2], 0.72, 0.82))
l.new(mix_color(snow, mix_color(rockiness, vegetation, rock), (0.8, 0.82, 0.86)), tb.inputs["Base Color"])
cd = n.new("ShaderNodeCameraData"); fog = n.new("ShaderNodeMapRange"); fog.inputs[1].default_value = 600; fog.inputs[2].default_value = 20000
l.new(cd.outputs["View Distance"], fog.inputs[0])
fogc = n.new("ShaderNodeMath"); fogc.operation = 'POWER'; fogc.inputs[1].default_value = 0.8; l.new(fog.outputs[0], fogc.inputs[0])
em = n.new("ShaderNodeEmission"); em.inputs[0].default_value = (0.75, 0.85, 1.0, 1); em.inputs[1].default_value = 1.0
ms = n.new("ShaderNodeMixShader"); l.new(fogc.outputs[0], ms.inputs[0]); l.new(tb.outputs[0], ms.inputs[1]); l.new(em.outputs[0], ms.inputs[2])
l.new(ms.outputs[0], n["Material Output"].inputs[0])
terrain.visible_diffuse = False; terrain.visible_glossy = False; terrain.visible_transmission = False
# Light bounced off the ground onto the underside of the wing, following the glider.
bounce = bpy.data.objects.new("bounce", bpy.data.lights.new("bounce", "AREA")); scene.collection.objects.link(bounce)
bounce.data.shape = "SQUARE"; bounce.data.size = 200; bounce.data.energy = 25000; bounce.data.color = (0.85, 0.88, 0.8)
bounce.parent = target; bounce.location = (0, 0, -60); bounce.rotation_euler = (math.pi, 0, 0)
bounce.visible_camera = False; bounce.visible_glossy = False

# The pilot (the export has the harness hardware but no pilot): a seated figure in a pod harness, hanging from the
# carabiners and facing where the glider flies, the arms reaching the brake toggles.
def material(name, color, roughness=0.6):
    m = bpy.data.materials.new(name); m.use_nodes = True
    p = m.node_tree.nodes["Principled BSDF"]; p.inputs["Base Color"].default_value = (*color, 1); p.inputs["Roughness"].default_value = roughness
    return m


pilot = bpy.data.objects.new("pilot", None); scene.collection.objects.link(pilot)
pilot.rotation_mode = 'QUATERNION'
for f in range(first, last + 1):
    scene.frame_set(f)
    bone = lambda name: arm.matrix_world @ arm.pose.bones[name].head
    left_c, right_c, body = bone("Carabiner_L"), bone("Carabiner_R"), bone("Pilot")
    hang = (left_c + right_c) * 0.5
    side = (left_c - right_c).normalized()
    up = hang - body; up = (up - side * up.dot(side)).normalized()
    forward = side.cross(up)
    # Local axes: x to the pilot's left, y backward (Blender's glider flies toward −Y), z up.
    basis = mathutils.Matrix((side, -forward, up)).transposed()
    pilot.location = hang; pilot.keyframe_insert("location", frame=f)
    pilot.rotation_quaternion = basis.to_quaternion(); pilot.keyframe_insert("rotation_quaternion", frame=f)


def part(name, mat, location, scale, segments=24):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=segments // 2, location=(0, 0, 0))
    o = bpy.context.active_object; o.name = name; o.data.materials.append(mat)
    o.parent = pilot; o.location = location; o.scale = scale
    bpy.ops.object.shade_smooth()
    return o


harness = material("Harness", (0.03, 0.035, 0.04), 0.7)
jacket = material("Jacket", (0.35, 0.06, 0.04), 0.65)
helmet = material("Helmet", (0.85, 0.85, 0.82), 0.3)
part("pod", harness, (0, -0.25, -0.45), (0.2, 0.62, 0.17))       # legs stretched forward in the pod
part("seat", harness, (0, 0.12, -0.36), (0.23, 0.2, 0.2))        # seat and back protector
part("torso", jacket, (0, 0.06, -0.02), (0.18, 0.12, 0.27))
part("helmet", helmet, (0, 0.04, 0.38), (0.12, 0.13, 0.12))
for side_name, x in (("L", 0.19), ("R", -0.19)):
    hand = bpy.data.objects.new(f"hand_{side_name}", None); scene.collection.objects.link(hand)
    follow = hand.constraints.new('COPY_LOCATION'); follow.target = arm; follow.subtarget = f"Toggle_{side_name}"
    # A unit cylinder from its origin (the shoulder) along +Y, stretched to the hand.
    bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=0.045, depth=1, location=(0, 0.5, 0), rotation=(math.pi / 2, 0, 0))
    a = bpy.context.active_object; a.name = f"arm_{side_name}"; a.data.materials.append(jacket)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    a.parent = pilot; a.location = (x, 0.06, 0.18)
    stretch = a.constraints.new('STRETCH_TO'); stretch.target = hand; stretch.rest_length = 1.0
    stretch.volume = 'NO_VOLUME'; stretch.keep_axis = 'PLANE_Z'

# Thin ripstop: light comes through the cells.
canopy = bpy.data.materials["Canopy"]; t = canopy.node_tree
bsdf = next(x for x in t.nodes if x.type == 'BSDF_PRINCIPLED')
out_node = next(x for x in t.nodes if x.type == 'OUTPUT_MATERIAL')
trans = t.nodes.new("ShaderNodeBsdfTranslucent"); mix = t.nodes.new("ShaderNodeMixShader")
mix.inputs[0].default_value = 0.12
base_color = bsdf.inputs["Base Color"].links[0].from_socket if bsdf.inputs["Base Color"].is_linked else None
if base_color: t.links.new(base_color, trans.inputs["Color"])
t.links.new(bsdf.outputs[0], mix.inputs[1]); t.links.new(trans.outputs[0], mix.inputs[2]); t.links.new(mix.outputs[0], out_node.inputs[0])
bsdf.inputs["Roughness"].default_value = 0.55
bsdf.inputs["Sheen Weight"].default_value = 0.1

scene.render.image_settings.file_format = 'PNG'
if mode.startswith("stills:"):
    for f in (int(x) for x in mode[7:].split(",")):
        scene.frame_set(f)
        scene.render.filepath = os.path.join(out, f"still_{f:04d}.png")
        bpy.ops.render.render(write_still=True)
elif mode == "frames":
    for f in range(first, last + 1):
        path = os.path.join(out, f"frame_{f:04d}.png")
        if os.path.exists(path): continue
        scene.frame_set(f)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print(f"frame {f} of {last}", flush=True)
