import bpy, os, struct, math, json
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

# Builds assets/jacket.mesh (+ jacket.jpg) from the police dog jacket in E:\SPT Mod\Assets: the jacket is fitted
# over the dog's body, pushed clear of the fur, and skinned to the dog's own skeleton (weights copied from the
# nearest point of the body), so it bends with the dog in every animation.
# Format: 'JKT1', then one mesh block exactly as in k9dog.dog: name, vertex count, (pos, normal, uv, 4 bone indices,
# 4 weights) per vertex, submesh count, then index count and indices per submesh. Bone indices follow k9dog.dog.
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('DOG_EXPORT') or os.path.join(root, 'work', 'exported')
assets = os.environ.get('DOG_PROP_ASSETS') or os.path.join(os.path.dirname(root), 'Assets')
jacket_dir = os.path.join(assets, 'police-dog-jacket')
dest = os.path.join(root, 'assets')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (as convert_blender.py)
CLEARANCE, LOOSE = .008, .045      # inner surface sits this far off the fur; further than LOOSE counts as baggy

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
body = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
names = [b.name for b in arm.data.bones]
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=os.path.join(jacket_dir, 'source', 'render.fbx'))
jacket = next(o for o in set(bpy.data.objects) - before if o.type == 'MESH')
bpy.ops.object.select_all(action='DESELECT'); jacket.select_set(True); bpy.context.view_layer.objects.active = jacket
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

# Body surface in world space, triangulated, for distance and weight lookups.
body_world = [body.matrix_world @ v.co for v in body.data.vertices]
body.data.calc_loop_triangles()
tris = [tuple(t.vertices) for t in body.data.loop_triangles]
tree = BVHTree.FromPolygons(body_world, tris)

def signed(p):
    hit, normal, index, dist = tree.find_nearest(p)
    if hit is None: return 1.0
    return (p - hit).dot(normal) if dist > 1e-6 else 0.0

