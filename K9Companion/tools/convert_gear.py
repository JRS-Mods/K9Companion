import bpy, bmesh, os, sys, struct, math, json
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree
from mathutils.interpolate import poly_3d_calc

# Builds assets/gear_<key>.mesh (+ gear_<key>.jpg) from one of the generated models in E:\SPT Mod\Assets\Dog
# Accessories (GLB, about two million triangles and 4K textures each): decimated to a game budget, cleared of loose
# fragments (anything floating inside a collar's ring), fitted to the dog (a harness round its chest and back, a
# collar round its neck where the game's collar sits), pushed clear of the fur and skinned to the dog's own skeleton
# (weights copied from the nearest point of the body), so it moves with every animation. Also renders it on the dog.
#   blender -b -P convert_gear.py -- <glb> <key> <harness|collar|head> [front: auto|+x|-x|-y] [render dir] [option=value ...]
# Options: span=<bone>:<bone> (a harness's length along the dog, default RigNeck2:RigSpine3), cut=<y>,<z> (drop whatever
# of the model lies in front of y and above z, in its own units: the goggles built into the K9 cam vest), wrap=1 (head
# gear bent and shrink-wrapped onto the head instead of kept rigid as made).
# Head gear (goggles, the camera headset) sits on the skull and is fixed to RigHead.
# Format: as jacket.mesh ('JKT1', one mesh block exactly as in k9dog.dog). Bone indices follow k9dog.dog.
args = sys.argv[sys.argv.index('--') + 1:]
options = dict(a.split('=', 1) for a in args if '=' in a)
args = [a for a in args if '=' not in a]
glb, key, kind = args[0], args[1], args[2]
front = args[3] if len(args) > 3 else '-x'
renders = args[4] if len(args) > 4 else None
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('DOG_EXPORT') or os.path.join(root, 'work', 'exported')
dest = os.path.join(root, 'assets')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (as convert_blender.py)
CLEARANCE, LOOSE = .005, .02     # gear sits this far off the fur; further than LOOSE counts as baggy
BUDGET = int(options.get('budget', {'harness': 9000, 'collar': 3000, 'head': 4000}[kind]))   # triangles: light enough for a raid with the dog on screen
# The harness covers the shoulders and the front of the back: its padded neck ring round the base of the neck, in front
# of the shoulders (the front legs come through the openings behind it), to the middle of the back.
HARNESS_FROM, HARNESS_TO = options.get('span', 'RigNeck2:RigSpine3').split(':')
HEAD_CLEARANCE = .009       # goggles sit close: the strap on the fur, the lens just off the face
COLLAR_BAND = .045          # how wide the collar band is (m)
COLLAR_BONE = 'RigNeck4'    # high on the neck, just behind the skull: clear of the harnesses' neck ring (lower down, at RigNeck3, they met)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
body = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
names = [b.name for b in arm.data.bones]
def bone(name): return arm.matrix_world @ arm.data.bones[name].head_local

before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=glb)
gear = next(o for o in set(bpy.data.objects) - before if o.type == 'MESH')
for o in set(bpy.data.objects) - before - {gear}:
    if o.type != 'MESH': bpy.data.objects.remove(o)
bpy.ops.object.select_all(action='DESELECT'); gear.select_set(True); bpy.context.view_layer.objects.active = gear
gear.parent = None
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
source_tris = sum(len(p.vertices) - 2 for p in gear.data.polygons)
if 'cut' in options:
    cut_y, cut_z = map(float, options['cut'].split(','))
    bm = bmesh.new(); bm.from_mesh(gear.data)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.y < cut_y and v.co.z > cut_z], context='VERTS')
    bm.to_mesh(gear.data); bm.free()

# ---- one piece: glTF splits vertices along every texture seam, which leaves the model in hundreds of loose islands
# (the texture coordinates live on the corners, so welding loses nothing) ----
bm = bmesh.new(); bm.from_mesh(gear.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
bm.to_mesh(gear.data); bm.free()

# ---- down to the game's budget ----
dec = gear.modifiers.new('decimate', 'DECIMATE')
dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1, BUDGET / max(1, sum(len(p.vertices) - 2 for p in gear.data.polygons))); dec.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=dec.name)

