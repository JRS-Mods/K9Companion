import bpy, bmesh, os, sys, json, math
import numpy as np
import shutil, struct
from mathutils import Vector, Matrix

# The K9 item models from E:\SPT Mod\Assets\New Dog Assets (GLBs, 440k-490k triangles, 4k maps): each
# label/logo PNG is projected onto the full-detail model (wrapped round the can, flat on the others), then colour,
# normal and metal/roughness are baked from that model onto a decimated copy, so the print stays sharp and the fine
# relief the decimation removes lives on in the normal map. Writes work/dog_items/<key>/ (<key>.blend with both
# versions, <key>.jpg, <key>_n.png, <key>_ms.png - metallic in red, smoothness in alpha, as PropModel reads) and the
# game's copy in assets/: gear_item_<key>.mesh (JKT1, as convert_item_models.py writes, at real size, front to Unity +z),
# its three maps under the same name, and icon_item_<key>.png (GearVisuals and DogGearPatches show them for the items).
#   blender -b -P convert_dog_items.py -- [key ...]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = os.path.join(os.environ.get('DOG_PROP_ASSETS') or os.path.join(os.path.dirname(root), 'Assets'), 'New Dog Assets')
work = os.path.join(root, 'work', 'dog_items')
args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []

# Placement is in the source's own units (the source's ~1 m box, standing on z=0, front facing -y): plane decals are centred
# at (x, z) on the front and `width` wide (the artwork's opaque part); the can's label runs round it between z0 and z1.
# style: print (paper label), paint (stencil on the vet kit's plate), patch (printed on the bag's patch), engrave (tag).
# size: the real thing's longest side in metres (the can's height), as the game shows it in the inspect window and on
# the ground; cells: the stash size of the item that uses it, for its icon (icon_item_<key>.png); more_cells: other items
# that share the model at another size (the 1x1 vet supply case), each icon_item_<key>_<w>x<h>.png.
ITEMS = {
    'can':    dict(glb='Can/rusty metal can 3d model.glb', budget=5000, art='Can/Vintage K9 Working Dog Food Label.png',
                   wrap=dict(copies=3, z0=.10, z1=.86), style='print', grime=.2, checker=True, unbend=True, size=.11, cells=(1, 1)),
    'meat':   dict(glb='Meat/beef steak 3d model.glb', budget=6000, art='Meat/Vintage K9 Dog Meat Ration Label.png',
                   at=(0, .37), width=.56, style='print', grime=0, checker=True, size=.2, cells=(1, 1)),
    'duffel': dict(glb='Duffel Bag/tactical bag 3d model.glb', budget=14000, art='Duffel Bag/Textured Beige K9 Training Kit Logo.png',
                   at=(.01, .34), width=.25, style='patch', grime=.2, size=.55, cells=(3, 2)),
    'tag':    dict(glb='K9 Tag/bone tag 3d model.glb', budget=5000, art='K9 Tag/K9 Industrial QR Tag Graphic.png',
                   at=(0, .28), width=.56, style='engrave', grime=0, size=.045, cells=(1, 1), view=(.35, -1.7, .45)),
    'vetkit': dict(glb='Vet Kit/Vet Kit 3d model.glb', budget=12000, art='Vet Kit/Distressed Beige K9 Veterinary Supplies Logo.png',
                   at=(0, .225), width=.34, style='paint', grime=.35, size=.42, cells=(2, 1), more_cells=[(1, 1)]),
}
SIZE = 2048
assets_out = os.path.join(root, 'assets')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (as convert_item_models.py)

def pixels(image):
    w, h = image.size
    return np.array(image.pixels[:], np.float32).reshape(h, w, 4)   # row 0 is the bottom

def dilate(m):
    d = m.copy()
    d[1:] |= m[:-1]; d[:-1] |= m[1:]; d[:, 1:] |= m[:, :-1]; d[:, :-1] |= m[:, 1:]
    return d

