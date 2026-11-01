import bpy, os, json
# Measures how far each root-motion gait carries the dog per second; the in-place clips play back at these speeds.
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.path.join(os.environ.get('DOG_EXPORT') or os.path.join(root, 'work', 'exported'), 'rootmotion')
result = {}
for name in ["Loco_Sneak","Loco_WalkSlow","Loco_Walk","Loco_Trot","Loco_Run","Loco_Sprint"]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.join(source, name + '.fbx'))
    arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    start, end = arm.animation_data.action.frame_range
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    bone = arm.pose.bones[0]
    def at(frame):
        bpy.context.scene.frame_set(int(frame), subframe=frame - int(frame))
        return arm.matrix_world @ bone.matrix.translation
    a, b = at(start), at(end)
    distance = ((b.x - a.x) ** 2 + (b.y - a.y) ** 2) ** 0.5
    duration = (end - start) / fps
    result[name] = {'bone': bone.name, 'metres': round(distance, 4), 'seconds': round(duration, 4), 'speed': round(distance / duration, 3)}
with open(os.path.join(source, 'gait-speeds.json'), 'w') as f: json.dump(result, f, indent=2)
print('GAIT_SPEEDS', json.dumps(result))