# ---- loose fragments: tiny specks anywhere, and (on a collar) whatever floats inside the ring ----
bm = bmesh.new(); bm.from_mesh(gear.data)
bm.verts.ensure_lookup_table()
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
total = len(bm.verts)
xy = [Vector((v.co.x, v.co.y)) for v in bm.verts]
centre_xy = sum(xy, Vector((0, 0))) / max(1, len(xy))
# The ring, from its biggest piece (the band, so a fragment or the hardware can't skew it): its centre is the middle of
# the band's box, and its inside is that box less the band's own thickness, on each axis.
band = max(islands, key=len)
blo = Vector((min(v.co.x for v in band), min(v.co.y for v in band))); bhi = Vector((max(v.co.x for v in band), max(v.co.y for v in band)))
centre_xy = (blo + bhi) / 2
half = (bhi - blo) / 2
def thickness(axis):
    other = 1 - axis
    near = [v.co[axis] - centre_xy[axis] for v in band if abs(v.co[other] - centre_xy[other]) < half[other] * .08]
    far = [abs(x) for x in near]
    return (max(far) - min(far)) if far else half[axis] * .1
inner_xy = Vector((half.x - thickness(0), half.y - thickness(1)))
inner = min(inner_xy)
doomed = []
for island in islands:
    c = sum((v.co for v in island), Vector()) / len(island)
    small = len(island) < total * .004
    floating = kind == 'collar' and (Vector((c.x, c.y)) - centre_xy).length < inner * .8 and len(island) < total * .2
    if small or floating: doomed.extend(island)
bmesh.ops.delete(bm, geom=list(set(doomed)), context='VERTS')
bm.to_mesh(gear.data); bm.free()
removed = len(set(doomed))

# ---- the dog's body, for fitting, pushing out and weights ----
body_world = [body.matrix_world @ v.co for v in body.data.vertices]
body.data.calc_loop_triangles()
tris = [tuple(t.vertices) for t in body.data.loop_triangles]
tree = BVHTree.FromPolygons(body_world, tris)

def signed(p):
    hit, normal, index, dist = tree.find_nearest(p)
    if hit is None: return 1.0
    return (p - hit).dot(normal) if dist > 1e-6 else 0.0

verts = gear.data.vertices
lo = Vector(map(min, *[v.co for v in verts])); hi = Vector(map(max, *[v.co for v in verts]))