def blur(a):
    p = np.pad(a, 1, mode='edge')
    return sum(p[1 + dy:p.shape[0] - 1 + dy, 1 + dx:p.shape[1] - 1 + dx] for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9

# The two labels came with a transparency checkerboard painted in: flood it away from the border (it is grey/white;
# the paper is tan, so the fill stops at the label's torn edge).
def remove_checker(px):
    rgb = px[..., :3]
    grey = (rgb.max(-1) - rgb.min(-1) < .07) & (rgb.max(-1) > .75)
    bg = np.zeros_like(grey)
    bg[0], bg[-1], bg[:, 0], bg[:, -1] = grey[0], grey[-1], grey[:, 0], grey[:, -1]
    while True:
        grown = dilate(bg) & grey
        if (grown == bg).all(): break
        bg = grown
    px[..., 3] = blur((~bg).astype(np.float32))
    return px

# Generated logos have coloured fringes in their half-transparent edge: the faint part goes, the rest takes the dark
# outline's colour.
def clean_edges(px):
    a = px[..., 3]
    px[..., 3] = np.clip((a - .35) / .5, 0, 1)
    edge = (a > .05) & (a < .9)
    px[edge, :3] = np.array([.05, .04, .03], np.float32)
    return px

# The can label is drawn as seen on a can (top and bottom edges bowed): stretch each column so both run straight.
def unbend(px):
    a = px[..., 3] > .5
    cols = np.where(a.sum(0) > a.shape[0] * .3)[0]
    xs = np.arange(cols[0], cols[-1] + 1)
    lo = np.array([np.argmax(a[:, x]) for x in xs], np.float32)
    hi = np.array([a.shape[0] - 1 - np.argmax(a[::-1, x]) for x in xs], np.float32)
    lo = np.polyval(np.polyfit(xs, lo, 2), xs); hi = np.polyval(np.polyfit(xs, hi, 2), xs)
    height = int(np.median(hi - lo))
    out = np.zeros((height, len(xs), 4), np.float32)
    t = np.linspace(0, 1, height)
    for i, x in enumerate(xs):
        src = lo[i] + t * (hi[i] - lo[i])
        i0 = np.clip(np.floor(src).astype(int), 0, a.shape[0] - 1); i1 = np.clip(i0 + 1, 0, a.shape[0] - 1); f = (src - i0)[:, None]
        out[:, i] = px[i0, x] * (1 - f) + px[i1, x] * f
    return out

def crop(px):
    a = px[..., 3] > .1
    ys, xs = np.where(a.any(1))[0], np.where(a.any(0))[0]
    return px[ys[0]:ys[-1] + 1, xs[0]:xs[-1] + 1]

def make_image(name, px, linear=False):
    h, w = px.shape[:2]
    im = bpy.data.images.new(name, w, h, alpha=True)
    if linear: im.colorspace_settings.name = 'Non-Color'
    im.pixels[:] = px.ravel()
    im.pack()
    return im

def artwork(key, spec):
    im = bpy.data.images.load(os.path.join(assets, spec['art']))
    px = pixels(im)
    px = remove_checker(px) if spec.get('checker') else clean_edges(px)
    if spec.get('unbend'): px = unbend(px)
    return crop(px)

def triangles(o): return sum(len(p.vertices) - 2 for p in o.data.polygons)

def load_source(spec):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(assets, spec['glb']))
    o = next(x for x in bpy.context.scene.objects if x.type == 'MESH')
    for x in list(bpy.context.scene.objects):
        if x is not o: bpy.data.objects.remove(x)
    bpy.ops.object.select_all(action='DESELECT'); o.select_set(True); bpy.context.view_layer.objects.active = o
    o.parent = None   # as convert_kennel_models.py: the source's root node only offsets it
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o

