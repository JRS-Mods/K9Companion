import bpy, bmesh, os, sys, struct, math, json
from mathutils import Matrix, Vector

# Builds assets/gear_<key>_<harness>.mesh for each harness (+ one gear_<key>.jpg) for a small piece that clips onto a
# harness (the action camera). Unlike convert_gear.py the piece isn't fitted to the dog: it stays rigid, turned so its
# lens faces the dog's forward, stood on the harness's back panel just behind the neck, every vertex on RigChest. One
# copy per harness because each harness's panel is at its own height.
#   blender -b -P convert_attachment.py -- <glb> <key> <height m> <lens: -x|+x|-y|+y> [render dir]
args = sys.argv[sys.argv.index('--') + 1:]
glb, key, height, lens = args[0], args[1], float(args[2]), args[3]
renders = args[4] if len(args) > 4 else None
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('DOG_EXPORT') or os.path.join(root, 'work', 'exported')
dest = os.path.join(root, 'assets')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (as convert_blender.py)
Ci = C.inverted()
BUDGET = 2000
HARNESSES = ('harness_chestrig', 'harness_pocket', 'harness_tactical', 'harness_assault')   # the K9 cam vest has its own camera
MOUNT_BONE = 'RigChest'
# On the harness's back panel just behind the neck, in front of the carry handle (higher up, the mount found the handle).

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
body = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
names = [b.name for b in arm.data.bones]
chest = arm.matrix_world @ arm.data.bones[MOUNT_BONE].head_local
neck = arm.matrix_world @ arm.data.bones['RigNeck1'].head_local
mount_y = (chest.y + neck.y) / 2

before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=glb)
piece = next(o for o in set(bpy.data.objects) - before if o.type == 'MESH')
bpy.ops.object.select_all(action='DESELECT'); piece.select_set(True); bpy.context.view_layer.objects.active = piece
piece.parent = None
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
source_tris = sum(len(p.vertices) - 2 for p in piece.data.polygons)
bm = bmesh.new(); bm.from_mesh(piece.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
bm.to_mesh(piece.data); bm.free()
dec = piece.modifiers.new('decimate', 'DECIMATE')
dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1, BUDGET / source_tris); dec.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=dec.name)

# Mount space (still Blender axes here): turned so the lens faces the dog's forward (-y), scaled to its real height,
# its base's middle at the origin.
turn = {'-y': 0, '+x': -90, '+y': 180, '-x': 90}[lens]
mesh = piece.data
mesh.transform(Matrix.Rotation(math.radians(turn), 4, 'Z'))
lo = Vector(map(min, *[v.co for v in mesh.vertices])); hi = Vector(map(max, *[v.co for v in mesh.vertices]))
s = height / (hi.z - lo.z)
mesh.transform(Matrix.Scale(s, 4) @ Matrix.Translation(-Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))))
mesh.update()

