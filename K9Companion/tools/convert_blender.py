import bpy, os, sys, struct, json, math
from mathutils import Matrix, Vector

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
source = os.environ.get('DOG_EXPORT') or os.path.join(root, 'work', 'exported')
dest = os.path.join(root, 'assets')
os.makedirs(dest, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(source, 'GermanShepherd.fbx'))
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
bones = list(arm.data.bones)
names = [b.name for b in bones]
parents = [names.index(b.parent.name) if b.parent else -1 for b in bones]
assert all(p < i for i,p in enumerate(parents))
C = Matrix(((-1,0,0,0),(0,0,1,0),(0,-1,0,0),(0,0,0,1)))
Ci = C.inverted()
rest = [C @ arm.matrix_world @ b.matrix_local @ Ci for b in bones]
stats = {'bones':len(bones),'meshes':[], 'clips':[]}

def pose(mat):
    p,q,s=mat.decompose()
    values=(*p,q.x,q.y,q.z,q.w,*s)
    assert all(math.isfinite(v) for v in values)
    return values

with open(os.path.join(dest,'k9dog.dog'), 'wb') as f:
    def ints(*a): f.write(struct.pack('<'+'i'*len(a),*a))
    def floats(*a): f.write(struct.pack('<'+'f'*len(a),*a))
    def text(s):
        raw=s.encode('utf8'); ints(len(raw)); f.write(raw)
    ints(0x31474f44,len(bones))
    for i,b in enumerate(bones):
        text(b.name); ints(parents[i])
        local=rest[parents[i]].inverted() @ rest[i] if parents[i]>=0 else rest[i]
        floats(*pose(local))
    ints(len(meshes))
    for ob in meshes:
        mesh=ob.data; mesh.calc_loop_triangles()
        text(ob.name)
        ints(len(mesh.loops))
        transform=C @ ob.matrix_world
        normal_matrix=transform.to_3x3().inverted().transposed()
        min_weight=1
        for loop in mesh.loops:
            vertex=mesh.vertices[loop.vertex_index]
            p=transform @ vertex.co
            n=(normal_matrix @ mesh.corner_normals[loop.index].vector).normalized()
            uv=mesh.uv_layers.active.data[loop.index].uv
            influences=sorted([(g.weight,names.index(ob.vertex_groups[g.group].name)) for g in vertex.groups
                               if ob.vertex_groups[g.group].name in names and g.weight>0], reverse=True)[:4]
            assert influences, 'Unweighted vertex'
            total=sum(w for w,b in influences); min_weight=min(min_weight,total)
            weights=[w/total for w,b in influences]; indices=[b for w,b in influences]
            while len(weights)<4: weights.append(0); indices.append(0)
            floats(*p,*n,*uv); ints(*indices); floats(*weights)
        submeshes=[[] for _ in mesh.materials]
        for triangle in mesh.loop_triangles:
            a,b,c=triangle.loops
            submeshes[triangle.material_index].extend((a,c,b))
        ints(len(submeshes))
        for triangles in submeshes: ints(len(triangles)); ints(*triangles)
        stats['meshes'].append({'name':ob.name,'vertices':len(mesh.loops),'triangles':len(mesh.loop_triangles),
                                'minimum_retained_skin_weight':min_weight,'size_metres':list(ob.dimensions)})
    clip_names=["Stand_00","Loco_Walk","Loco_Trot","Loco_Run","Loco_Sprint","Sitting_00","Trans_Stand_To_Sitting","Trans_Sitting_To_Stand","Loco_Walk_L","Loco_Walk_R","Loco_Trot_L","Loco_Trot_R","Loco_Run_L","Loco_Run_R","Loco_Sprint_L","Loco_Sprint_R","Trans_TurnL90","Trans_TurnR90","Loco_Sneak","Loco_Sneak_L","Loco_Sneak_R","Stand_Sneak_00","Loco_WalkSlow","Loco_WalkSlow_L","Loco_WalkSlow_R","Stand_01","Stand_02","Sitting_01","Sitting_02","Lying_00","Lying_01","Lying_02","Sleeping_01","Trans_Stand_To_Lying","Trans_Lying_To_Stand","Trans_Lying_to_Sleeping","Trans_Sleeping_to_Lying","Death_Stand_L","Death_Stand_R","Death_Stand_L_Pose","Death_Stand_R_Pose","Hit_ChestL_Light","Hit_ChestR_Light","Hit_ChestL_Heavy","Hit_ChestR_Heavy","Attack_BiteL","Attack_BiteR","Agressive_01"]
    ints(len(clip_names))
    for name in clip_names:
        before=set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=os.path.join(source,name+'.fbx'))
        added=set(bpy.data.objects)-before
        anim=next(o for o in added if o.type=='ARMATURE')
        action=anim.animation_data.action
        start,end=action.frame_range
        fps=bpy.context.scene.render.fps/bpy.context.scene.render.fps_base
        duration=(end-start)/fps
        frames=max(2,round(duration*30)+1)
        effective_fps=(frames-1)/duration
        text(name); ints(frames); floats(effective_fps)
        assert set(names)==set(b.name for b in anim.data.bones), 'Skeleton mismatch'
        max_delta=max(max(abs(a-b) for ra,rb in zip(rest[i], C@anim.matrix_world@anim.data.bones[n].matrix_local@Ci)
                          for a,b in zip(ra,rb)) for i,n in enumerate(names))
        assert max_delta < 0.001, (name,'bind mismatch',max_delta)
        for j in range(frames):
            frame=start+(end-start)*j/(frames-1)
            bpy.context.scene.frame_set(int(frame),subframe=frame-int(frame))
            matrices=[C @ anim.matrix_world @ anim.pose.bones[n].matrix @ Ci for n in names]
            for i,world in enumerate(matrices):
                local=matrices[parents[i]].inverted() @ world if parents[i]>=0 else world
                floats(*pose(local))
        stats['clips'].append({'name':name,'frames':frames,'duration':duration,'bind_error':max_delta})
        for ob in added: bpy.data.objects.remove(ob,do_unlink=True)

image=bpy.data.images.load(os.path.join(source,'GermanShepherd_BaseColor.tga'))
image.scale(2048,2048)
image.filepath_raw=os.path.join(dest,'k9dog.png'); image.file_format='PNG'; image.save()
with open(os.path.join(source,'asset-validation.json'),'w') as f: json.dump(stats,f,indent=2)
print('DOG_CONVERSION_OK',json.dumps(stats))