# A second UV map, 'decal', puts the artwork on the faces it covers; every other face points outside it.
def project(o, spec, art):
    me = o.data
    layer = me.uv_layers.new(name='decal')
    h, w = art.shape[:2]
    outside = (-5, -5)
    if 'wrap' in spec:
        wr, top = spec['wrap'], max(v.co.z for v in me.vertices)
        # One tile of the texture is one copy of the label, padded above and below so v runs over the whole can.
        label_h = wr['z1'] - wr['z0']
        for poly in me.polygons:
            n = poly.normal
            inside = abs(n.z) < .5 and wr['z0'] - .01 < poly.center.z < wr['z1'] + .01
            us = []
            for li in poly.loop_indices:
                p = me.vertices[me.loops[li].vertex_index].co
                us.append(wr['copies'] * (math.atan2(p.y, p.x) + math.pi / 2) / (2 * math.pi) + .5)
            if max(us) - min(us) > wr['copies'] / 2:
                us = [u + wr['copies'] if u < wr['copies'] / 2 else u for u in us]
            for li, u in zip(poly.loop_indices, us):
                z = me.vertices[me.loops[li].vertex_index].co.z
                layer.data[li].uv = (u, (z - wr['z0']) / label_h) if inside else outside
        return
    cx, cz = spec['at']; width = spec['width']; height = width * h / w
    # The surface point in front of (cx, cz), and the average outward direction of the front around it (the meat's pack
    # leans back, so the label is laid along its face rather than straight along y).
    near = [p for p in me.polygons if p.normal.y < -.3 and abs(p.center.x - cx) < width / 2 and abs(p.center.z - cz) < height / 2]
    front = min(p.center.y for p in near if abs(p.center.x - cx) < .03 and abs(p.center.z - cz) < .03)
    near = [p for p in near if p.center.y < front + .05]
    d = sum((p.normal * p.area for p in near), Vector()).normalized()
    right = (Vector((1, 0, 0)) - d * d.x).normalized(); up = d.cross(right)
    origin = Vector((cx, front, cz))
    for poly in me.polygons:
        ok = poly.normal.dot(d) > .3 and (poly.center - origin).dot(d) > -.05
        for li in poly.loop_indices:
            q = me.vertices[me.loops[li].vertex_index].co - origin
            layer.data[li].uv = (q.dot(right) / width + .5, q.dot(up) / height + .5) if ok else outside

# the source's material: basecolor -> Base Color, rm -> Separate Color (G roughness, B metallic), normal -> Normal Map.
# Adds the artwork on top and returns the sockets each bake pass needs.
def decorate(o, spec, art_image):
    mat = o.active_material; nt = mat.node_tree; N, L = nt.nodes, nt.links
    bsdf = next(n for n in N if n.type == 'BSDF_PRINCIPLED')
    out = next(n for n in N if n.type == 'OUTPUT_MATERIAL')
    base = bsdf.inputs['Base Color'].links[0].from_socket
    rough = bsdf.inputs['Roughness'].links[0].from_socket
    metal = bsdf.inputs['Metallic'].links[0].from_socket
    normal = bsdf.inputs['Normal'].links[0].from_socket
    base_image = bsdf.inputs['Base Color'].links[0].from_node.image
    lum = pixels(base_image)[..., :3] @ np.array([.2126, .7152, .0722], np.float32)

    def node(kind, **kw):
        n = N.new(kind)
        for k, v in kw.items(): setattr(n, k, v)
        return n
    def math_node(op, a, b, clamp=False):
        n = node('ShaderNodeMath', operation=op, use_clamp=clamp)
        for s, v in zip(n.inputs, (a, b)):
            if isinstance(v, (int, float)): s.default_value = v
            else: L.new(v, s)
        return n.outputs[0]
    def mix(fac, a, b, kind='RGBA', blend='MIX'):
        n = node('ShaderNodeMix', data_type=kind, blend_type=blend)
        t = {'RGBA': 'RGBA', 'FLOAT': 'VALUE'}[kind]
        pick = lambda name: next(s for s in n.inputs if s.name == name and s.type == t)
        for s, v in ((n.inputs['Factor'], fac), (pick('A'), a), (pick('B'), b)):
            if isinstance(v, (int, float, tuple)): s.default_value = v
            else: L.new(v, s)
        return next(s for s in n.outputs if s.type == t)

    uv = node('ShaderNodeUVMap', uv_map='decal')
    tex = node('ShaderNodeTexImage', image=art_image, interpolation='Cubic', extension='REPEAT' if 'wrap' in spec else 'CLIP')
    L.new(uv.outputs[0], tex.inputs[0])
    a = tex.outputs['Alpha']; art = tex.outputs['Color']
    style = spec['style']
    if style == 'engrave':
        color = mix(math_node('MULTIPLY', a, .75), base, (.02, .018, .015, 1))
        new_rough = mix(math_node('MULTIPLY', a, .6), rough, .85, 'FLOAT')
        new_metal = metal
        height, strength, invert = a, .9, True
    else:
        # A little of the surface's own light/dark under the print (dirt, scratches), then the print over the surface.
        g = spec['grime']
        bw = node('ShaderNodeRGBToBW'); L.new(base, bw.inputs[0])
        shade = math_node('DIVIDE', bw.outputs[0], float(lum.mean()))
        shade = mix(g, 1.0, math_node('MINIMUM', math_node('MAXIMUM', shade, .55), 1.25), 'FLOAT')
        sh = node('ShaderNodeCombineColor')
        for i in range(3): L.new(shade, sh.inputs[i])
        color = mix(a, base, mix(1.0, art, sh.outputs[0], blend='MULTIPLY'))
        paper = {'print': .7, 'paint': .65, 'patch': .9}[style]
        new_rough = mix(a, rough, paper, 'FLOAT')
        new_metal = mix(a, metal, 0.0, 'FLOAT')
        height, strength, invert = a, {'print': .25, 'paint': .3, 'patch': .5}[style], False
    bump = node('ShaderNodeBump', invert=invert)
    bump.inputs['Strength'].default_value = strength; bump.inputs['Distance'].default_value = .002
    L.new(height, bump.inputs['Height']); L.new(normal, bump.inputs['Normal'])
    L.new(color, bsdf.inputs['Base Color']); L.new(new_rough, bsdf.inputs['Roughness']); L.new(new_metal, bsdf.inputs['Metallic'])
    L.new(bump.outputs[0], bsdf.inputs['Normal'])
    emit = node('ShaderNodeEmission')
    ms = node('ShaderNodeCombineColor'); L.new(new_metal, ms.inputs[0]); L.new(new_rough, ms.inputs[1])
    return dict(tree=nt, out=out, bsdf=bsdf, emit=emit, color=color, ms=ms.outputs[0])

