import bpy, os, sys, struct, json, math, zipfile, tempfile
from mathutils import Matrix

# Builds assets/kennel.props (+ textures) from the bed and bowl assets in E:\SPT Mod\Assets.
# Format: 'PRP1', count, then per mesh: name, texture file, vertex count, (pos, normal, uv) per vertex, index count, indices.
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = os.environ.get('DOG_PROP_ASSETS') or os.path.join(os.path.dirname(root), 'Assets')
dest = os.path.join(root, 'assets')
work = tempfile.mkdtemp(prefix='k9_props_')
C = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))   # Blender Z-up -> Unity Y-up (same as the dog)

bpy.ops.wm.read_factory_settings(use_empty=True)

def only_new(before):
    return [o for o in set(bpy.data.objects) - before if o.type == 'MESH']

def prepare(objs, target_faces, metres_per_unit, upright=False):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1: bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active
    # The bed scan is Z-up and upside down (scan floor on top): turn it the right way up.
    if upright: o.rotation_euler = (math.pi, 0, 0)
    o.scale = [s * metres_per_unit for s in o.scale]
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    faces = len(o.data.polygons)
    if faces > target_faces:
        mod = o.modifiers.new('decimate', 'DECIMATE'); mod.ratio = target_faces / faces
        bpy.ops.object.modifier_apply(modifier=mod.name)
    # Sit on the ground, centred.
    xs = [v.co.x for v in o.data.vertices]; ys = [v.co.y for v in o.data.vertices]; zs = [v.co.z for v in o.data.vertices]
    shift = ((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, min(zs))
    for v in o.data.vertices: v.co.x -= shift[0]; v.co.y -= shift[1]; v.co.z -= shift[2]
    o.data.update()
    return o

def texture(src, name, size):
    img = bpy.data.images.load(src)
    w, h = img.size
    if max(w, h) > size: img.scale(size, size * h // w if w >= h else size) if w >= h else img.scale(size * w // h, size)
    path = os.path.join(dest, name)
    img.filepath_raw = path; img.file_format = 'JPEG'; img.save()
    return name

def write_mesh(f, name, o, tex):
    mesh = o.data; mesh.calc_loop_triangles()
    uv = mesh.uv_layers.active.data
    def text(s): raw = s.encode('utf8'); f.write(struct.pack('<i', len(raw))); f.write(raw)
    text(name); text(tex)
    f.write(struct.pack('<i', len(mesh.loops)))
    normal_matrix = C.to_3x3()
    for loop in mesh.loops:
        p = C @ mesh.vertices[loop.vertex_index].co
        n = (normal_matrix @ mesh.corner_normals[loop.index].vector).normalized()
        t = uv[loop.index].uv
        f.write(struct.pack('<8f', p.x, p.y, p.z, n.x, n.y, n.z, t.x, t.y))
    tris = []
    for tri in mesh.loop_triangles: a, b, c = tri.loops; tris += [a, c, b]
    f.write(struct.pack('<i', len(tris))); f.write(struct.pack('<%di' % len(tris), *tris))
    return {'name': name, 'vertices': len(mesh.loops), 'triangles': len(tris) // 3,
            'size_m': [round(x, 3) for x in o.dimensions]}

stats = []
# Bed: photogrammetry scan in millimetres, 1.3M faces -> ~9k.
with zipfile.ZipFile(os.path.join(assets, 'dog-bed', 'source', '2023-05-12-20-24-50_stl.zip')) as z: z.extractall(work)
before = set(bpy.data.objects); bpy.ops.wm.obj_import(filepath=os.path.join(work, 'DogBedLower.obj'))
bed = prepare(only_new(before), 9000, 0.001, upright=True)
bed_tex = texture(os.path.join(assets, 'dog-bed', 'textures', 'DogBedLower.jpeg'), 'kennel_bed.jpg', 2048)
# Bowl: ~2.3 units across -> 0.24 m dish.
before = set(bpy.data.objects); bpy.ops.import_scene.fbx(filepath=os.path.join(assets, 'dog-bowl', 'source', 'DogBowlFBX.fbx'))
objs = only_new(before)
across = max(max(o.dimensions.x, o.dimensions.y) for o in objs)
bowl = prepare(objs, 6000, 0.24 / across)
bowl_tex = texture(os.path.join(assets, 'dog-bowl', 'textures', 'DogBowl01_DogBowl01_BaseColor.jpg'), 'kennel_bowl.jpg', 1024)

with open(os.path.join(dest, 'kennel.props'), 'wb') as f:
    f.write(struct.pack('<ii', 0x31505250, 2))
    stats.append(write_mesh(f, 'bed', bed, bed_tex))
    stats.append(write_mesh(f, 'bowl', bowl, bowl_tex))
print('PROPS_OK', json.dumps(stats))

if os.environ.get('DOG_PROP_PREVIEW'):
    for o, x, img in ((bed, 0, bed_tex), (bowl, .9, bowl_tex)):
        mat = bpy.data.materials.new(o.name + ' preview'); mat.use_nodes = True
        node = mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(os.path.join(dest, img))
        mat.node_tree.links.new(node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
        o.data.materials.clear(); o.data.materials.append(mat); o.location.x = x
    bpy.ops.mesh.primitive_plane_add(size=20, location=(0, 0, -.001))
    bpy.ops.object.light_add(type='SUN', location=(2, -2, 4)); bpy.context.object.data.energy = 4
    bpy.ops.object.camera_add(location=(1.4, -2.4, 1.5))
    cam = bpy.context.object; from mathutils import Vector
    cam.rotation_euler = (Vector((.35, 0, .15)) - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc = bpy.context.scene; sc.camera = cam; sc.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'CYCLES'
    sc.render.resolution_x, sc.render.resolution_y = 1000, 700
    sc.world = bpy.data.worlds.new('w'); sc.world.use_nodes = True; sc.world.node_tree.nodes['Background'].inputs[0].default_value = (.4, .4, .4, 1)
    sc.render.filepath = os.environ['DOG_PROP_PREVIEW']; bpy.ops.render.render(write_still=True)
