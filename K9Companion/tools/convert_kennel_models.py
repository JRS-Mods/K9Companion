import bpy, bmesh, os, sys, struct, json, math
import numpy as np
from mathutils import Matrix, Vector

# Builds assets/kennel_models.props (+ kennel_<name>.jpg) from the generated kennel props in
# E:\SPT Mod\Assets\Updated Kennel Props (GLBs, 550k-920k triangles each): welded, decimated to a hideout budget,
# scaled from the source's unit box to real size, stood on the floor and centred. Same 'PRP1' format as kennel.props
# (PropModel.cs reads either). Optionally renders them side by side.
#   blender -b -P convert_kennel_models.py -- [preview png]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = os.environ.get('DOG_PROP_ASSETS') or os.path.join(os.path.dirname(root), 'Assets')
source = os.path.join(assets, 'Updated Kennel Props')
dest = os.path.join(root, 'assets')
args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
preview = args[0] if args else None
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (same as the dog)

# name: file, triangles, longest side in metres (sized round the dog ~0.62 m at the shoulder), texture size
PROPS = {
    # military K9 kennel (level 3), with its own bed: seen up close at a man's height, so its panels, frame and mesh door
    # need far more triangles than the small pieces (at 9000 they broke into facets and lumps)
    'crate':    ('K9 LVL3 crate-model.glb', 60000, 1.10, 2048),
    # big enough to hold the dog's bed (the game stretches it round the bed; this is only its starting size), and enough
    # triangles that the planks don't break up into facets
    'doghouse': ('wooden dog house 3d model.glb', 24000, 1.45, 2048),
    'toybox':   ('rusty dog crate 3d model.glb', 9000, .62, 2048),      # ammo box of toys: the toys need the triangles
    'foodbag':  ('dog food bag 3d model.glb', 6000, .62, 2048),         # 15 kg sack; fewer triangles smear its print
}

def convert(name, file, budget, longest):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(source, file))
    o = next(x for x in bpy.context.scene.objects if x.type == 'MESH')
    bpy.ops.object.select_all(action='DESELECT'); o.select_set(True); bpy.context.view_layer.objects.active = o
    o.parent = None
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    source_tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
    # glTF splits vertices along texture seams; welding them lets the decimator see one surface (UVs live on corners).
    # Not on the bag: its print crosses seams, and collapsing across them smears letters.
    if name != 'foodbag':
        bm = bmesh.new(); bm.from_mesh(o.data)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
        bm.to_mesh(o.data); bm.free()
    dec = o.modifiers.new('decimate', 'DECIMATE')
    dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1, budget / source_tris); dec.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier=dec.name)
    # Smooth shading, but sharp where faces meet at an angle (plank edges, corners): the decimated triangles otherwise
    # show as facets of light and dark.
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    mesh = o.data
    lo = Vector(map(min, *[v.co for v in mesh.vertices])); hi = Vector(map(max, *[v.co for v in mesh.vertices]))
    s = longest / max(hi - lo)
    mesh.transform(Matrix.Scale(s, 4) @ Matrix.Translation(-Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))))
    mesh.update()
    image = next(n.image for n in o.active_material.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image and 'basecolor' in n.image.name.lower())
    return o, image, source_tris, list((hi - lo) * s)

# The model's other two maps, beside its colour (kennel_<name>.jpg), for the game's Standard material (PropModel):
# kennel_<name>_n.png, the normal map (the fine relief the decimated mesh no longer has), and kennel_<name>_ms.png,
# metallic in red and smoothness (1 - glTF roughness) in alpha, from glTF's roughness (green) / metallic (blue) map.
def write_maps(o, name, size):
    tree = o.active_material.node_tree
    normal = next((l.from_node.image for l in tree.links if l.to_node.type == 'NORMAL_MAP' and l.from_node.type == 'TEX_IMAGE'), None)
    rm = next((n.image for n in tree.nodes if n.type == 'TEX_IMAGE' and n.image and '_rm' in n.image.name.lower()), None)
    if normal is not None:
        if max(normal.size) > size: normal.scale(size, size)
        normal.filepath_raw = os.path.join(dest, 'kennel_%s_n.png' % name); normal.file_format = 'PNG'; normal.save()
    if rm is not None:
        side = min(1024, max(rm.size))
        if max(rm.size) > side: rm.scale(side, side)
        w, h = rm.size
        px = np.array(rm.pixels[:], dtype=np.float32).reshape(-1, 4)
        out = np.zeros_like(px)
        out[:, 0] = out[:, 1] = out[:, 2] = px[:, 2]; out[:, 3] = 1 - px[:, 1]
        ms = bpy.data.images.new(name + '_ms', w, h, alpha=True)
        ms.colorspace_settings.name = 'Non-Color'
        ms.pixels[:] = out.ravel()
        ms.filepath_raw = os.path.join(dest, 'kennel_%s_ms.png' % name); ms.file_format = 'PNG'; ms.save()
    return normal is not None, rm is not None

