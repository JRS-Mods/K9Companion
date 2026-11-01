import bpy, os, sys, struct, math
from mathutils import Vector, Matrix

# A preview of the dog wearing gear as the game shows it: the dog with a harness and a collar from assets/
# (gear_<key>.mesh + .jpg, tools/convert_gear.py), side and three-quarter, one row per pairing. Anything after the collar
# is more gear worn with them (goggles, the headset).
#   blender -b --factory-startup --python render_gear_preview.py -- <out png> <harness>+<collar>[+<gear>...] ...
args = sys.argv[sys.argv.index('--') + 1:]
out, pairs = args[0], [a.split('+') for a in args[1:]]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data = os.path.join(root, 'assets'); source = os.path.join(root, 'work', 'exported')
Ci = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))   # Unity -> Blender (inverse of the converters' C)

def ints(f, n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
def floats(f, n): return struct.unpack('<' + 'f' * n, f.read(4 * n))

def gear(key):
    with open(os.path.join(data, f'gear_{key}.mesh'), 'rb') as f:
        assert ints(f)[0] == 0x31544b4a
        name = f.read(ints(f)[0]).decode()
        verts, uvs = [], []
        for _ in range(ints(f)[0]):
            v = floats(f, 8); verts.append(Ci @ Vector(v[:3])); uvs.append(v[6:8]); ints(f, 4); floats(f, 4)
        tris = []
        for _ in range(ints(f)[0]):
            idx = ints(f, ints(f)[0]); tris.extend(tuple(idx[i:i + 3]) for i in range(0, len(idx), 3))
    mesh = bpy.data.meshes.new(key); mesh.from_pydata(verts, [], [(a, c, b) for a, b, c in tris]); mesh.update()
    layer = mesh.uv_layers.new()
    for loop in mesh.loops: layer.data[loop.index].uv = uvs[loop.vertex_index]
    for p in mesh.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(key, mesh); bpy.context.scene.collection.objects.link(ob)
    mat = bpy.data.materials.new(key); mat.use_nodes = True
    tex = mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(os.path.join(data, f'gear_{key}.jpg'))
    bsdf = mat.node_tree.nodes['Principled BSDF']; bsdf.inputs['Roughness'].default_value = .8
    mat.node_tree.links.new(tex.outputs[0], bsdf.inputs['Base Color'])
    ob.data.materials.append(mat)
    return ob

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in scene.objects if o.type == 'ARMATURE')
body = next(o for o in scene.objects if o.type == 'MESH')
fur = bpy.data.materials.new('fur'); fur.use_nodes = True
node = fur.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(os.path.join(source, 'GermanShepherd_BaseColor.tga'))
fur.node_tree.links.new(node.outputs[0], fur.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
body.data.materials.clear(); body.data.materials.append(fur)
engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
scene.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in engines else 'BLENDER_EEVEE'
scene.render.resolution_x, scene.render.resolution_y = 1000, 760
scene.world = bpy.data.worlds.new('w'); scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (.42, .42, .44, 1)
sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); scene.collection.objects.link(sun)
sun.data.energy = 3.5; sun.rotation_euler = (math.radians(45), 0, math.radians(-30))
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
cam.data.lens = 50
target = arm.matrix_world @ arm.data.bones['RigNeck1'].head_local
folder = os.path.dirname(out); stem = os.path.splitext(os.path.basename(out))[0]
shots = []
for harness, collar, *extra in pairs:
    worn = [gear(harness), gear('collar_' + collar)] + [gear(k) for k in extra]
    for view, d in (('side', Vector((1, .15, .12))), ('angle', Vector((.75, -.95, .45)))):
        cam.location = target + d.normalized() * 1.9
        cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(folder, '_'.join([stem, harness, collar] + extra + [view]) + '.png'); shots.append(scene.render.filepath)
        bpy.ops.render.render(write_still=True)
    for ob in worn: bpy.data.objects.remove(ob)
print('PREVIEW_SHOTS', shots)