if kind == 'harness':
    # The model's length (x) along the dog's length (its head is at -y), its front at the dog's chest; up is up. Sized
    # to the dog's torso over that length: from the top of its back to its belly, and as wide as its chest. the source's dog
    # vests face -x (their chest piece is there); 'auto' fits both ways and keeps the closer fit, but the closer fit can
    # be the wrong way round (it was for all four), so -x is the default.
    a, b = bone(HARNESS_FROM), bone(HARNESS_TO)
    y0, y1 = min(a.y, b.y), max(a.y, b.y)
    slab = [p for p in body_world if y0 <= p.y <= y1]
    spine_strip = [p for p in slab if abs(p.x) < .04]
    back_top = max(p.z for p in spine_strip)
    belly = min(p.z for p in spine_strip if p.z > .2)
    half_width = max(abs(p.x) for p in slab if p.z > belly + .05)
    length = (y1 - y0) * 1.15
    gap = .008
    # The vest's own body, from its belly edge to the top of its back (body=<low>,<high> in model units), when more than
    # that sticks out above and below: the K9 cam vest's camera and handle on top, its leg straps underneath.
    body_lo, body_hi = map(float, options['body'].split(',')) if 'body' in options else (lo.z, hi.z)
    long_axis = 1 if front == '-y' else 0   # the model's axis that runs along the dog
    target = {long_axis: length, 1 - long_axis: 2 * half_width + 2 * gap}
    scales = Vector((target[0] / (hi.x - lo.x), target[1] / (hi.y - lo.y), (back_top - belly + 2 * gap) / (body_hi - body_lo)))
    mid = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, body_hi))
    model = [v.co.copy() for v in verts]

    def fit_harness(way):
        turn = Matrix.Rotation(math.radians({'-x': 90, '+x': -90, '-y': 0}[way]), 4, 'Z')
        start = Matrix.Translation(Vector((0, (y0 + y1) / 2, back_top + gap))) @ turn @ Matrix.Diagonal(scales.to_4d()) @ Matrix.Translation(-mid)
        original = [start @ co for co in model]
        centre = sum(original, Vector()) / len(original)
        sample = original[::max(1, len(original) // 2500)]

        def place(co, p):
            dy, dz, sx, sy, sz = p
            d = co - centre
            return Vector((centre.x + d.x * sx, centre.y + dy + d.y * sy, centre.z + dz + d.z * sz))

        def cost(p):
            t = 0
            for co in sample:
                s = signed(place(co, p))
                if s < CLEARANCE: t += min((CLEARANCE - s) * 40, 4) ** 2
                elif s > LOOSE: t += min((s - LOOSE) * 25, 2) ** 2
            return t / len(sample)

        params, steps = [0.0, 0.0, 1.0, 1.0, 1.0], [.04, .03, .1, .08, .1]
        # Hardly any sliding along the dog: left free, the fit slides back off the neck onto the easier ribcage.
        limits = [(-.03, .03), (-.1, .15), (.7, 1.5), (.65, 1.3), (.6, 1.5)]
        best = cost(params)
        for rnd in range(7):
            for i in range(5):
                for direction in (1, -1):
                    while True:
                        trial = list(params); trial[i] = min(max(trial[i] + direction * steps[i], limits[i][0]), limits[i][1])
                        c = cost(trial)
                        if c < best - 1e-7: params, best = trial, c
                        else: break
            steps = [s * .5 for s in steps]
        return best, params, [place(co, params) for co in original]

    tried = {way: fit_harness(way) for way in ((front,) if front in ('-x', '+x', '-y') else ('-x', '+x'))}
    way = min(tried, key=lambda w: tried[w][0])
    best, params, placed = tried[way]
    for v, co in zip(verts, placed): v.co = co
    fit = {'front': way, 'scale': list(scales), 'params': params, 'cost': best, 'costs': {w: t[0] for w, t in tried.items()}}
elif kind == 'head':
    # Goggles are a loop of strap round the head with the lens at its front (the source's: the loop in the model's x-y plane,
    # the lens at -y, the over-the-top strap up +z). On the dog the loop can't be level: the back of its skull runs
    # straight into the neck, so a level loop at eye height ends up round the neck. It runs instead from the eyes up to
    # the crown just behind the ears (the strap passes under the ears at the sides), tilted like the forehead.
    eyes = (bone('RigLEye1') + bone('RigREye1')) / 2
    ears = (bone('RigLEar1') + bone('RigREar1')) / 2
    crown_y = ears.y + .08
    crown = tree.ray_cast(Vector((0, crown_y, 2)), Vector((0, 0, -1)), 3)[0]
    front_point, back_point = Vector((0, eyes.y, eyes.z)), Vector((0, crown_y, crown.z - .01))
    forward = (front_point - back_point).normalized()
    side = Vector((1, 0, 0))
    up = forward.cross(side)
    centre = (front_point + back_point) / 2
    # The lens's middle sits in the loop's plane: the model's height there, from the front tenth of the model.
    lens = sorted(v.co.z for v in verts if v.co.y < lo.y + (hi.y - lo.y) * .1)
    lens_z = lens[len(lens) // 2]
    s = .15 / (hi.x - lo.x)    # the lens about as wide as the face at the eyes (the strap is brought in to the fur below)
    for v in verts:
        x, y, z = v.co.x - centre_xy.x, v.co.y - centre_xy.y, v.co.z - lens_z
        v.co = centre + side * (x * s) - forward * (y * s) + up * (z * s)
    # Then round the head's own outline in that plane, every 15 degrees (as the collars round the neck): the strap's inside,
    # measured from the loop's own vertices near the plane (not the top strap arching over), onto the fur.
    BINS = 24
    def angle(r): return math.atan2(r.dot(side), r.dot(forward)) % (2 * math.pi)
    inner_at = [None] * BINS
    for v in verts:
        d = v.co - centre; h = d.dot(up); r = d - up * h
        if abs(h) > .012: continue
        b = int(angle(r) / (2 * math.pi) * BINS) % BINS
        if inner_at[b] is None or r.length < inner_at[b]: inner_at[b] = r.length
    fur_at = []
    for b in range(BINS):
        t = (b + .5) / BINS * 2 * math.pi
        hit, normal, index, dist = tree.ray_cast(centre, forward * math.cos(t) + side * math.sin(t), .5)
        fur_at.append(dist if hit is not None else None)
    factor = [((fur_at[b] + HEAD_CLEARANCE) / inner_at[b]) if fur_at[b] and inner_at[b] else None for b in range(BINS)]
    known = [f for f in factor if f]
    factor = [f or sum(known) / len(known) for f in factor]
    if options.get('wrap') != '1':
        # Rigid (the default): the gear keeps the shape it was made with, as in its inspect window. One even scale
        # about the middle of the loop, so the strap's inside clears the skull all round (the bins where it comes
        # closest; a stray tight bin doesn't blow it up), and only what still dips into the fur is nudged out below.
        need = sorted(factor)
        grow = min(max(need[int(len(need) * .75)], .85), 1.4)
        for v in verts: v.co = centre + (v.co - centre) * grow
        s *= grow
        factor = [grow] * BINS
    else:
        # wrap=1: bent onto the head instead, every 15 degrees round it, then shrink-wrapped onto the fur.
        for v in verts:
            d = v.co - centre; h = d.dot(up); r = d - up * h
            x = angle(r) / (2 * math.pi) * BINS - .5
            b0 = int(math.floor(x)) % BINS; w = x - math.floor(x)
            f = factor[b0] * (1 - w) + factor[(b0 + 1) % BINS] * w
            v.co = centre + up * h + r * f
        # Away from the lens nothing should stand off the head further than a strap's thickness: the camera headset's outer
        # frame (a second loop outside its strap) and loose chin strap are drawn in towards the fur.
        STAND_OFF = .018
        for v in verts:
            d = v.co - centre; h = d.dot(up); r = d - up * h
            t = angle(r)
            if min(t, 2 * math.pi - t) < math.radians(55) or r.length < 1e-6: continue
            b = int(t / (2 * math.pi) * BINS) % BINS
            if not fur_at[b]: continue
            excess = r.length - fur_at[b] - HEAD_CLEARANCE
            if excess > STAND_OFF: v.co = centre + up * h + r.normalized() * (fur_at[b] + HEAD_CLEARANCE + STAND_OFF + (excess - STAND_OFF) * .15)
        # Shrink-wrapped: every part pulled onto the fur, keeping only its own thickness (how far it stands off the goggles'
        # inside within a few centimetres round it), and that kept down to what real dog goggles stand off the face: the
        # lens a couple of centimetres, straps and buckles a few millimetres. Without it the lens box hung off the face with
        # a gap over the muzzle, and the top and side straps floated above the fur.
        WRAP_RADIUS, LENS_DEPTH, STRAP_DEPTH = .025, .03, .008
        near = []
        for v in verts:
            hit, normal, index, dist = tree.find_nearest(v.co)
            near.append((hit, normal, (v.co - hit).dot(normal)))
        kd = KDTree(len(verts))
        for i, v in enumerate(verts): kd.insert(v.co, i)
        kd.balance()
        wrapped = []
        for i, v in enumerate(verts):
            hit, normal, d = near[i]
            floor = min(near[j][2] for _, j, _ in kd.find_range(v.co, WRAP_RADIUS))
            t = max(0, d - floor)
            r = v.co - centre; r = r - up * r.dot(up)
            cap = LENS_DEPTH if r.length > 1e-6 and r.normalized().dot(forward) > .6 else STRAP_DEPTH
            if t > cap: t = cap + (t - cap) * .2
            wrapped.append(hit + normal * (HEAD_CLEARANCE + t))
        # The pull itself is smoothed across the goggles, so the frame and lens move in as a piece rather than each point
        # its own way (unsmoothed, the frames came out crumpled); the push out of the fur afterwards catches what's left.
        move = [w - v.co for v, w in zip(verts, wrapped)]
        links = [[] for _ in verts]
        for e in gear.data.edges:
            i, j = e.vertices; links[i].append(j); links[j].append(i)
        for _ in range(40):
            move = [(m + sum((move[j] for j in links[i]), Vector()) / len(links[i])) / 2 if links[i] else m for i, m in enumerate(move)]
        for v, m in zip(verts, move): v.co = v.co + m
    fit = {'scale': s, 'plane': [list(front_point), list(back_point)], 'conform': [round(f, 3) for f in factor]}
else:
    # Round the neck where the game hangs its collar (RigNeck2, facing the head), sized to the neck's own cross-section.
    neck, head = bone(COLLAR_BONE), bone('RigHead')
    axis = (head - neck).normalized()
    side = Vector((1, 0, 0)); side = (side - axis * side.dot(axis)).normalized()
    other = axis.cross(side)
    # The neck's cross-section there: body points in a thin slice, near the axis (not the chest or shoulders behind it).
    us, ws = [], []
    for p in body_world:
        d = p - neck
        if abs(d.dot(axis)) < .02:
            r = d - axis * d.dot(axis)
            if r.length < .14: us.append(abs(r.dot(side))); ws.append(abs(r.dot(other)))
    us.sort(); ws.sort()
    # Snug: the neck itself (anything the band would still clip is pushed out of the fur afterwards).
    ru = us[int(len(us) * .95)] + .002; rw = ws[int(len(ws) * .95)] + .002
    # Where the game hangs the name tag (its collar's local -z, the collar turned from up to the neck's direction):
    # the buckle and D-ring go there.
    tag = Vector((0, 0, 1)).rotation_difference(axis) @ Vector((0, 1, 0))
    tag_angle = math.atan2(tag.dot(other), tag.dot(side))
    # The model's hardware sticks out furthest from its axis: its direction is where the buckle is.
    far = sorted(verts, key=lambda v: -(Vector((v.co.x, v.co.y)) - centre_xy).length)[:max(1, len(verts) // 100)]
    hw = sum((Vector((v.co.x, v.co.y)) - centre_xy for v in far), Vector((0, 0)))
    spin = tag_angle - math.atan2(hw.y, hw.x)
    cz = (lo.z + hi.z) / 2
    # Turned so its buckle points at the tag, the model's x and y axes swap roles at a quarter turn.
    q = abs(math.sin(spin))
    inner_u = inner_xy.x * (1 - q) + inner_xy.y * q; inner_w = inner_xy.y * (1 - q) + inner_xy.x * q
    sx, sy, sz = ru / inner_u, rw / inner_w, COLLAR_BAND / (hi.z - lo.z)
    for v in verts:
        x, y = v.co.x - centre_xy.x, v.co.y - centre_xy.y
        x, y = x * math.cos(spin) - y * math.sin(spin), x * math.sin(spin) + y * math.cos(spin)
        v.co = neck + side * (x * sx) + other * (y * sy) + axis * ((v.co.z - cz) * sz)
    # Then round the neck's own shape, which isn't an oval centred on the bone (the bone runs nearer the back of the neck,
    # so an oval hangs loose under the throat): every 15 degrees round it, the band's inside is brought to just on the fur
    # there (measured with a ray from the neck's centre), and its outside moves with it.
    BINS = 24
    def angle(r): return math.atan2(r.dot(other), r.dot(side)) % (2 * math.pi)
    inner_at = [None] * BINS
    for v in verts:
        d = v.co - neck; r = d - axis * d.dot(axis)
        b = int(angle(r) / (2 * math.pi) * BINS) % BINS
        if inner_at[b] is None or r.length < inner_at[b]: inner_at[b] = r.length
    fur_at = []
    for b in range(BINS):
        t = (b + .5) / BINS * 2 * math.pi
        hit, normal, index, dist = tree.ray_cast(neck, side * math.cos(t) + other * math.sin(t), .5)
        fur_at.append(dist if hit is not None else None)
    factor = [((fur_at[b] + .004) / inner_at[b]) if fur_at[b] and inner_at[b] else 1 for b in range(BINS)]
    for v in verts:
        d = v.co - neck; along = d.dot(axis); r = d - axis * along
        x = angle(r) / (2 * math.pi) * BINS - .5
        b0 = int(math.floor(x)) % BINS; w = x - math.floor(x)
        f = factor[b0] * (1 - w) + factor[(b0 + 1) % BINS] * w
        v.co = neck + axis * along + r * f
    fit = {'neck_radii': [ru, rw], 'inner': list(inner_xy), 'scale': [sx, sy, sz], 'spin_deg': math.degrees(spin), 'bone': COLLAR_BONE,
           'conform': [round(f, 3) for f in factor]}
gear.data.update()

# ---- snug (harnesses): the fit above only scales the whole vest, which left straps round the chest and the big vests'
# panel edges standing 3-7 cm off the dog. The underside is brought onto the fur everywhere, and each point moves with
# the underside beneath it (the closest the vest comes to the fur within 5 cm round it), so pouches, buckles and the
# handle keep their height above it. The pull is smoothed across the vest so pieces move in whole. ----
if kind == 'harness':
    near = []
    for v in verts:
        hit, normal, index, dist = tree.find_nearest(v.co)
        near.append((normal, (v.co - hit).dot(normal)))
    kd = KDTree(len(verts))
    for i, v in enumerate(verts): kd.insert(v.co, i)
    kd.balance()
    move = []
    for i, v in enumerate(verts):
        floor = min(near[j][1] for _, j, _ in kd.find_range(v.co, .05))
        move.append(-near[i][0] * max(0, floor - CLEARANCE))
    links = [[] for _ in verts]
    for e in gear.data.edges:
        i, j = e.vertices; links[i].append(j); links[j].append(i)
    for _ in range(20):
        move = [(m + sum((move[j] for j in links[i]), Vector()) / len(links[i])) / 2 if links[i] else m for i, m in enumerate(move)]
    for v, m in zip(verts, move): v.co = v.co + m
    gear.data.update()

# ---- whatever still dips into the fur is pushed out along the body's surface normal ----
pushed = 0
for v in verts:
    hit, normal, index, dist = tree.find_nearest(v.co)
    if hit is None: continue
    s = (v.co - hit).dot(normal)
    clear = HEAD_CLEARANCE if kind == 'head' else CLEARANCE
    if s < clear: v.co = v.co + normal * (clear - s); pushed += 1
gear.data.update()

# ---- skin weights: from the nearest point of the body, smoothed across the gear so it doesn't crease ----
group_bone = {g.index: names.index(g.name) for g in body.vertex_groups if g.name in names}
body_weights = []
for v in body.data.vertices:
    w = {}
    for g in v.groups:
        if g.group in group_bone and g.weight > 0: w[group_bone[g.group]] = w.get(group_bone[g.group], 0) + g.weight
    body_weights.append(w)
weights = []
for v in verts:
    hit, normal, index, dist = tree.find_nearest(v.co)
    tri = tris[index]
    bary = poly_3d_calc([body_world[i] for i in tri], hit)
    w = {}
    for corner, f in zip(tri, bary):
        for b, x in body_weights[corner].items(): w[b] = w.get(b, 0) + x * f
    weights.append(w)
if kind == 'head':
    # Rigid on the skull: the head carries goggles whole (weights from the face would let the ears and jaw bend them).
    weights = [{names.index('RigHead'): 1.0} for _ in verts]
neighbours = [[] for _ in verts]
for e in gear.data.edges:
    a, b = e.vertices; neighbours[a].append(b); neighbours[b].append(a)
for _ in range(6 if kind == 'collar' else 4):
    smoothed = []
    for i, w in enumerate(weights):
        if not neighbours[i]: smoothed.append(dict(w)); continue
        mix = dict((b, x * .5) for b, x in w.items())
        share = .5 / len(neighbours[i])
        for j in neighbours[i]:
            for b, x in weights[j].items(): mix[b] = mix.get(b, 0) + x * share
        smoothed.append(mix)
    weights = smoothed

def top4(w):
    best4 = sorted(((x, b) for b, x in w.items() if x > 0), reverse=True)[:4]
    total = sum(x for x, b in best4)
    assert total > 0, 'Unweighted vertex'
    ws = [x / total for x, b in best4]; bs = [b for x, b in best4]
    while len(ws) < 4: ws.append(0); bs.append(0)
    return bs, ws

# ---- write: one vertex per distinct (position, uv, normal) ----
mesh = gear.data
mesh.calc_loop_triangles()
uv_layer = mesh.uv_layers.active.data
normal_matrix = C.to_3x3().inverted().transposed()
skins = [top4(w) for w in weights]
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
            out_vertices.append((C @ mesh.vertices[loop.vertex_index].co, n, uv, skins[loop.vertex_index]))
        ids.append(index_of[k])
    a, b, c = ids
    triangles.extend((a, c, b))

os.makedirs(dest, exist_ok=True)
with open(os.path.join(dest, 'gear_%s.mesh' % key), 'wb') as f:
    def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
    def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
    ints(0x31544b4a)   # 'JKT1'
    raw = key.encode(); ints(len(raw)); f.write(raw)
    ints(len(out_vertices))
    for p, n, uv, (bs, ws) in out_vertices:
        floats(*p, *n, *uv); ints(*bs); floats(*ws)
    ints(1); ints(len(triangles)); ints(*triangles)

# The base colour, at 2048 (the 4K original is far more than a dog's harness shows on screen).
mat = gear.active_material
image = next(n.image for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image and 'basecolor' in n.image.name.lower())
image.scale(2048, 2048)
bpy.context.scene.render.image_settings.quality = 88
image.filepath_raw = os.path.join(dest, 'gear_%s.jpg' % key); image.file_format = 'JPEG'
image.save()

inside = sum(1 for v in verts if signed(v.co) < 0)
stats = {'key': key, 'source_triangles': source_tris, 'triangles': len(triangles) // 3, 'vertices': len(out_vertices),
         'fragments_removed': removed, 'pushed_out': pushed, 'still_inside': inside, 'fit': fit,
         'bones_used': sorted({names[b] for _, _, _, (bs, ws) in out_vertices for b, w in zip(bs, ws) if w > .05})}
print('GEAR_CONVERSION_OK', json.dumps(stats))

# ---- preview: the gear on the dog, from the side and three-quarters ----
if renders:
    os.makedirs(renders, exist_ok=True)
    tex = bpy.data.images.load(os.path.join(source, 'GermanShepherd_BaseColor.tga'))
    fur = bpy.data.materials.new('fur'); fur.use_nodes = True
    node = fur.node_tree.nodes.new('ShaderNodeTexImage'); node.image = tex
    fur.node_tree.links.new(node.outputs[0], fur.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    body.data.materials.clear(); body.data.materials.append(fur)
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE'
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    world = bpy.data.worlds.new('w'); scene.world = world; world.use_nodes = True
    world.node_tree.nodes['Background'].inputs[0].default_value = (.4, .4, .42, 1)
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); scene.collection.objects.link(sun)
    sun.data.energy = 3.5; sun.rotation_euler = (math.radians(45), 0, math.radians(-30))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
    target = {'collar': bone('RigNeck2'), 'head': bone('RigHead')}.get(kind, bone('RigChest'))
    span = {'collar': .55, 'head': .4}.get(kind, 1.0)
    cam.data.lens = 50
    for name, d in {'side': Vector((1, 0, .1)), 'angle': Vector((.8, -.9, .5)), 'front': Vector((0, -1, .25))}.items():
        cam.location = target + d.normalized() * span * 2.2
        cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(renders, '%s_%s.png' % (key, name))
        bpy.ops.render.render(write_still=True)
    print('GEAR_RENDERS', renders)