def decimate(src, budget):
    o = src.copy(); o.data = src.data.copy(); o.name = src.name + '_low'
    bpy.context.scene.collection.objects.link(o)
    o.data.uv_layers.remove(o.data.uv_layers['decal'])
    bm = bmesh.new(); bm.from_mesh(o.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
    bm.to_mesh(o.data); bm.free()
    dec = o.modifiers.new('decimate', 'DECIMATE')
    dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1, budget / triangles(o)); dec.use_collapse_triangulate = True
    bpy.ops.object.select_all(action='DESELECT'); o.select_set(True); bpy.context.view_layer.objects.active = o
    bpy.ops.object.modifier_apply(modifier=dec.name)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    # Fresh UVs: collapsing the welded seams stretched the source's own layout into streaks through the print, and
    # everything is baked onto this copy anyway.
    while o.data.uv_layers: o.data.uv_layers.remove(o.data.uv_layers[0])
    o.data.uv_layers.new(name='UVMap')
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=.004)
    bpy.ops.object.mode_set(mode='OBJECT')
    return o

def bake(high, low, key, sockets):
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'HIP'; prefs.get_devices()
        for dv in prefs.devices: dv.use = dv.type != 'CPU'
        sc.cycles.device = 'GPU'
    except Exception: pass
    mat = bpy.data.materials.new(key + '_baked'); mat.use_nodes = True
    low.data.materials.clear(); low.data.materials.append(mat)
    target = mat.node_tree.nodes.new('ShaderNodeTexImage'); mat.node_tree.nodes.active = target
    bk = sc.render.bake
    bk.use_selected_to_active = True; bk.cage_extrusion = .006; bk.max_ray_distance = .03; bk.margin = 8
    bpy.ops.object.select_all(action='DESELECT'); high.select_set(True); low.select_set(True)
    bpy.context.view_layer.objects.active = low
    nt, L = sockets['tree'], sockets['tree'].links
    images = {}
    for name, kind, src, linear in (('color', 'EMIT', sockets['color'], False), ('ms', 'EMIT', sockets['ms'], True),
                                    ('normal', 'NORMAL', None, True)):
        im = bpy.data.images.new(f'{key}_{name}', SIZE, SIZE, alpha=False)
        if linear: im.colorspace_settings.name = 'Non-Color'
        target.image = im
        if kind == 'EMIT':
            L.new(src, sockets['emit'].inputs['Color']); L.new(sockets['emit'].outputs[0], sockets['out'].inputs['Surface'])
            sc.cycles.samples = 1
        else:
            L.new(sockets['bsdf'].outputs[0], sockets['out'].inputs['Surface'])
            sc.cycles.samples = 4
        bpy.ops.object.bake(type=kind, normal_space='TANGENT')
        im.pack()
        images[name] = im
    L.new(sockets['bsdf'].outputs[0], sockets['out'].inputs['Surface'])
    return mat, images

