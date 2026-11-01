import bpy, os, sys
from mathutils import Vector

# Renders Dr. Yang's trader portrait (the clinic's mascot: the K9 dog) to res/dryang.jpg.
# Run: blender -b --factory-startup -P render_portrait.py -- <GermanShepherd.fbx> <BaseColor.tga> <out.jpg>
fbx, tex, out = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx)
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
mat = bpy.data.materials.new('coat'); mat.use_nodes = True
node = mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(tex)
bsdf = mat.node_tree.nodes['Principled BSDF']; bsdf.inputs['Roughness'].default_value = .75
mat.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
for o in bpy.context.scene.objects:
    if o.type == 'MESH': o.data.materials.clear(); o.data.materials.append(mat)
head = arm.matrix_world @ arm.data.bones['RigHead'].head_local
neck = arm.matrix_world @ arm.data.bones['RigNeck1'].head_local
forward = (head - neck); forward.z = 0; forward.normalize()
side = forward.cross(Vector((0, 0, 1)))
target = head.lerp(neck, .35) + Vector((0, 0, -.02))
cam_pos = target + forward * .95 + side * .42 + Vector((0, 0, .12))
bpy.ops.object.camera_add(location=cam_pos)
cam = bpy.context.object; cam.rotation_euler = (target - cam_pos).to_track_quat('-Z', 'Y').to_euler(); cam.data.lens = 70
for loc, energy, size in ((cam_pos + side * .8 + Vector((0, 0, .8)), 120, 1.2), (cam_pos - side * 1.2 + Vector((0, 0, .3)), 45, 1.5), (head - forward * 1.2 + Vector((0, 0, .9)), 90, .8)):
    bpy.ops.object.light_add(type='AREA', location=loc); l = bpy.context.object
    l.data.energy = energy; l.data.size = size; l.rotation_euler = (head - loc).to_track_quat('-Z', 'Y').to_euler()
sc = bpy.context.scene; sc.camera = cam; sc.render.engine = 'CYCLES'; sc.cycles.samples = 64
sc.world = bpy.data.worlds.new('clinic'); sc.world.use_nodes = True
sc.world.node_tree.nodes['Background'].inputs[0].default_value = (.32, .45, .43, 1)   # surgery-green backdrop
sc.render.resolution_x = sc.render.resolution_y = 400
sc.view_settings.view_transform = 'AgX'
sc.render.image_settings.file_format = 'JPEG'; sc.render.image_settings.quality = 92
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
print('PORTRAIT_OK', out)
