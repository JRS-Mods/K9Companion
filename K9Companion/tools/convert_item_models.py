import bpy, bmesh, os, sys, struct, math, json
from mathutils import Vector, Matrix

# The dog's gear as an item, off the dog: the model as it came (not stretched round the dog as convert_gear.py fits it),
# cut down to a budget, for the inspect window and for gear lying on the ground (GearVisuals.cs). Same texture as the
# worn mesh (gear_<key>.jpg; the coordinates are the source's), same format (JKT1, every vertex on bone 0).
#   blender -b -P convert_item_models.py -- <glb> <key> <front: -x|+x|-y|+y> [budget=8000] [cut=<y>,<z>] [height=<m>]
# Size: as wide (across the dog) as the worn mesh, or height=<m> tall (the action camera, whose worn copies are per rig).
# The result faces Unity's +z with +y up, as the worn gear on a dog facing +z does.
args = sys.argv[sys.argv.index('--') + 1:]
options = dict(a.split('=', 1) for a in args if '=' in a)
args = [a for a in args if '=' not in a]
glb, key, front = args[0], args[1], args[2]
budget = int(options.get('budget', 8000))
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
dest = os.path.join(root, 'assets')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (as convert_gear.py)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
gear = max(meshes, key=lambda o: len(o.data.polygons))
for o in list(bpy.context.scene.objects):
    if o is not gear: bpy.data.objects.remove(o)
bpy.ops.object.select_all(action='DESELECT'); gear.select_set(True); bpy.context.view_layer.objects.active = gear
gear.parent = None
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
source_tris = sum(len(p.vertices) - 2 for p in gear.data.polygons)
if 'cut' in options:   # as convert_gear.py: the goggles built into the camera vest
    cut_y, cut_z = map(float, options['cut'].split(','))
    bm = bmesh.new(); bm.from_mesh(gear.data)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.y < cut_y and v.co.z > cut_z], context='VERTS')
    bm.to_mesh(gear.data); bm.free()

# One piece (glTF splits vertices along texture seams), then down to the budget.
bm = bmesh.new(); bm.from_mesh(gear.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
bm.to_mesh(gear.data); bm.free()
dec = gear.modifiers.new('decimate', 'DECIMATE')
dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1, budget / max(1, sum(len(p.vertices) - 2 for p in gear.data.polygons)))
dec.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=dec.name)

# Specks left floating by the decimation.
bm = bmesh.new(); bm.from_mesh(gear.data); bm.verts.ensure_lookup_table()
seen, islands = set(), []
for v in bm.verts:
    if v.index in seen: continue
    stack, island = [v], []
    seen.add(v.index)
    while stack:
        x = stack.pop(); island.append(x)
        for e in x.link_edges:
            o = e.other_vert(x)
            if o.index not in seen: seen.add(o.index); stack.append(o)
    islands.append(island)
doomed = {v for island in islands if len(island) < len(bm.verts) * .004 for v in island}
# A collar lies flat (its ring level, as convert_gear.py has it): whatever floats inside the ring goes too (the black
# collar came with a stray bit of buckle in the middle).
if key.startswith('collar_'):
    band = max(islands, key=len)
    centre = sum((Vector((v.co.x, v.co.y)) for v in band), Vector((0, 0))) / len(band)
    inner = min((Vector((v.co.x, v.co.y)) - centre).length for v in band)
    for island in islands:
        c = sum((Vector((v.co.x, v.co.y)) for v in island), Vector((0, 0))) / len(island)
        if island is not band and (c - centre).length < inner * .9 and len(island) < len(bm.verts) * .2: doomed.update(island)
bmesh.ops.delete(bm, geom=list(doomed), context='VERTS')
bm.to_mesh(gear.data); bm.free()

# Facing the dog's way (its head at Blender -y, which C turns to Unity +z), centred, at the worn gear's size.
turn = {'-x': 90, '+x': -90, '-y': 0, '+y': 180}[front]
mesh = gear.data
mesh.transform(Matrix.Rotation(math.radians(turn), 4, 'Z'))
lo = Vector(map(min, *[v.co for v in mesh.vertices])); hi = Vector(map(max, *[v.co for v in mesh.vertices]))
if 'height' in options:
    scale = float(options['height']) / (hi.z - lo.z)
else:
    with open(os.path.join(dest, f'gear_{key}.mesh'), 'rb') as f:
        f.read(4); f.read(struct.unpack('<i', f.read(4))[0])
        xs = []
        for _ in range(struct.unpack('<i', f.read(4))[0]):
            xs.append(struct.unpack('<f', f.read(4))[0]); f.read(28 + 32)
    scale = (max(xs) - min(xs)) / (hi.x - lo.x)   # Unity x is Blender -x: the same width
mesh.transform(Matrix.Diagonal((scale, scale, scale, 1)) @ Matrix.Translation(-(lo + hi) / 2))
mesh.update()

# Write: one vertex per distinct (position, uv, normal), as convert_gear.py does.
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
with open(os.path.join(dest, f'gear_{key}_item.mesh'), 'wb') as f:
    def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
    def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
    ints(0x31544b4a)   # 'JKT1'
    raw = (key + '_item').encode(); ints(len(raw)); f.write(raw)
    ints(len(out_vertices))
    for p, n, uv in out_vertices:
        floats(*p, *n, *uv); ints(0, 0, 0, 0); floats(1, 0, 0, 0)
    ints(1); ints(len(triangles)); ints(*triangles)
size = [round((hi - lo)[i] * scale, 3) for i in range(3)]
print('ITEM_MODEL_OK', json.dumps({'key': key, 'source_triangles': source_tris, 'triangles': len(triangles) // 3, 'size_m': size}))