# The low copy's own material, as the game's Standard shader will see it.
def finish_material(mat, images):
    nt = mat.node_tree; N, L = nt.nodes, nt.links
    for n in list(N):
        if n.type == 'TEX_IMAGE': N.remove(n)
    bsdf = N['Principled BSDF']
    c = N.new('ShaderNodeTexImage'); c.image = images['color']; L.new(c.outputs[0], bsdf.inputs['Base Color'])
    m = N.new('ShaderNodeTexImage'); m.image = images['ms']
    sep = N.new('ShaderNodeSeparateColor'); L.new(m.outputs[0], sep.inputs[0])
    L.new(sep.outputs[0], bsdf.inputs['Metallic']); L.new(sep.outputs[1], bsdf.inputs['Roughness'])
    nm = N.new('ShaderNodeTexImage'); nm.image = images['normal']
    nmap = N.new('ShaderNodeNormalMap'); L.new(nm.outputs[0], nmap.inputs['Color']); L.new(nmap.outputs[0], bsdf.inputs['Normal'])

def save_maps(key, images):
    folder = os.path.join(work, key); os.makedirs(folder, exist_ok=True)
    c = images['color']; c.filepath_raw = os.path.join(folder, key + '.jpg'); c.file_format = 'JPEG'; c.save()
    n = images['normal']; n.filepath_raw = os.path.join(folder, key + '_n.png'); n.file_format = 'PNG'; n.save()
    px = pixels(images['ms']); out = np.zeros_like(px)
    out[..., 0] = out[..., 1] = out[..., 2] = px[..., 0]; out[..., 3] = 1 - px[..., 1]
    ms = bpy.data.images.new(key + '_ms_unity', SIZE, SIZE, alpha=True); ms.colorspace_settings.name = 'Non-Color'
    ms.pixels[:] = out.ravel(); ms.filepath_raw = os.path.join(folder, key + '_ms.png'); ms.file_format = 'PNG'; ms.save()
    for suffix in ('.jpg', '_n.png', '_ms.png'):
        shutil.copyfile(os.path.join(folder, key + suffix), os.path.join(assets_out, f'gear_item_{key}{suffix}'))

# The game's copy: one vertex per distinct (position, uv, normal), every vertex on bone 0 (as the gear's item meshes).
def write_mesh(o, key, size):
    mesh = o.data
    lo = Vector(map(min, *[v.co for v in mesh.vertices])); hi = Vector(map(max, *[v.co for v in mesh.vertices]))
    s = size / (hi.z - lo.z if key == 'can' else max(hi - lo))
    place = Matrix.Scale(s, 4) @ Matrix.Translation(-(lo + hi) / 2)
    mesh.calc_loop_triangles()
    uv = mesh.uv_layers.active.data
    normal_matrix = C.to_3x3()
    index_of, out, triangles = {}, [], []
    for tri in mesh.loop_triangles:
        ids = []
        for li in tri.loops:
            n = (normal_matrix @ mesh.corner_normals[li].vector).normalized()
            t = uv[li].uv
            k = (mesh.loops[li].vertex_index, round(t.x, 5), round(t.y, 5), round(n.x, 3), round(n.y, 3), round(n.z, 3))
            if k not in index_of:
                index_of[k] = len(out)
                out.append((C @ place @ mesh.vertices[mesh.loops[li].vertex_index].co, n, (t.x, t.y)))
            ids.append(index_of[k])
        a, b, c = ids
        triangles += [a, c, b]
    with open(os.path.join(assets_out, f'gear_item_{key}.mesh'), 'wb') as f:
        def ints(*a): f.write(struct.pack('<' + 'i' * len(a), *a))
        def floats(*a): f.write(struct.pack('<' + 'f' * len(a), *a))
        ints(0x31544b4a)   # 'JKT1'
        raw = ('item_' + key).encode(); ints(len(raw)); f.write(raw)
        ints(len(out))
        for p, n, t in out:
            floats(*p, *n, *t); ints(0, 0, 0, 0); floats(1, 0, 0, 0)
        ints(1); ints(len(triangles)); ints(*triangles)
    return [round((hi - lo)[i] * s, 3) for i in range(3)]