# ---- fit: offset and per-axis scale about the jacket's centre, chosen so the jacket sits just outside the body ----
original = [v.co.copy() for v in jacket.data.vertices]
centre = sum(original, Vector()) / len(original)
sample = original[::max(1, len(original) // 2500)]

def place(co, p):
    dy, dz, sx, sy, sz = p
    d = co - centre
    return Vector((centre.x + d.x * sx, centre.y + dy + d.y * sy, centre.z + dz + d.z * sz))

def cost(p):
    total = 0
    for co in sample:
        s = signed(place(co, p))
        if s < CLEARANCE: total += min((CLEARANCE - s) * 40, 4) ** 2     # inside the dog: heavily penalised
        elif s > LOOSE: total += min((s - LOOSE) * 10, 1) ** 2           # hanging off the body: mildly penalised
    return total / len(sample)

params = [0.0, 0.0, 1.0, 1.0, 1.0]
steps = [.04, .03, .08, .06, .08]
limits = [(-.08, .08), (-.05, .2), (.8, 1.35), (.85, 1.25), (.85, 1.4)]
best = cost(params)
print('JACKET_FIT start', round(best, 5))
for rnd in range(7):
    for i in range(5):
        for direction in (1, -1):
            while True:
                trial = list(params); trial[i] = min(max(trial[i] + direction * steps[i], limits[i][0]), limits[i][1])
                c = cost(trial)
                if c < best - 1e-7: params, best = trial, c
                else: break
    steps = [s * .5 for s in steps]
print('JACKET_FIT params dy=%.3f dz=%.3f sx=%.3f sy=%.3f sz=%.3f cost=%.5f' % (*params, best))
for v, co in zip(jacket.data.vertices, original): v.co = place(co, params)
jacket.data.update()

# ---- whatever still dips into the fur is pushed out along the body's surface normal ----
pushed = 0
for v in jacket.data.vertices:
    hit, normal, index, dist = tree.find_nearest(v.co)
    if hit is None: continue
    s = (v.co - hit).dot(normal)
    if s < CLEARANCE: v.co = v.co + normal * (CLEARANCE - s); pushed += 1
jacket.data.update()

# ---- skin weights: from the nearest point of the body, then smoothed across the jacket so it doesn't crease ----
group_bone = {g.index: names.index(g.name) for g in body.vertex_groups if g.name in names}
body_weights = []
for v in body.data.vertices:
    w = {}
    for g in v.groups:
        if g.group in group_bone and g.weight > 0: w[group_bone[g.group]] = w.get(group_bone[g.group], 0) + g.weight
    body_weights.append(w)
weights = []
for v in jacket.data.vertices:
    hit, normal, index, dist = tree.find_nearest(v.co)
    tri = tris[index]
    bary = poly_3d_calc([body_world[i] for i in tri], hit)
    w = {}
    for corner, f in zip(tri, bary):
        for b, x in body_weights[corner].items(): w[b] = w.get(b, 0) + x * f
    weights.append(w)
neighbours = [[] for _ in jacket.data.vertices]
for e in jacket.data.edges:
    a, b = e.vertices; neighbours[a].append(b); neighbours[b].append(a)
for _ in range(4):
    smoothed = []
    for i, w in enumerate(weights):
        mix = dict((b, x * .5) for b, x in w.items())
        if neighbours[i]:
            share = .5 / len(neighbours[i])
            for j in neighbours[i]:
                for b, x in weights[j].items(): mix[b] = mix.get(b, 0) + x * share
        else:
            mix = dict(w)
        smoothed.append(mix)
    weights = smoothed

def top4(w):
    best4 = sorted(((x, b) for b, x in w.items() if x > 0), reverse=True)[:4]
    total = sum(x for x, b in best4)
    assert total > 0, 'Unweighted jacket vertex'
    ws = [x / total for x, b in best4]; bs = [b for x, b in best4]
    while len(ws) < 4: ws.append(0); bs.append(0)
    return bs, ws

# ---- write: one vertex per distinct (position, uv, normal), as few as the jacket needs ----
mesh = jacket.data
mesh.calc_loop_triangles()
uv_layer = mesh.uv_layers.active.data
normal_matrix = C.to_3x3().inverted().transposed()
skins = [top4(w) for w in weights]
index_of, out_vertices, triangles = {}, [], []
for tri in mesh.loop_triangles:
    corner_ids = []
    for loop_index in tri.loops:
        loop = mesh.loops[loop_index]
        uv = tuple(round(x, 5) for x in uv_layer[loop_index].uv)
        n = (normal_matrix @ mesh.corner_normals[loop_index].vector).normalized()
        key = (loop.vertex_index, uv, tuple(round(x, 3) for x in n))
        if key not in index_of:
            index_of[key] = len(out_vertices)
            out_vertices.append((C @ mesh.vertices[loop.vertex_index].co, n, uv, skins[loop.vertex_index]))
        corner_ids.append(index_of[key])
    a, b, c = corner_ids
    triangles.extend((a, c, b))

os.makedirs(dest, exist_ok=True)
with open(os.path.join(dest, 'jacket.mesh'), 'wb') as f:
    def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
    def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
    ints(0x31544b4a)   # 'JKT1'
    raw = b'Police dog jacket'; ints(len(raw)); f.write(raw)
    ints(len(out_vertices))
    for p, n, uv, (bs, ws) in out_vertices:
        floats(*p, *n, *uv); ints(*bs); floats(*ws)
    ints(1); ints(len(triangles)); ints(*triangles)

image = bpy.data.images.load(os.path.join(jacket_dir, 'textures', 'dog_police_jaket_Dog_police_jaket_AlbedoTr.jpeg'))
image.scale(2048, 2048)
image.filepath_raw = os.path.join(dest, 'jacket.jpg'); image.file_format = 'JPEG'
bpy.context.scene.render.image_settings.quality = 90
image.save()
inside = sum(1 for v in mesh.vertices if signed(v.co) < 0)
stats = {'vertices': len(out_vertices), 'triangles': len(triangles) // 3, 'fit': params, 'fit_cost': best,
         'pushed_out': pushed, 'still_inside': inside, 'bones_used': sorted({names[b] for _, _, _, (bs, ws) in out_vertices for b, w in zip(bs, ws) if w > .05})}
print('JACKET_CONVERSION_OK', json.dumps(stats))
