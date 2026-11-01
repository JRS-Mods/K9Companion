import bpy, os, struct, math, sys
from mathutils import Vector, Matrix

# The stash/trader icons for the dog's gear (harnesses, head gear, the clip-on camera and the collars), from assets/gear_<key>.mesh + gear_<key>.jpg (tools/convert_gear.py):
# icon_<key>.png, the harness on its own in three-quarter view, lit like the game's item icons, transparent, at twice
# the game's size for its cells. Run: blender -b --factory-startup --python tools/render_gear_icons.py
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = os.path.join(root, 'assets')
# Each harness's stash size in cells (width, height), as the server mod sets it for its item.
SIZES = {'harness_chestrig': (2, 2), 'harness_pocket': (2, 3), 'harness_tactical': (3, 3), 'harness_assault': (3, 4), 'harness_camera': (3, 4),
         'head_goggles_tan': (2, 1), 'head_goggles_black': (2, 1), 'head_headset': (2, 2), 'camera_action': (1, 1),
         'collar_training': (1, 1), 'collar_black': (1, 1), 'collar_buckle': (1, 1)}
# The clip-on camera has one model per harness (tools/convert_attachment.py); its icon is the one for the chest rig.
MESHES = {'camera_action': 'camera_action_harness_chestrig'}
# The collars as they were made (tools/convert_item_models.py), not wrapped round the dog's neck.
MESHES.update({f'collar_{s}': f'collar_{s}_item' for s in ('training', 'black', 'buckle')})
# Seen from the front quarter rather than the side, so its lens shows.
VIEWS = {'camera_action': (0.7, -1.6, 0.7)}
VIEWS.update({f'collar_{s}': (0.6, -1.6, 0.7) for s in ('training', 'black', 'buckle')})
def icon_px(cells): return 2 * (cells * 63 + 1)
Ci = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))   # Unity -> Blender (inverse of the converters' C)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

def ints(f, n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
def floats(f, n): return struct.unpack('<' + 'f' * n, f.read(4 * n))

def read_mesh_block(f):
    name = f.read(ints(f)[0]).decode('utf8')
    verts, uvs = [], []
    for _ in range(ints(f)[0]):
        v = floats(f, 8); verts.append(Ci @ Vector(v[:3])); uvs.append(v[6:8]); ints(f, 4); floats(f, 4)
    tris = []
    for _ in range(ints(f)[0]):
        idx = ints(f, ints(f)[0]); tris.extend(tuple(idx[i:i + 3]) for i in range(0, len(idx), 3))
    return name, verts, uvs, tris

def make_object(name, verts, uvs, tris):
    mesh = bpy.data.meshes.new(name); mesh.from_pydata(verts, [], [(a, c, b) for a, b, c in tris]); mesh.update()
    layer = mesh.uv_layers.new()
    for loop in mesh.loops: layer.data[loop.index].uv = uvs[loop.vertex_index]
    for p in mesh.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, mesh); scene.collection.objects.link(ob)
    return ob

def look(ob, target): ob.rotation_euler = (target - ob.location).to_track_quat('-Z', 'Y').to_euler()

scene.render.engine = 'CYCLES'; scene.cycles.samples = 48; scene.cycles.device = 'CPU'
scene.render.film_transparent = True; scene.view_settings.view_transform = 'Standard'
scene.render.image_settings.file_format = 'PNG'; scene.render.image_settings.color_mode = 'RGBA'
scene.world = bpy.data.worlds.new('icon'); scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (0.3, 0.3, 0.3, 1)
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
cam.data.type = 'ORTHO'
lights = []
for key, (w, h) in SIZES.items():
    with open(os.path.join(data, f'gear_{MESHES.get(key, key)}.mesh'), 'rb') as f:
        assert ints(f)[0] == 0x31544b4a
        ob = make_object(*read_mesh_block(f))
    material = bpy.data.materials.new(key); material.use_nodes = True
    nodes = material.node_tree.nodes; bsdf = nodes.get('Principled BSDF'); bsdf.inputs['Roughness'].default_value = 0.75
    tex = nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(os.path.join(data, f'gear_{key}.jpg'))
    material.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    ob.data.materials.append(material)
    pts = [ob.matrix_world @ v.co for v in ob.data.vertices]
    lo = Vector(map(min, *pts)); hi = Vector(map(max, *pts)); centre = (lo + hi) / 2
    for l in lights: bpy.data.objects.remove(l)
    lights = []
    for location, energy in [((1.2, -1.0, 1.6), 90), ((-1.4, 0.6, 1.0), 40)]:
        light = bpy.data.objects.new('light', bpy.data.lights.new('light', 'AREA')); scene.collection.objects.link(light)
        light.data.energy = energy; light.data.size = 1.5; light.location = centre + Vector(location); look(light, centre)
        lights.append(light)
    scene.render.resolution_x, scene.render.resolution_y = icon_px(w), icon_px(h)
    cam.location = centre + Vector(VIEWS.get(key, (1.6, -0.9, 0.95))); look(cam, centre)
    # Framed to the harness's outline as the camera sees it (the scale is the picture's longer side).
    bpy.context.view_layer.update()
    right, up = cam.matrix_world.to_3x3() @ Vector((1, 0, 0)), cam.matrix_world.to_3x3() @ Vector((0, 1, 0))
    across = max(p.dot(right) for p in pts) - min(p.dot(right) for p in pts)
    tall = max(p.dot(up) for p in pts) - min(p.dot(up) for p in pts)
    W, H = icon_px(w), icon_px(h)
    cam.data.ortho_scale = 1.08 * (max(across, tall * W / H) if W >= H else max(tall, across * H / W))
    scene.render.filepath = os.path.join(data, f'icon_{key}.png')
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(ob)
print('GEAR_ICONS_OK', sorted(SIZES))