# ---- mount points: the top of each harness above the mount bone, along the spine ----
def harness_points(name):
    with open(os.path.join(dest, 'gear_%s.mesh' % name), 'rb') as f:
        def ints(n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
        assert ints()[0] == 0x31544b4a
        f.read(ints()[0])
        points = []
        for _ in range(ints()[0]):
            v = struct.unpack('<8f', f.read(32)); f.read(32)
            points.append(Ci @ Vector(v[:3]))
    return points

def fur_top(y):
    points = [body.matrix_world @ v.co for v in body.data.vertices]
    return max(p.z for p in points if abs(p.x) < .03 and abs(p.y - y) < .02)

mounts = {}
for name in HARNESSES:
    if not os.path.exists(os.path.join(dest, 'gear_%s.mesh' % name)): continue
    fur = fur_top(mount_y)
    top = [p for p in harness_points(name) if abs(p.x) < .025 and abs(p.y - mount_y) < .015 and p.z < fur + .05]
    mounts[name] = Vector((0, mount_y, max(p.z for p in top) if top else fur + .01))

# ---- write: as convert_gear.py, every vertex fully on the mount bone, one copy per harness ----
bone_index = names.index(MOUNT_BONE)
mesh.calc_loop_triangles()
uv_layer = mesh.uv_layers.active.data
normal_matrix = C.to_3x3().inverted().transposed()
index_of, out_vertices, triangles = {}, [], []
for tri in mesh.loop_triangles:
    ids = []
    for loop_index in tri.loops:
        loop = mesh.loops[loop_index]
        uv = tuple(round(x, 5) for x in uv_layer[loop_index].uv)
        n = (normal_matrix @ mesh.corner_normals[loop_index].vector).normalized()
        k = (loop.vertex_index, uv, tuple(round(x, 3) for x in n))
        if k not in index_of:
            index_of[k] = len(out_vertices)
            out_vertices.append((C @ mesh.vertices[loop.vertex_index].co, n, uv))
        ids.append(index_of[k])
    a, b, c = ids
    triangles.extend((a, c, b))
for name, mount in mounts.items():
    shift = C @ mount
    with open(os.path.join(dest, 'gear_%s_%s.mesh' % (key, name)), 'wb') as f:
        def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
        def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
        ints(0x31544b4a)   # 'JKT1'
        raw = ('%s_%s' % (key, name)).encode(); ints(len(raw)); f.write(raw)
        ints(len(out_vertices))
        for p, n, uv in out_vertices:
            floats(*(p + shift), *n, *uv); ints(bone_index, 0, 0, 0); floats(1, 0, 0, 0)
        ints(1); ints(len(triangles)); ints(*triangles)
image = next(n.image for n in piece.active_material.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image and 'basecolor' in n.image.name.lower())
image.scale(512, 512)    # a few centimetres across on screen
image.filepath_raw = os.path.join(dest, 'gear_%s.jpg' % key); image.file_format = 'JPEG'
image.save()

print('ATTACHMENT_OK', json.dumps({'key': key, 'source_triangles': source_tris, 'triangles': len(triangles) // 3,
                                   'vertices': len(out_vertices), 'size_m': list((hi - lo) * s),
                                   'mounts': {k: list(C @ m) for k, m in mounts.items()}}))

# ---- preview: on each harness, from the side and three-quarters ----
if renders:
    os.makedirs(renders, exist_ok=True)
    def load_gear(name, texture=None):
        points = harness_points(name)
        with open(os.path.join(dest, 'gear_%s.mesh' % name), 'rb') as f:
            def ints(n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
            ints(); f.read(ints()[0]); nv = ints()[0]
            uvs = []
            for _ in range(nv): v = struct.unpack('<8f', f.read(32)); f.read(32); uvs.append(v[6:8])
            tris = []
            for _ in range(ints()[0]):
                idx = ints(ints()[0]); tris.extend(tuple(idx[i:i + 3]) for i in range(0, len(idx), 3))
        m = bpy.data.meshes.new(name); m.from_pydata(points, [], [(a, c, b) for a, b, c in tris]); m.update()
        layer = m.uv_layers.new()
        for loop in m.loops: layer.data[loop.index].uv = uvs[loop.vertex_index]
        for p in m.polygons: p.use_smooth = True
        ob = bpy.data.objects.new(name, m); bpy.context.scene.collection.objects.link(ob)
        mat = bpy.data.materials.new(name); mat.use_nodes = True
        tex = mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(os.path.join(dest, 'gear_%s.jpg' % (texture or name)))
        mat.node_tree.links.new(tex.outputs[0], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
        m.materials.append(mat)
        return ob
    bpy.data.objects.remove(piece)
    fur = bpy.data.materials.new('fur'); fur.use_nodes = True
    node = fur.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(os.path.join(source, 'GermanShepherd_BaseColor.tga'))
    fur.node_tree.links.new(node.outputs[0], fur.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    body.data.materials.clear(); body.data.materials.append(fur)
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x, scene.render.resolution_y = 700, 560
    scene.world = bpy.data.worlds.new('w'); scene.world.use_nodes = True
    scene.world.node_tree.nodes['Background'].inputs[0].default_value = (.42, .42, .44, 1)
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); scene.collection.objects.link(sun)
    sun.data.energy = 3.5; sun.rotation_euler = (math.radians(45), 0, math.radians(-30))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
    for name, mount in mounts.items():
        worn = [load_gear(name), load_gear('%s_%s' % (key, name), key)]
        for view, d in (('side', Vector((1, .1, .25))), ('angle', Vector((.7, -.9, .6)))):
            cam.location = chest + d.normalized() * 1.3
            cam.rotation_euler = (chest - cam.location).to_track_quat('-Z', 'Y').to_euler()
            scene.render.filepath = os.path.join(renders, '%s_on_%s_%s.png' % (key, name, view))
            bpy.ops.render.render(write_still=True)
        for ob in worn: bpy.data.objects.remove(ob)
    print('ATTACHMENT_RENDERS', renders)
