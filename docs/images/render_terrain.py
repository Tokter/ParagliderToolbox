# Renders a terrain from "Export terrain glTF" with Cycles: a Nishita sky and sun, haze with distance (aerial
# perspective) on the terrain, and a camera placed by latitude and longitude (the export's root node keeps the origin).
#
# blender -b --python docs/images/render_terrain.py -- <terrain.glb> <output.png> <shot> [samples] [scale]
#   shot: a name from SHOTS below, or custom:LAT,LON,ALT:LAT,LON,ALT:LENS:SUN_ELEVATION,SUN_AZIMUTH (camera, target;
#         an altitude like +400 is above the ground there)
import bpy, sys, math, mathutils

argv = sys.argv[sys.argv.index("--") + 1:]
glb, out, shot = argv[0], argv[1], argv[2]
samples = int(argv[3]) if len(argv) > 3 else 64
scale = float(argv[4]) if len(argv) > 4 else 1.0

# Camera (lat, lon, altitude), target (lat, lon, altitude), lens (mm), sun (elevation, azimuth: 180 is south).
SHOTS = {
    # High over the Lauterbrunnen valley, looking south-east to the Eiger, Mönch and Jungfrau, as a paraglider sees them.
    "jungfrau": ((46.6400, 7.9050, "+1300"), (46.5500, 7.9650, 1900), 26, (35, 250)),
    # The Eiger, Mönch and Jungfrau wall from the west side of the Lauterbrunnen valley.
    "wall": ((46.6000, 7.8700, "+800"), (46.5650, 7.9900, 3000), 36, (32, 255)),
}

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
scene = bpy.context.scene
root = bpy.data.objects.get("Terrain")
lat0, lon0 = (root["latitude"], root["longitude"]) if root and "latitude" in root else (0.0, 0.0)


def local(lat, lon):
    # Meters east (Blender X) and north (Y) of the origin; a few meters off the export's transverse Mercator at 10 km.
    return ((lon - lon0) * 111320 * math.cos(math.radians(lat0)), (lat - lat0) * 110574)


def ground(x, y):
    hit, location, *_ = scene.ray_cast(bpy.context.evaluated_depsgraph_get(), (x, y, 9000), (0, 0, -1))
    return location.z if hit else 0.0


def point(spec):
    lat, lon, alt = spec
    x, y = local(lat, lon)
    alt = str(alt)
    z = ground(x, y) + float(alt[1:]) if alt.startswith("+") else float(alt)
    return mathutils.Vector((x, y, z))


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
scene.view_settings.exposure = -0.4

world = bpy.data.worlds.new("sky"); scene.world = world; world.use_nodes = True
nt = world.node_tree
sky = nt.nodes.new("ShaderNodeTexSky"); sky.sky_type = 'NISHITA'
sky.altitude = 2000; sky.air_density = 1.0; sky.dust_density = 0.7
bg = nt.nodes["Background"]; nt.links.new(sky.outputs[0], bg.inputs[0]); bg.inputs[1].default_value = 0.35
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN')); scene.collection.objects.link(sun)
sun.data.angle = math.radians(0.6); sun.data.energy = 4.0


def set_sun(elevation, azimuth):
    sky.sun_elevation = math.radians(elevation); sky.sun_rotation = math.radians(azimuth)
    towards = mathutils.Vector((math.sin(math.radians(azimuth)) * math.cos(math.radians(elevation)),
                                math.cos(math.radians(azimuth)) * math.cos(math.radians(elevation)),
                                math.sin(math.radians(elevation))))
    sun.rotation_euler = (-towards).to_track_quat('-Z', 'Y').to_euler()


# The aerial images are albedo with their own soft shadows: matte, and fading into the haze with distance.
for material in bpy.data.materials:
    if not material.use_nodes: continue
    t = material.node_tree
    bsdf = next((n for n in t.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    output = next((n for n in t.nodes if n.type == 'OUTPUT_MATERIAL'), None)
    if bsdf is None or output is None: continue
    bsdf.inputs["Roughness"].default_value = 0.95
    bsdf.inputs["Specular IOR Level"].default_value = 0.15
    camera = t.nodes.new("ShaderNodeCameraData")
    fog = t.nodes.new("ShaderNodeMapRange"); fog.inputs[1].default_value = 2500; fog.inputs[2].default_value = 60000
    fog.inputs[4].default_value = 0.7
    t.links.new(camera.outputs["View Distance"], fog.inputs[0])
    curve = t.nodes.new("ShaderNodeMath"); curve.operation = 'POWER'; curve.inputs[1].default_value = 0.7
    t.links.new(fog.outputs[0], curve.inputs[0])
    haze = t.nodes.new("ShaderNodeEmission"); haze.inputs[0].default_value = (0.62, 0.74, 0.92, 1); haze.inputs[1].default_value = 0.9
    mix = t.nodes.new("ShaderNodeMixShader")
    t.links.new(curve.outputs[0], mix.inputs[0]); t.links.new(bsdf.outputs[0], mix.inputs[1]); t.links.new(haze.outputs[0], mix.inputs[2])
    t.links.new(mix.outputs[0], output.inputs[0])

cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); scene.collection.objects.link(cam); scene.camera = cam
cam.data.clip_start = 1; cam.data.clip_end = 120000

if shot.startswith("custom:"):
    _, c, t2, lens, light = shot.split(":")
    def parse(s):
        lat, lon, alt = s.split(","); return (float(lat), float(lon), alt)
    camera_spec, target_spec, lens, (elevation, azimuth) = parse(c), parse(t2), float(lens), map(float, light.split(","))
else:
    camera_spec, target_spec, lens, (elevation, azimuth) = SHOTS[shot]
set_sun(elevation, azimuth)
location, target = point(camera_spec), point(target_spec)
cam.location = location; cam.data.lens = lens
cam.rotation_euler = (target - location).to_track_quat('-Z', 'Y').to_euler()
print(f"camera {tuple(round(v) for v in location)} target {tuple(round(v) for v in target)}")
scene.render.filepath = out
if out.lower().endswith((".jpg", ".jpeg")):
    scene.render.image_settings.file_format = 'JPEG'; scene.render.image_settings.quality = 88
bpy.ops.render.render(write_still=True)
