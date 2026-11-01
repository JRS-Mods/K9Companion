import bpy, os, struct, math, json
from mathutils import Matrix, Vector, Quaternion
root=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
data=os.path.join(root,'assets')
out=os.environ.get('DOG_EXPORT') or os.path.join(root,'work','exported')
os.makedirs(out,exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
f=open(os.path.join(data,'k9dog.dog'),'rb')
def ints(n=1): return struct.unpack('<'+'i'*n,f.read(4*n))
def floats(n): return struct.unpack('<'+'f'*n,f.read(4*n))
def text(): return f.read(ints()[0]).decode('utf8')
def matrix():
    v=floats(10)
    assert all(math.isfinite(x) for x in v)
    q=Quaternion((v[6],v[3],v[4],v[5]))
    assert abs(q.magnitude-1)<0.001
    return Matrix.LocRotScale(Vector(v[:3]),q,Vector(v[7:]))
assert ints()[0]==0x31474f44
count=ints()[0]; parents=[]; rest=[]; bone_names=[]
for i in range(count):
    name=text(); bone_names.append(name); p=ints()[0]; parents.append(p); local=matrix()
    rest.append(rest[p]@local if p>=0 else local)
meshes=[]
for _ in range(ints()[0]):
    name=text(); vertices=[]; uvs=[]; indices=[]; weights=[]
    for i in range(ints()[0]):
        v=floats(8); vertices.append(Vector(v[:3])); uvs.append(v[6:8]); indices.append(ints(4)); weights.append(floats(4))
        assert abs(sum(weights[-1])-1)<0.00001
    triangles=[]
    for _ in range(ints()[0]):
        tri=ints(ints()[0]); triangles.extend(tuple(tri[i:i+3]) for i in range(0,len(tri),3))
    meshes.append((vertices,uvs,indices,weights,triangles,'coat'))
clips={}
for _ in range(ints()[0]):
    name=text(); frames=ints()[0]; fps=floats(1)[0]; poses=[]
    for frame in range(frames):
        world=[]
        for i in range(count):
            m=matrix(); p=parents[i]; world.append(world[p]@m if p>=0 else m)
        poses.append(world)
    clips[name]=poses
assert not f.read(1); f.close()
# The jacket (tools/convert_jacket.py) is skinned to the same skeleton; DOG_PREVIEW_JACKET=0 leaves it off.
jacket_path=os.path.join(data,'jacket.mesh')
if os.environ.get('DOG_PREVIEW_JACKET','1')!='0' and os.path.exists(jacket_path):
    f=open(jacket_path,'rb'); assert ints()[0]==0x31544b4a; text()
    vertices=[]; uvs=[]; indices=[]; weights=[]
    for i in range(ints()[0]):
        v=floats(8); vertices.append(Vector(v[:3])); uvs.append(v[6:8]); indices.append(ints(4)); weights.append(floats(4))
        assert abs(sum(weights[-1])-1)<0.00001 and all(0<=b<count for b in indices[-1])
    triangles=[]
    for _ in range(ints()[0]):
        tri=ints(ints()[0]); triangles.extend(tuple(tri[i:i+3]) for i in range(0,len(tri),3))
    assert not f.read(1); f.close()
    meshes.append((vertices,uvs,indices,weights,triangles,'jacket'))
C=Matrix(((-1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
image=bpy.data.images.load(os.path.join(data,'k9dog.png'))
mat=bpy.data.materials.new('Original coat'); mat.use_nodes=True
tex=mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image=image
bsdf=mat.node_tree.nodes.get('Principled BSDF'); bsdf.inputs['Roughness'].default_value=0.8
mat.node_tree.links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
materials={'coat':mat}
if os.path.exists(os.path.join(data,'jacket.jpg')):
    jm=bpy.data.materials.new('Police dog jacket'); jm.use_nodes=True
    jt=jm.node_tree.nodes.new('ShaderNodeTexImage'); jt.image=bpy.data.images.load(os.path.join(data,'jacket.jpg'))
    jm.node_tree.links.new(jt.outputs['Color'],jm.node_tree.nodes.get('Principled BSDF').inputs['Base Color'])
    materials['jacket']=jm
bounds={}
preview=os.environ.get('DOG_PREVIEW_CLIPS','Loco_Sneak,Sitting_02,Sleeping_01,Stand_01,Loco_Run,Lying_01').split(',')
for offset,name in enumerate(preview):
    sample=clips[name][len(clips[name])//3]
    skins=[sample[i]@rest[i].inverted() for i in range(count)]
    for verts,uvs,indices,weights,triangles,kind in meshes:
        posed=[]
        for v,bi,bw in zip(verts,indices,weights):
            pos=sum(((skins[i]@v)*w for i,w in zip(bi,bw)),Vector())
            assert all(math.isfinite(x) for x in pos)
            posed.append(C@pos)
        if kind=='coat': bounds[name]=[[min(v[d] for v in posed),max(v[d] for v in posed)] for d in range(3)]
        assert max(v.z for v in posed)<1.5 and min(v.z for v in posed)>-0.3, (name,bounds[name])
        mesh=bpy.data.meshes.new(name); mesh.from_pydata(posed,[],[(a,c,b) for a,b,c in triangles]); mesh.update()
        uv=mesh.uv_layers.new()
        for loop in mesh.loops: uv.data[loop.index].uv=uvs[loop.vertex_index]
        for poly in mesh.polygons: poly.use_smooth=True
        ob=bpy.data.objects.new(name,mesh); bpy.context.collection.objects.link(ob); ob.location.x=(offset%3-1)*1.3; ob.location.y=(offset//3)*2.0
        ob.data.materials.append(materials[kind])
    offset_v=Vector(((offset%3-1)*1.3,(offset//3)*2.0,0))
    neck=C@sample[bone_names.index('RigNeck2')].translation
    head=C@sample[bone_names.index('RigHead')].translation
    spine=C@sample[bone_names.index('RigSpine2')].translation
    chest=C@sample[bone_names.index('RigSpine3')].translation
    bpy.ops.mesh.primitive_torus_add(major_segments=32,minor_segments=8,location=neck+offset_v,major_radius=.115,minor_radius=.018)
    collar=bpy.context.object; collar.rotation_euler=(head-neck).to_track_quat('Z','Y').to_euler()
    leather=bpy.data.materials.new('Collar leather');leather.diffuse_color=(.18,.10,.055,1);leather.use_nodes=True;leather.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.18,.10,.055,1);collar.data.materials.append(leather)
    curve=bpy.data.curves.new(name+' label','FONT'); curve.body=name; curve.align_x='CENTER'; curve.size=0.12
    label=bpy.data.objects.new(name+' label',curve); bpy.context.collection.objects.link(label)
    label.location=((offset%3-1)*1.3,(offset//3)*2.0-1.1,0.01)
bpy.ops.mesh.primitive_plane_add(size=200)
floor=bpy.context.object; floor.location.z=-0.06
floor_mat=bpy.data.materials.new('Floor'); floor_mat.diffuse_color=(0.12,0.15,0.18,1);floor_mat.use_nodes=True;floor_mat.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.12,.15,.18,1); floor.data.materials.append(floor_mat)
for location,energy,size in [((1,-3,5),1800,5),((-4,1,3),1200,4)]:
    bpy.ops.object.light_add(type='AREA',location=location)
    light=bpy.context.object; light.data.energy=energy; light.data.shape='DISK'; light.data.size=size
    light.rotation_euler=(Vector((0,0,0.4))-light.location).to_track_quat('-Z','Y').to_euler()
# DOG_PREVIEW_VIEW=x,y,z,ortho moves the camera in for close-ups (default: the whole grid from the front right).
view=[float(x) for x in os.environ.get('DOG_PREVIEW_VIEW','3.2,-6,5,6.2').split(',')]
look=Vector((0,1,0.3)) if view[3]>5 else Vector((0,0,0.35))
bpy.ops.object.camera_add(location=tuple(view[:3]))
camera=bpy.context.object; camera.rotation_euler=(look-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO'; camera.data.ortho_scale=view[3]
scene=bpy.context.scene; scene.camera=camera; scene.render.engine='CYCLES'; scene.cycles.samples=24
scene.render.resolution_x=1400; scene.render.resolution_y=1100; scene.render.resolution_percentage=100
scene.world=bpy.data.worlds.new('World'); scene.world.use_nodes=True
scene.world.node_tree.nodes['Background'].inputs[0].default_value=(0.3,0.3,0.3,1)
scene.view_settings.view_transform='AgX'
scene.render.filepath=os.path.join(out,os.environ.get('DOG_PREVIEW_NAME','preview.png'))
with open(os.path.join(out,'pose-validation.json'),'w') as fh: json.dump(bounds,fh,indent=2)
bpy.ops.render.render(write_still=True)
print('DOG_BINARY_VALIDATED',json.dumps(bounds))