def write_mesh(f, name, o, tex):
    mesh = o.data; mesh.calc_loop_triangles()
    uv = mesh.uv_layers.active.data
    def text(s): raw = s.encode('utf8'); f.write(struct.pack('<i', len(raw))); f.write(raw)
    text(name); text(tex)
    # One vertex per distinct (position, uv, normal): corners that share all three share a vertex (a vertex per corner
    # made the 60k-triangle crate 180k vertices).
    normal_matrix = C.to_3x3()
    index_of, out, corner = {}, [], {}
    for loop in mesh.loops:
        n = (normal_matrix @ mesh.corner_normals[loop.index].vector).normalized()
        t = uv[loop.index].uv
        k = (loop.vertex_index, round(t.x, 5), round(t.y, 5), round(n.x, 3), round(n.y, 3), round(n.z, 3))
        if k not in index_of:
            index_of[k] = len(out)
            p = C @ mesh.vertices[loop.vertex_index].co
            out.append((p.x, p.y, p.z, n.x, n.y, n.z, t.x, t.y))
        corner[loop.index] = index_of[k]
    f.write(struct.pack('<i', len(out)))
    for v in out: f.write(struct.pack('<8f', *v))
    tris = []
    for tri in mesh.loop_triangles: a, b, c = (corner[i] for i in tri.loops); tris += [a, c, b]
    f.write(struct.pack('<i', len(tris))); f.write(struct.pack('<%di' % len(tris), *tris))
    return len(tris) // 3

# Each prop is converted in a fresh scene, so its mesh is written out straight away.
stats = []
with open(os.path.join(dest, 'kennel_models.props'), 'wb') as f:
    f.write(struct.pack('<ii', 0x31505250, len(PROPS)))
    for name, (file, budget, longest, size) in PROPS.items():
        o, image, source_tris, dims = convert(name, file, budget, longest)
        tex = 'kennel_%s.jpg' % name
        if max(image.size) > size: image.scale(size, size)
        image.filepath_raw = os.path.join(dest, tex); image.file_format = 'JPEG'; image.save()
        has_normal, has_ms = write_maps(o, name, size)
        stats.append({'name': name, 'source_triangles': source_tris, 'triangles': write_mesh(f, name, o, tex),
                      'size_m': [round(x, 3) for x in dims], 'normal_map': has_normal, 'metal_smooth_map': has_ms})
print('KENNEL_MODELS_OK', json.dumps(stats))

if preview:
    # Read back what was written (as the game will), lined up on a floor.
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    Ci = C.inverted()
    x = 0
    with open(os.path.join(dest, 'kennel_models.props'), 'rb') as f:
        def ints(n=1): return struct.unpack('<' + 'i' * n, f.read(4 * n))
        def text(): return f.read(ints()[0]).decode('utf8')
        assert ints()[0] == 0x31505250
        for _ in range(ints()[0]):
            name, tex = text(), text()
            raw = [struct.unpack('<8f', f.read(32)) for _ in range(ints()[0])]
            idx = ints(ints()[0])
            m = bpy.data.meshes.new(name)
            m.from_pydata([Ci @ Vector(v[:3]) for v in raw], [], [(idx[i], idx[i + 2], idx[i + 1]) for i in range(0, len(idx), 3)])
            m.update()
            layer = m.uv_layers.new()
            for loop in m.loops: layer.data[loop.index].uv = raw[loop.vertex_index][6:8]
            for p in m.polygons: p.use_smooth = True
            mat = bpy.data.materials.new(name); mat.use_nodes = True
            node = mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(os.path.join(dest, tex))
            mat.node_tree.links.new(node.outputs[0], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
            mat.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value = .85
            m.materials.append(mat)
            ob = bpy.data.objects.new(name, m); scene.collection.objects.link(ob)
            width = max(v.co.x for v in m.vertices) - min(v.co.x for v in m.vertices)
            ob.location.x = x + width / 2; x += width + .25
    bpy.ops.mesh.primitive_plane_add(size=20, location=(x / 2, 0, 0))
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); scene.collection.objects.link(sun)
    sun.data.energy = 3.5; sun.rotation_euler = (math.radians(50), 0, math.radians(-25))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); scene.collection.objects.link(cam); scene.camera = cam
    target = Vector((x / 2, 0, .35))
    cam.data.lens = 35
    cam.location = target + Vector((.15, -1, .4)).normalized() * (x * .95 + .8)
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x, scene.render.resolution_y = 1600, 700
    scene.world = bpy.data.worlds.new('w'); scene.world.use_nodes = True
    scene.world.node_tree.nodes['Background'].inputs[0].default_value = (.4, .4, .42, 1)
    scene.render.filepath = preview
    bpy.ops.render.render(write_still=True)
