import bpy, os, sys, math
import numpy as np
from mathutils import Vector

# Preview of convert_dog_items.py's output: per item one row - the source as it came, then the decimated, baked
# copy from several sides (the can turned round so the whole wrap shows).
#   blender -b -P render_dog_items.py -- <out.png> [key ...]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
work = os.path.join(root, 'work', 'dog_items')
args = sys.argv[sys.argv.index('--') + 1:]
out, keys = args[0], args[1:] or ['can', 'meat', 'duffel', 'tag', 'vetkit']
# (object, yaw seen from, pitch): yaw 0 = straight at the front (-y)
VIEWS = {
    'can':    [('source', -25, 12), ('low', -25, 12), ('low', 95, 12), ('low', 215, 12), ('low', -25, 70)],
    'meat':   [('source', -25, 20), ('low', -25, 20), ('low', 0, 5), ('low', 150, 20), ('low', 60, 45)],
    'duffel': [('source', -25, 15), ('low', -25, 15), ('low', 0, 5), ('low', 140, 20), ('low', 70, 50)],
    'tag':    [('source', -20, 10), ('low', -20, 10), ('low', 0, 0), ('low', 160, 10), ('low', 70, 40)],
    'vetkit': [('source', -25, 15), ('low', -25, 15), ('low', 0, 5), ('low', 150, 20), ('low', 60, 50)],
}
RES = 640

def render_row(key):
    bpy.ops.wm.open_mainfile(filepath=os.path.join(work, key, key + '.blend'))
    sc = bpy.context.scene
    src, low = bpy.data.objects[key + '_source'], bpy.data.objects[key]
    sc.render.engine = 'BLENDER_EEVEE'; sc.render.resolution_x = sc.render.resolution_y = RES
    sc.render.film_transparent = False
    sc.world = bpy.data.worlds.new('w'); sc.world.use_nodes = True
    sc.world.node_tree.nodes['Background'].inputs[0].default_value = (.32, .32, .34, 1)
    sc.view_settings.view_transform = 'AgX'
    def light(kind, energy, rot, size=None):
        l = bpy.data.lights.new(kind, kind); l.energy = energy
        if size: l.size = size
        ob = bpy.data.objects.new(kind, l); sc.collection.objects.link(ob)
        ob.rotation_euler = [math.radians(r) for r in rot]
        return ob
    light('SUN', 3.2, (50, 0, -35)); light('SUN', 1.0, (70, 0, 140))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.lens = 60
    co = np.array([v.co for v in low.data.vertices])
    centre = Vector(((co.min(0) + co.max(0)) / 2).tolist()); radius = float(np.linalg.norm(co.max(0) - co.min(0))) / 2
    tiles = []
    for which, yaw, pitch in VIEWS[key]:
        src.hide_render = which != 'source'; low.hide_render = which == 'source'
        y, p = math.radians(yaw), math.radians(pitch)
        direction = Vector((math.sin(y) * math.cos(p), -math.cos(y) * math.cos(p), math.sin(p)))
        cam.location = centre + direction * radius * 3.3
        cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
        path = os.path.join(work, key, f'_view_{len(tiles)}.png')
        sc.render.filepath = path
        bpy.ops.render.render(write_still=True)
        tiles.append(path)
    return tiles

rows = [render_row(k) for k in keys]
cols = max(len(r) for r in rows)
canvas = np.zeros((len(rows) * RES, cols * RES, 4), np.float32); canvas[..., 3] = 1
for r, tiles in enumerate(rows):
    for c, path in enumerate(tiles):
        im = bpy.data.images.load(path)
        px = np.array(im.pixels[:], np.float32).reshape(RES, RES, 4)
        rr = len(rows) - 1 - r
        canvas[rr * RES:(rr + 1) * RES, c * RES:(c + 1) * RES] = px
        # a gap after the source column
        if c == 0: canvas[rr * RES:(rr + 1) * RES, RES - 6:RES] = (.1, .1, .1, 1)
        os.remove(path)
img = bpy.data.images.new('sheet', cols * RES, len(rows) * RES, alpha=False)
img.pixels[:] = canvas.ravel(); img.filepath_raw = out; img.file_format = 'PNG'; img.save()
print('RENDER_OK', out)
