import bpy, os, struct, math
import numpy as np
from mathutils import Vector, Matrix

# Builds, from assets/jacket.mesh + jacket.jpg (tools/convert_jacket.py) and assets/k9dog.dog:
#   jacket_<colour>.jpg   the jacket texture in each colour (only the fabric is recoloured; webbing and buckles stay)
#   icon_<colour>.png     the stash/trader icon for each colour (3 x 2 cells, transparent)
#   dog_outline.png       the landscape dog silhouette drawn behind the DOG tab's slots
# Run: blender -b --factory-startup --python tools/render_ui_assets.py
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = os.path.join(root, 'assets')
COLOURS = {   # name: fabric albedo (None = the jacket's own black)
    'black': None,
    'olive': (0.26, 0.28, 0.17),
    'coyote': (0.50, 0.40, 0.27),
    'blue': (0.09, 0.13, 0.27),
    'hivis': (0.80, 0.86, 0.12),
    'elite': (0.30, 0.31, 0.33),   # wolf grey (The Last Handler's elite harness)
}
# Each tier's stash size in cells (width, height), as the server mod sets it; icons are drawn at twice the game's size.
SIZES = {'black': (1, 2), 'hivis': (2, 2), 'blue': (2, 3), 'olive': (3, 3), 'coyote': (3, 4), 'elite': (3, 3)}
# UI_ASSETS_ONLY=elite,blue renders just those colours (and skips the outline), leaving the other files as they are.
ONLY = [c for c in os.environ.get('UI_ASSETS_ONLY', '').split(',') if c]
if ONLY: COLOURS = {c: a for c, a in COLOURS.items() if c in ONLY}
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
    if uvs:
        layer = mesh.uv_layers.new()
        for loop in mesh.loops: layer.data[loop.index].uv = uvs[loop.vertex_index]
    for p in mesh.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, mesh); scene.collection.objects.link(ob)
    return ob

def look(cam, target):
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()

# ---- textures: recolour the fabric, keep the hardware ----
source = bpy.data.images.load(os.path.join(data, 'jacket.jpg'))
w, h = source.size
pixels = np.empty(w * h * 4, dtype=np.float32); source.pixels.foreach_get(pixels)
rgba = pixels.reshape(h, w, 4)
lum = rgba[..., :3].mean(axis=2)
fabric = np.clip((0.19 - lum) / 0.06, 0, 1)[..., None]                 # fabric is near-black, webbing ~0.25
detail = np.clip(lum / 0.04, 0.3, 2.2)[..., None]                      # keep the weave and seams
textures = {}
for colour, albedo in COLOURS.items():
    out = rgba.copy()
    if albedo is not None:
        tinted = np.clip(np.array(albedo, dtype=np.float32) * (0.6 + 0.4 * detail), 0, 1)
        out[..., :3] = rgba[..., :3] * (1 - fabric) + tinted * fabric
    image = bpy.data.images.new('jacket_' + colour, w, h)
    image.pixels.foreach_set(out.ravel())
    image.filepath_raw = os.path.join(data, f'jacket_{colour}.jpg'); image.file_format = 'JPEG'
    scene.render.image_settings.quality = 90
    image.save()
    textures[colour] = image

# ---- icons: the jacket on its own, three-quarter view, lit like the game's item icons ----
with open(os.path.join(data, 'jacket.mesh'), 'rb') as f:
    assert ints(f)[0] == 0x31544b4a
    jacket = make_object(*read_mesh_block(f))