# The stash icon, as tools/render_gear_icons.py draws the gear's: three-quarter view, two area lights, transparent, at
# twice the game's size for its cells.
def render_icon(o, key, spec, cells, name):
    sc = bpy.context.scene
    for x in list(sc.objects):
        if x.type in ('LIGHT', 'CAMERA'): bpy.data.objects.remove(x)
    sc.cycles.samples = 64
    sc.render.film_transparent = True; sc.view_settings.view_transform = 'Standard'
    sc.render.image_settings.file_format = 'PNG'; sc.render.image_settings.color_mode = 'RGBA'
    sc.world = bpy.data.worlds.new('icon'); sc.world.use_nodes = True
    sc.world.node_tree.nodes['Background'].inputs[0].default_value = (.3, .3, .3, 1)
    pts = [o.matrix_world @ v.co for v in o.data.vertices]
    lo = Vector(map(min, *pts)); hi = Vector(map(max, *pts)); centre = (lo + hi) / 2
    def look(ob, target): ob.rotation_euler = (target - ob.location).to_track_quat('-Z', 'Y').to_euler()
    for location, energy in (((1.2, -1.0, 1.6), 90), ((-1.4, -.6, 1.0), 40)):
        light = bpy.data.objects.new('light', bpy.data.lights.new('light', 'AREA')); sc.collection.objects.link(light)
        light.data.energy = energy; light.data.size = 1.5; light.location = centre + Vector(location); look(light, centre)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.type = 'ORTHO'
    cam.location = centre + Vector(spec.get('view', (1.1, -1.6, .9))); look(cam, centre)
    bpy.context.view_layer.update()
    right, up = cam.matrix_world.to_3x3() @ Vector((1, 0, 0)), cam.matrix_world.to_3x3() @ Vector((0, 1, 0))
    across = max(p.dot(right) for p in pts) - min(p.dot(right) for p in pts)
    tall = max(p.dot(up) for p in pts) - min(p.dot(up) for p in pts)
    W, H = (2 * (c * 63 + 1) for c in cells)
    sc.render.resolution_x, sc.render.resolution_y = W, H
    cam.data.ortho_scale = 1.08 * (max(across, tall * W / H) if W >= H else max(tall, across * H / W))
    sc.render.filepath = os.path.join(assets_out, name)
    bpy.ops.render.render(write_still=True)

stats = []
for key in (args or ITEMS):
    spec = ITEMS[key]
    high = load_source(spec)
    source_tris = triangles(high)
    art = artwork(key, spec)
    untouched = high.active_material.copy()   # the source as it came, for the comparison renders
    project(high, spec, art)
    art_image = make_image(key + '_art', art)
    os.makedirs(os.path.join(work, key), exist_ok=True)
    art_image.filepath_raw = os.path.join(work, key, key + '_art.png'); art_image.file_format = 'PNG'; art_image.save()
    sockets = decorate(high, spec, art_image)
    low = decimate(high, spec['budget'])
    mat, images = bake(high, low, key, sockets)
    finish_material(mat, images)
    save_maps(key, images)
    high.data.materials[0] = untouched
    high.name = key + '_source'; low.name = key
    high.hide_render = True; high.hide_set(True)
    folder = os.path.join(work, key)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, key + '.blend'), compress=True)
    render_icon(low, key, spec, spec['cells'], f'icon_item_{key}.png')
    for w, h in spec.get('more_cells', []): render_icon(low, key, spec, (w, h), f'icon_item_{key}_{w}x{h}.png')
    size = write_mesh(low, key, spec['size'])
    stats.append({'key': key, 'source_triangles': source_tris, 'triangles': triangles(low), 'size_m': size})
    print('DOG_ITEM', json.dumps(stats[-1]), flush=True)
print('DOG_ITEMS_OK', json.dumps(stats))
