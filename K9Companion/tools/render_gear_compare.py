import bpy, os, sys, struct, math
from mathutils import Vector, Matrix

# Before and after for one piece of the dog's gear, three shots side by side (joined by the caller):
#   source  - the model as it came (the .glb), before any of convert_gear's work
#   dog     - the game's mesh (assets/gear_<key>.mesh) on the dog, as worn in game
#   inspect - the item model (gear_<key>_item.mesh, off the dog) on a dark background, as the inspect window shows it
#   blender -b --factory-startup --python render_gear_compare.py -- <out folder> <key> <source glb>
args = sys.argv[sys.argv.index('--') + 1:]
out, key, glb = args[0], args[1], args[2]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = os.path.join(root, 'assets'); source = os.path.join(root, 'work', 'exported')
Ci = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))   # Unity -> Blender (inverse of the converters' C)
small = not key.startswith('harness_')

def ints(f, n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
def floats(f, n): return struct.unpack('<' + 'f' * n, f.read(4 * n))

def gear(part, texture):
    with open(os.path.join(data, f'gear_{part}.mesh'), 'rb') as f:
        assert ints(f)[0] == 0x31544b4a
        f.read(ints(f)[0])
        verts, uvs = [], []
        for _ in range(ints(f)[0]):
            v = floats(f, 8); verts.append(Ci @ Vector(v[:3])); uvs.append(v[6:8]); ints(f, 4); floats(f, 4)
        tris = []
        for _ in range(ints(f)[0]):
            idx = ints(f, ints(f)[0]); tris.extend(tuple(idx[i:i + 3]) for i in range(0, len(idx), 3))
    mesh = bpy.data.meshes.new(part); mesh.from_pydata(verts, [], [(a, c, b) for a, b, c in tris]); mesh.update()
    layer = mesh.uv_layers.new()
    for loop in mesh.loops: layer.data[loop.index].uv = uvs[loop.vertex_index]
    for p in mesh.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(part, mesh); bpy.context.scene.collection.objects.link(ob)
    mat = bpy.data.materials.new(part); mat.use_nodes = True
    tex = mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(os.path.join(data, f'gear_{texture}.jpg'))
    bsdf = mat.node_tree.nodes['Principled BSDF']; bsdf.inputs['Roughness'].default_value = .7
    mat.node_tree.links.new(tex.outputs[0], bsdf.inputs['Base Color'])
    ob.data.materials.append(mat)
    return ob

def bounds(obs):
    pts = [o.matrix_world @ Vector(c) for o in obs for c in o.bound_box]
    lo = Vector(map(min, *pts)); hi = Vector(map(max, *pts))
    return (lo + hi) / 2, (hi - lo).length

def shoot(name, target, size, direction, background):
    scene.world.node_tree.nodes['Background'].inputs[0].default_value = background
    cam.location = target + direction.normalized() * size * 1.6
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = os.path.join(out, f'{key}_{name}.png')
    bpy.ops.render.render(write_still=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
scene.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in engines else 'BLENDER_EEVEE'
scene.render.resolution_x, scene.render.resolution_y = 640, 520
scene.world = bpy.data.worlds.new('w'); scene.world.use_nodes = True
sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); scene.collection.objects.link(sun)
sun.data.energy = 3.5; sun.rotation_euler = (math.radians(45), 0, math.radians(-30))
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
cam.data.lens = 50
os.makedirs(out, exist_ok=True)
# The views: the dog's head is at -y, up is z. Jackets from the side and a little in front; head gear from the front.
view = Vector((.45, -1, .35)) if small else Vector((1, -.75, .45))

# ---- the source model, as it came ----
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=glb)
raw = [o for o in set(bpy.data.objects) - before if o.type == 'MESH']
centre, size = bounds(raw)
tris = sum(len(p.vertices) - 2 for o in raw for p in o.data.polygons)
shoot('1_source', centre, size, Vector((1, -1, .5)), (.42, .42, .44, 1))
for o in set(bpy.data.objects) - before: bpy.data.objects.remove(o)

# ---- the game's mesh alone, as the inspect window turns it (dark, like the game's) ----
texture = 'camera_action' if key.startswith('camera_action') else key
item = key + '_item' if os.path.exists(os.path.join(data, f'gear_{key}_item.mesh')) else key
parts = [gear(item, texture)]
centre, size = bounds(parts)
shoot('3_inspect', centre, size, view, (.03, .03, .035, 1))
for o in parts: bpy.data.objects.remove(o)
parts = [gear(key, texture)]

# ---- the game's mesh on the dog ----
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in scene.objects if o.type == 'ARMATURE')
body = next(o for o in scene.objects if o.type == 'MESH' and o.name not in [p.name for p in parts])
fur = bpy.data.materials.new('fur'); fur.use_nodes = True
node = fur.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(os.path.join(source, 'GermanShepherd_BaseColor.tga'))
fur.node_tree.links.new(node.outputs[0], fur.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
body.data.materials.clear(); body.data.materials.append(fur)
target = arm.matrix_world @ arm.data.bones['RigHead' if small else 'RigChest'].head_local
shoot('2_dog', target, .45 if small else 1.0, view, (.42, .42, .44, 1))
print('COMPARE_OK', key, tris)