centre = sum((jacket.matrix_world @ v.co for v in jacket.data.vertices), Vector()) / len(jacket.data.vertices)
material = bpy.data.materials.new('jacket'); material.use_nodes = True
nodes = material.node_tree.nodes; bsdf = nodes.get('Principled BSDF'); bsdf.inputs['Roughness'].default_value = 0.75
tex = nodes.new('ShaderNodeTexImage'); material.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
jacket.data.materials.append(material)
scene.render.engine = 'CYCLES'; scene.cycles.samples = 48; scene.cycles.device = 'CPU'
scene.render.film_transparent = True; scene.view_settings.view_transform = 'Standard'
scene.render.image_settings.file_format = 'PNG'; scene.render.image_settings.color_mode = 'RGBA'
scene.world = bpy.data.worlds.new('icon'); scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (0.55, 0.55, 0.55, 1)
scene.world.node_tree.nodes['Background'].inputs[1].default_value = 1.0
for location, energy in [((1.2, -1.0, 1.6), 260), ((-1.4, 0.6, 1.0), 120)]:
    light = bpy.data.objects.new('light', bpy.data.lights.new('light', 'AREA')); scene.collection.objects.link(light)
    light.data.energy = energy; light.data.size = 1.5; light.location = centre + Vector(location); look(light, centre)
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
cam.data.type = 'ORTHO'; cam.data.ortho_scale = 0.78
for colour, image in textures.items():
    w, h = SIZES[colour]
    scene.render.resolution_x, scene.render.resolution_y = icon_px(w), icon_px(h)
    cam.location = centre + Vector((1.6, -0.9, 0.95)); look(cam, centre)
    if h > w: cam.rotation_euler.rotate_axis('Z', math.pi / 2)   # tall icons show the jacket upright
    tex.image = image
    scene.render.filepath = os.path.join(data, f'icon_{colour}.png')
    bpy.ops.render.render(write_still=True)
bpy.data.objects.remove(jacket)

if ONLY:
    print('UI_ASSETS_OK', sorted(n for n in os.listdir(data) if any(n.startswith(('jacket_' + c, 'icon_' + c)) for c in ONLY)))
    raise SystemExit(0)

# ---- the dog outline: a soft side-on silhouette with a lighter rim, like the gear screen's figure ----
with open(os.path.join(data, 'k9dog.dog'), 'rb') as f:
    assert ints(f)[0] == 0x31474f44
    for _ in range(ints(f)[0]):
        f.read(ints(f)[0]); ints(f); floats(f, 10)
    body = [make_object(*read_mesh_block(f)) for _ in range(ints(f)[0])]
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'FLAT'; scene.display.shading.color_type = 'SINGLE'; scene.display.shading.single_color = (1, 1, 1)
OW, OH = 1100, 620
scene.render.resolution_x, scene.render.resolution_y = OW, OH
mid = Vector((0, 0.04, 0.43))
cam.data.ortho_scale = 1.62; cam.location = mid + Vector((3, 0, 0)); look(cam, mid)
silhouette = os.path.join(bpy.app.tempdir, 'dog_silhouette.png')
scene.render.filepath = silhouette
bpy.ops.render.render(write_still=True)
shot = bpy.data.images.load(silhouette)
px = np.empty(OW * OH * 4, dtype=np.float32); shot.pixels.foreach_get(px)
alpha = px.reshape(OH, OW, 4)[..., 3]
inside = alpha > 0.5
grown = inside.copy()
for dy in range(-3, 4):
    for dx in range(-3, 4):
        if dx * dx + dy * dy <= 9: grown |= np.roll(np.roll(inside, dy, 0), dx, 1)
rim = grown & ~inside
out = np.zeros((OH, OW, 4), dtype=np.float32)
out[inside] = (0.42, 0.45, 0.47, 0.42)
out[rim] = (0.70, 0.73, 0.75, 0.55)
outline = bpy.data.images.new('dog_outline', OW, OH, alpha=True)
outline.pixels.foreach_set(out.ravel())
outline.filepath_raw = os.path.join(data, 'dog_outline.png'); outline.file_format = 'PNG'
outline.save()
print('UI_ASSETS_OK', sorted(n for n in os.listdir(data) if n.startswith(('jacket_', 'icon_', 'dog_outline'))))
