import unreal, os, json
out = os.environ.get('DOG_EXPORT') or os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'work', 'exported')
os.makedirs(out, exist_ok=True)
base = '/Game/Animalia/GermanShepherd/'
items = [('Mesh/GermanShepherd', 'GermanShepherd.fbx')]
for name in ["Stand_00","Loco_Walk","Loco_Trot","Loco_Run","Loco_Sprint","Sitting_00","Trans_Stand_To_Sitting","Trans_Sitting_To_Stand","Loco_Walk_L","Loco_Walk_R","Loco_Trot_L","Loco_Trot_R","Loco_Run_L","Loco_Run_R","Loco_Sprint_L","Loco_Sprint_R","Trans_TurnL90","Trans_TurnR90","Loco_Sneak","Loco_Sneak_L","Loco_Sneak_R","Stand_Sneak_00","Loco_WalkSlow","Loco_WalkSlow_L","Loco_WalkSlow_R","Stand_01","Stand_02","Sitting_01","Sitting_02","Lying_00","Lying_01","Lying_02","Sleeping_01","Trans_Stand_To_Lying","Trans_Lying_To_Stand","Trans_Lying_to_Sleeping","Trans_Sleeping_to_Lying","Death_Stand_L","Death_Stand_R","Death_Stand_L_Pose","Death_Stand_R_Pose","Hit_ChestL_Light","Hit_ChestR_Light","Hit_ChestL_Heavy","Hit_ChestR_Heavy","Attack_BiteL","Attack_BiteR","Agressive_01"]:
    items.append(('Animations/InPlace/' + name + '-IP', name + '.fbx'))
# Root-motion versions are only used to measure each gait's real ground speed (tools/measure_gaits.py).
for name in ["Loco_Sneak","Loco_WalkSlow","Loco_Walk","Loco_Trot","Loco_Run","Loco_Sprint"]:
    items.append(('Animations/' + name, 'rootmotion/' + name + '.fbx'))
items.append(('Textures/GermanShepherd_BaseColor', 'GermanShepherd_BaseColor.tga'))
os.makedirs(os.path.join(out, 'rootmotion'), exist_ok=True)
report = []
for path, filename in items:
    asset = unreal.load_asset(base + path)
    if not asset: raise RuntimeError('Cannot load ' + base + path)
    task = unreal.AssetExportTask()
    task.object = asset
    task.filename = os.path.join(out, filename)
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    if filename.endswith('.fbx'):
        options = unreal.FbxExportOption()
        options.ascii = False
        options.level_of_detail = False
        task.options = options
    ok = unreal.Exporter.run_asset_export_task(task)
    report.append({'asset': path, 'output': filename, 'success': ok, 'errors': list(task.errors)})
    if not ok: raise RuntimeError('Export failed: ' + path)
with open(os.path.join(out, 'export-report.json'), 'w') as f: json.dump(report, f, indent=2)
