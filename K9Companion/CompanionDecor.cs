using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace K9Companion
{
    internal sealed class CompanionDecor : IDisposable
    {
        private readonly Transform dog;
        private readonly DogModel model;
        private readonly bool hasHarnesses;
        private readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();
        private readonly Material leather, steel, cushion, foodMaterial, waterMaterial;
        private Transform foodFill, waterFill, vest, spine;
        // The gear models (tools/convert_gear.py), skinned to the dog: one harness per look, one collar per style.
        internal static readonly string[] Harnesses = { "harness_chestrig", "harness_pocket", "harness_tactical", "harness_assault", "harness_camera" };
        internal static readonly string[] CollarStyles = { "training", "black", "buckle" };
        // Head gear (rigid on the skull), and the clip-on camera: one model per harness, stood on that harness's back.
        internal static readonly string[] HeadGear = { "head_goggles_tan", "head_goggles_black" };
        private readonly Dictionary<string, GameObject> harnesses = new Dictionary<string, GameObject>(), collars = new Dictionary<string, GameObject>(),
                                                         heads = new Dictionary<string, GameObject>(), cameras = new Dictionary<string, GameObject>();
        private string collarStyle, vestLook;
        private bool cameraWorn;
        internal string VestLook => vestLook;
        // Where each camera's lens is (tools: measured on gear_harness_camera and gear_camera_action_<harness>), in the model's
        // space, and the bone that carries it. Set while the skeleton is in its bind pose, facing forward.
        private static readonly (string Key, string Bone, Vector3 Lens)[] Lenses =
        {
            ("vest", "RigChest", new Vector3(0, .70f, .255f)),  // camera vest's own, on the front of its handle
            ("clip", "RigChest", new Vector3(0, .75f, .21f)),   // clip-on action camera on the jacket's back panel
        };
        private readonly Dictionary<string, Transform> mounts = new Dictionary<string, Transform>();
        internal Transform Mount(string key) => mounts.TryGetValue(key, out Transform t) ? t : null;
        private Quaternion vestBind;
        public Transform Home { get; private set; }
        private PropModel props, models;
        // The kennel.props bed (about 1.1 x 1.2 m as made) is scaled down to fit the dog curled up on it; the hideout's
        // pet bed (0.41 x 0.33 m) is stretched to about 1.2 x 1.0 m so the dog fits inside lying down, its sides only a
        // little higher.
        private const float BedScale = .65f, BedZ = -.05f;
        private static readonly Vector3 BedStretch = new Vector3(3.0f, 3.0f, 1.3f);
        private float bowlZ = .73f;
        // The floor the whole kennel needs (bed and bowls), around the kennel's position, in its own frame.
        public static readonly Vector3 FootprintCentre = new Vector3(0, 0, .1f), FootprintHalf = new Vector3(.52f, 0, .78f);
        // Where the kennel's furniture stands beside the bed, and the floor each piece needs (half sizes).
        private static readonly Vector3 CabinetAt = new Vector3(.95f, 0, -.35f), CabinetHalf = new Vector3(.31f, 0, .18f),
                                        BarrelAt = new Vector3(.95f, 0, .35f), BarrelHalf = new Vector3(.26f, 0, .24f),
                                        TableAt = new Vector3(-1.2f, 0, -.2f), TableHalf = new Vector3(.62f, 0, .32f);
        // The custom kennel models: clear of the bed, the bowls and the pieces above (they don't count towards the room the
        // kennel needs; each is left out where there's no space for it).
        // The toy box moves out past the military kennel's open door.
        private static readonly Vector3 ToyBoxAt = new Vector3(-.72f, 0, 1.0f), ToyBoxBesideDoorAt = new Vector3(-.95f, 0, 1.0f);
        // The kennel's shelter stands over the bed (the dog sleeps inside, its door towards the bowls): the wooden dog
        // house, then the military K9 kennel from the Tactical K9 Facility on. The floor each needs.
        private static readonly Vector3 DogHouseHalf = new Vector3(.68f, 0, .78f),
                                        MilitaryKennelAt = new Vector3(0, 0, .13f), MilitaryKennelHalf = new Vector3(.62f, 0, .84f);
        private const float DogHouseRoom = .22f;   // how much bigger than the bed, each way, the inside is
        // The military kennel is kept as made, only scaled up to the dog house's height, and has its own bed: the
        // hideout's is put away and the dog lies on the kennel's. Its inside (tools: raycast on kennel_models.props) is
        // centred this far behind the model's middle; that goes over where the bed was. Its door stands open on the left.
        // Its body's front (the pallet's edge) in the model's space: the open door swings out further, beside the way in.
        private const float MilitaryKennelHeight = 1.3f, MilitaryKennelInsideZ = -.12f, MilitaryKennelFrontZ = .29f;
        private Bounds bedBounds = new Bounds(new Vector3(0, 0, BedZ), new Vector3(1f, .2f, 1.2f));
        // The bed and the cushion the dog lies on: raised onto the shelter's floor while there is one.
        private readonly List<Transform> bedParts = new List<Transform>();
        private Transform bedModel;
        private float bedRaised;
        private BoxCollider cushionBox;

        /// <summary>
        /// Where the dog stands or lies at 'p' if that's on its bed: the top of the bed (the cushion is on no layer the
        /// dog's ground check sees, so without this it lay on the floor under a raised bed, through the kennel's floor).
        /// </summary>
        public bool OnBed(Vector3 p, out float y)
        {
            y = 0;
            if (cushionBox == null) return false;
            Vector3 local = cushionBox.transform.InverseTransformPoint(p) - cushionBox.center;
            Vector3 half = cushionBox.size / 2;
            if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.z) > half.z) return false;
            y = cushionBox.bounds.max.y;
            return true;
        }

        // The way into the shelter over the bed (dog house or military kennel), in the kennel's own space: just out in
        // front of its door, and how far its sides reach. Set when the shelter is built; none without one.
        private float shelterFrontZ, shelterHalfX;
        private bool hasShelter;

        /// <summary>
        /// The way to the bed from 'from' when a shelter stands over it: round its side (if 'from' is beside or behind
        /// it), to just in front of its door, then the bed. Without a shelter, straight to the bed.
        /// </summary>
        public List<Vector3> BedRoute(Vector3 from)
        {
            var route = new List<Vector3>();
            if (Home == null) return route;
            if (hasShelter)
            {
                Vector3 local = Home.InverseTransformPoint(from);
                float doorZ = shelterFrontZ + .2f;
                if (local.z < shelterFrontZ) route.Add(Home.TransformPoint(new Vector3(Mathf.Sign(local.x == 0 ? 1 : local.x) * (shelterHalfX + .55f), 0, doorZ + .25f)));
                route.Add(Home.TransformPoint(new Vector3(0, 0, doorZ)));
            }
            route.Add(Home.position);
            return route;
        }

        /// <summary>Just outside the shelter's door, where a dog leaving its bed heads first; null without a shelter.</summary>
        public Vector3? DoorStep => hasShelter && Home != null ? Home.TransformPoint(new Vector3(0, 0, shelterFrontZ + .35f)) : (Vector3?)null;

        /// <summary>The floor the whole kennel needs at a level: the bed and bowls first, then its furniture.</summary>
        public static IEnumerable<(Vector3 centre, Vector3 half)> Footprint(int level)
        {
            yield return (FootprintCentre, FootprintHalf);
            if (level >= 2) { yield return (CabinetAt, CabinetHalf); yield return (BarrelAt, BarrelHalf); yield return level >= 3 ? (MilitaryKennelAt, MilitaryKennelHalf) : (new Vector3(0, 0, BedZ), DogHouseHalf); }
            if (level >= 3) yield return (TableAt, TableHalf);
        }
        public CompanionDecor(Transform dog, DogModel model)
        {
            this.dog = dog; this.model = model; spine = model.Bone("RigSpine2");
            // The harnesses and collars are the gear models, skinned to the dog's skeleton so they bend with it. Each is loaded
            // the first time it's worn (Gear): loading the lot, for a dog that wears three, was most of the wait for a dog.
            // Without any harness files a simple harness is built instead.
            hasHarnesses = Harnesses.Any(look => File.Exists(Path.Combine(Plugin.AssetFolder, "gear_" + look + ".mesh")));
            foreach (var (key, boneName, lens) in Lenses)
            {
                Transform bone = model.Bone(boneName);
                if (bone == null || model.Root == null) continue;
                var mount = new GameObject("K9 camera lens " + key).transform;
                mount.SetPositionAndRotation(model.Root.TransformPoint(lens), model.Root.rotation);
                mount.SetParent(bone, true);
                mounts[key] = mount;
            }
            if (!hasHarnesses)
            {
                vest = BuildVest(dog);
                vestBind = spine != null ? Quaternion.Inverse(spine.rotation) * dog.rotation : Quaternion.identity;
                vest.gameObject.SetActive(false);
            }
            leather = Mat(new Color(.18f,.10f,.055f)); steel = Mat(new Color(.5f,.53f,.55f));
            cushion = Mat(new Color(.23f,.28f,.25f));
            foodMaterial = Mat(new Color(.28f,.12f,.04f)); waterMaterial = Mat(new Color(.12f,.42f,.55f));

        }
        private Material Mat(Color color)
        {
            var m = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")); m.color = color;
            if(m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", .2f);
            resources.Add(m); return m;
        }
        private Transform Primitive(string name, PrimitiveType shape, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(shape); go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>(); if(collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            go.GetComponent<Renderer>().sharedMaterial = material; return go.transform;
        }
        private Transform Ring(string name, Transform parent, float radius, float thickness, Material material)
        {
            const int segments = 32, sides = 8;
            var vertices = new Vector3[segments*sides]; var triangles = new int[segments*sides*6];
            for(int i=0;i<segments;i++) for(int j=0;j<sides;j++)
            {
                float a=i*2*Mathf.PI/segments, b=j*2*Mathf.PI/sides;
                vertices[i*sides+j] = new Vector3(Mathf.Cos(a)*(radius+thickness*Mathf.Cos(b)), thickness*Mathf.Sin(b), Mathf.Sin(a)*(radius+thickness*Mathf.Cos(b)));
                int k=(i*sides+j)*6, v=i*sides+j, x=((i+1)%segments)*sides+j, y=i*sides+(j+1)%sides, z=((i+1)%segments)*sides+(j+1)%sides;
                triangles[k]=v;triangles[k+1]=y;triangles[k+2]=x;triangles[k+3]=y;triangles[k+4]=z;triangles[k+5]=x;
            }
            var mesh=new Mesh { vertices=vertices, triangles=triangles }; mesh.RecalculateNormals(); mesh.RecalculateBounds(); resources.Add(mesh);
            var go=new GameObject(name);go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial=material;return go.transform;
        }
        // A piece of gear, loaded the first time it's wanted: on the dog's layer (the preview dog has one of its own), and
        // only casting a shadow if it's big enough for that to show. Null if its files aren't there.
        private GameObject Gear(Dictionary<string, GameObject> set, string name, string key, string texture = null, bool shadow = false)
        {
            if (set.TryGetValue(name, out GameObject have)) return have;
            Wearable(model, key, set, name, texture);
            set.TryGetValue(name, out have);
            set[name] = have;
            if (have == null) return null;
            have.layer = model.Root.gameObject.layer;
            if (!shadow) foreach (Renderer r in have.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return have;
        }

        // Only this one of the set shows (none for null).
        private static void ShowOnly(Dictionary<string, GameObject> set, string name)
        {
            foreach (var piece in set) if (piece.Value != null && piece.Value.activeSelf != (piece.Key == name)) piece.Value.SetActive(piece.Key == name);
        }

        // One gear model (gear_<key>.mesh + .jpg, or another texture), hidden until worn.
        private static void Wearable(DogModel model, string key, Dictionary<string, GameObject> into, string name = null, string texture = null)
        {
            try
            {
                GameObject worn = model.LoadWearable(Plugin.AssetFolder, "gear_" + key + ".mesh", texture ?? "gear_" + key + ".jpg");
                if (worn == null) { Plugin.Log.LogWarning($"No gear_{key} files; that one won't show on the dog."); return; }
                worn.SetActive(false);
                into[name ?? key] = worn;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Dog gear {key} couldn't be loaded: " + e.Message); }
        }

        // The jacket the dog wears, by its look (null = none). Without the harness files, the simple harness.
        public void SetVest(string look)
        {
            if (!hasHarnesses)
            {
                bool worn = look != null;
                if (vest != null && vest.gameObject.activeSelf != worn) vest.gameObject.SetActive(worn);
                return;
            }
            // A look without its model (files missing) shows the chest rig rather than nothing.
            if (look != null && (!Harnesses.Contains(look) || Gear(harnesses, look, look, null, true) == null))
                look = Gear(harnesses, "harness_chestrig", "harness_chestrig", null, true) != null ? "harness_chestrig" : null;
            ShowOnly(harnesses, look);
            vestLook = look;
            ShowCamera();
        }

        // The goggles on the dog's head, by look (null = none).
        public void SetHead(string look)
        {
            if (look != null && HeadGear.Contains(look)) Gear(heads, look, look);
            ShowOnly(heads, look);
        }

        // Whether the clip-on camera is fitted: it shows on the back of the jacket worn (no jacket, or one with its own
        // camera, shows none).
        public void SetCamera(bool worn) { cameraWorn = worn; ShowCamera(); }

        private void ShowCamera()
        {
            string on = cameraWorn && vestLook != null && !DogGear.BuiltInCamera.Contains(vestLook) ? vestLook : null;
            if (on != null) Gear(cameras, on, "camera_action_" + on, "gear_camera_action.jpg");
            ShowOnly(cameras, on);
        }

        // The collar the dog wears, by style (none for null: its collar slot is empty; an unknown one is the training collar).
        private void SetCollar(string style)
        {
            if (style != null && !CollarStyles.Contains(style)) style = "training";
            if (style != null && Gear(collars, style, "collar_" + style) == null) style = null;
            collarStyle = style;
            ShowOnly(collars, style);
        }

        private Transform BuildVest(Transform parent)
        {
            var root = new GameObject("K9 saddlebag vest").transform; root.SetParent(parent, false);
            Material cordura = Mat(new Color(.29f, .30f, .22f)), webbing = Mat(new Color(.12f, .12f, .10f));
            const int arcs = 18; float length = .40f;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int end = 0; end < 2; end++) for (int a = 0; a <= arcs; a++)
            {
                float t = Mathf.Lerp(-105, 105, (float)a / arcs) * Mathf.Deg2Rad;
                vertices.Add(new Vector3(Mathf.Sin(t) * .155f, Mathf.Cos(t) * .17f - .07f, end == 0 ? -length / 2 : length / 2));
            }
            for (int a = 0; a < arcs; a++) { int i = a, j = arcs + 1 + a; triangles.AddRange(new[] { i, i + 1, j + 1, i, j + 1, j, i, j + 1, i + 1, i, j, j + 1 }); }
            var mesh = new Mesh { vertices = vertices.ToArray(), triangles = triangles.ToArray() }; mesh.RecalculateNormals(); mesh.RecalculateBounds(); resources.Add(mesh);
            var shell = new GameObject("Vest shell"); shell.transform.SetParent(root, false);
            shell.AddComponent<MeshFilter>().sharedMesh = mesh; shell.AddComponent<MeshRenderer>().sharedMaterial = cordura;
            foreach (float side in new[] { -1f, 1f })
            {
                Primitive("Saddlebag pouch", PrimitiveType.Cube, root, new Vector3(side * .175f, -.13f, .02f), new Vector3(.07f, .14f, .22f), cordura);
                Primitive("Pouch flap", PrimitiveType.Cube, root, new Vector3(side * .182f, -.07f, .02f), new Vector3(.075f, .03f, .23f), webbing);
            }
            foreach (float z in new[] { -.14f, .14f })
            {
                Transform strap = Ring("Belly strap", root, .16f, .012f, webbing);
                strap.localPosition = new Vector3(0, -.07f, z); strap.localRotation = Quaternion.Euler(90, 0, 0);   // wraps round the body
            }
            return root;
        }

        public void Update()
        {
            if (vest != null && vest.gameObject.activeSelf && spine != null)
            { vest.position = spine.position; vest.rotation = spine.rotation * vestBind; vest.localScale = Vector3.one * Plugin.Scale.Value; }
            // The kennel's lights go off and on, and dim, with the hideout lamp they were set from (its power).
            if (lampReference != null)
            {
                bool on = lampReference.isActiveAndEnabled;
                float dim = referenceIntensity > 0 ? lampReference.intensity / referenceIntensity : 1;
                if (lamp != null) { lamp.enabled = on; lamp.intensity = lampIntensity * dim; }
                if (doorLamp != null) { doorLamp.enabled = on; doorLamp.intensity = DoorLampIntensity * dim; }
            }
        }

        // ---- the kennel's lights ------------------------------------------------------------------------------------

        // A lamp under the ceiling over the kennel, lighting its front, like the lights the hideout's own areas have: set
        // like the nearest of the hideout's lamps (colour, brightness, reach, shadows) and following it. With no hideout
        // lamp about, a warm bulb of its own. The military kennel's caged lamp over its door lights up too.
        private Light lamp, doorLamp, lampReference;
        private float referenceIntensity, lampIntensity = 1.6f;
        private const float DoorLampIntensity = .9f;
        // The caged lamp over the military kennel's door, in the model's own space (kennel_models.props).
        private static readonly Vector3 DoorLampAt = new Vector3(0, .6f, .31f);

        private void PlaceLamp()
        {
            if (lamp == null)
            {
                var go = new GameObject("K9 kennel lamp");
                go.transform.SetParent(Home, false);
                lamp = go.AddComponent<Light>();
                lamp.type = LightType.Point;
            }
            // Matched again wherever the kennel goes (moved into another room, it takes after that room's lamps). Only a
            // lamp that is lit and lights a room counts, not a glow or an indicator.
            lampReference = UnityEngine.Object.FindObjectsOfType<Light>()
                .Where(l => l.isActiveAndEnabled && (l.type == LightType.Point || l.type == LightType.Spot) && l.intensity >= .5f && l.range >= 3
                            && l.gameObject.scene == Home.gameObject.scene && !l.transform.IsChildOf(Home) && !l.transform.IsChildOf(dog))
                .OrderBy(l => (l.transform.position - Home.position).sqrMagnitude).FirstOrDefault();
            if (lampReference != null)
            {
                lamp.color = lampReference.color; lamp.shadows = lampReference.shadows; lamp.renderMode = lampReference.renderMode;
                lamp.cullingMask = lampReference.cullingMask; lamp.bounceIntensity = lampReference.bounceIntensity;
                lamp.range = Mathf.Clamp(lampReference.range, 5, 9);
                lampIntensity = Mathf.Clamp(lampReference.intensity, 1f, 3f); referenceIntensity = lampReference.intensity;
                Plugin.Log.LogInfo($"Kennel lamp set like the hideout's {lampReference.name} ({Vector3.Distance(lampReference.transform.position, Home.position):0.0} m away): " +
                                   $"intensity {lampReference.intensity:0.##}, range {lampReference.range:0.#}, {lampReference.shadows} shadows.");
            }
            else
            {
                lamp.color = new Color(1f, .8f, .56f); lamp.range = 6; lamp.shadows = LightShadows.Soft; lampIntensity = 1.6f;
                Plugin.Log.LogInfo("Kennel lamp: no lit hideout lamp to match; a warm bulb of its own.");
            }
            lamp.enabled = true; lamp.intensity = lampIntensity;
            // Under the ceiling (or at a room's height where there's none to find), a little out in front of the bed.
            float height = Physics.Raycast(Home.position + Vector3.up * .5f, Vector3.up, out RaycastHit ceiling, 4.5f, solidMask, QueryTriggerInteraction.Ignore)
                ? ceiling.point.y - Home.position.y - .35f : 2.3f;
            lamp.transform.localPosition = new Vector3(0, Mathf.Clamp(height, 1.5f, 3f), .3f);
        }

        // The military kennel's own lamp, on the kennel model 't' (scaled as placed).
        private void LightDoorLamp(Transform t)
        {
            var go = new GameObject("Military kennel door lamp");
            go.transform.SetParent(t.parent, false);
            go.transform.position = t.TransformPoint(DoorLampAt);
            doorLamp = go.AddComponent<Light>();
            doorLamp.type = LightType.Point;
            doorLamp.color = lamp != null ? lamp.color : new Color(1f, .85f, .65f);
            doorLamp.range = 2.2f; doorLamp.shadows = LightShadows.None; doorLamp.intensity = DoorLampIntensity;
        }
        // The kennel: bed, food bowl and water bowl, as new objects in the hideout scene (copies of the hideout's own pet
        // bed and bowls; HideoutProps). Nothing of the game's is changed; all of it is destroyed when the dog leaves the
        // hideout (Dispose).
        public void PlaceHome(Vector3 point, Quaternion rotation)
        {
            if(Home != null) { Home.position=point; Home.rotation=rotation; Physics.SyncTransforms(); PlaceLamp(); return; }
            Home=new GameObject("K9 kennel").transform;UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Home.gameObject, dog.gameObject.scene);Home.position=point;Home.rotation=rotation;
            PlaceLamp();
            // The hideout's own pet bed (cat-sized, so stretched to fit the dog, long side along the way it lies);
            // without it, the bed from kennel.props.
            Bounds bed; float top=0;
            bedParts.Clear(); bedRaised = 0;
            Transform hideoutBed=HideoutProps.Spawn(HideoutProps.Bed,Home,new Vector3(0,0,BedZ),out bed,turn:90,scale:BedStretch);
            bool placed=hideoutBed != null;
            bedModel=hideoutBed;
            if(placed) bedParts.Add(hideoutBed);
            if(placed) top=HideoutProps.BedFloor*BedStretch.z;
            else
            {
                props = PropModel.Load(Plugin.AssetFolder);
                Transform modelBed=props != null && props.Measure("bed",out bed,out top) ? props.Spawn("bed",Home,new Vector3(0,0,BedZ),BedScale) : null;
                placed=modelBed != null;
                bedModel=modelBed;
                if(placed) bedParts.Add(modelBed);
                bed=new Bounds(new Vector3(0,0,BedZ),bed.size*BedScale); top*=BedScale;
            }
            if(placed)
            {
                // An invisible cushion, exactly as high as the bed's own, so the dog lies on the bed: not sunk into it, not floating.
                var cushion=new GameObject("K9 bed cushion");cushion.transform.SetParent(Home,false);bedParts.Add(cushion.transform);
                var box=cushion.AddComponent<BoxCollider>();box.center=new Vector3(0,top/2,BedZ);
                box.size=new Vector3(bed.size.x*.62f,top,bed.size.z*.62f);
                cushionBox=box;
                bedBounds=bed;
                // Both bowls side by side in front of the bed, out past the dog house's door once there is one.
                bowlZ=BedZ+bed.extents.z+DogHouseRoom/2+.2f;
                foodFill=ModelBowl("Food bowl",new Vector3(-.19f,0,bowlZ),foodMaterial);
                waterFill=ModelBowl("Water bowl",new Vector3(.19f,0,bowlZ),waterMaterial);
                // What your interact key finds when you look at the bowls or the front of the bed (first person).
                var zone=new GameObject("K9 kennel interaction"){layer=LayerMask.NameToLayer("Interactive")};
                zone.transform.SetParent(Home,false);
                var trigger=zone.AddComponent<BoxCollider>();trigger.isTrigger=true;
                trigger.center=new Vector3(0,.35f,bowlZ-.25f);trigger.size=new Vector3(1.1f,.7f,.8f);
                zone.AddComponent<KennelInteractive>();
            }
            else
            {
                Primitive("Bed base",PrimitiveType.Cube,Home,new Vector3(0,.035f,-.18f),new Vector3(.85f,.07f,1.2f),leather);
                Primitive("Bed cushion",PrimitiveType.Cube,Home,new Vector3(0,.08f,-.18f),new Vector3(.76f,.05f,1.1f),cushion);
                foreach(float x in new[]{-.4f,.4f}) Primitive("Bed bolster",PrimitiveType.Cube,Home,new Vector3(x,.14f,-.18f),new Vector3(.1f,.16f,1.2f),cushion);
                Primitive("Back bolster",PrimitiveType.Cube,Home,new Vector3(0,.14f,-.74f),new Vector3(.85f,.16f,.1f),cushion);
                foodFill=Bowl("Food dish",new Vector3(-.65f,0,.5f),foodMaterial);
                waterFill=Bowl("Water dish",new Vector3(.65f,0,.5f),waterMaterial);
            }
            BuildUpgrades();
            Physics.SyncTransforms();
        }
        private Transform ModelBowl(string name,Vector3 position,Material fill)
        {
            if(HideoutProps.Spawn(HideoutProps.Bowl,Home,position,out _) == null) props?.Spawn("bowl",Home,position,1f);
            return Primitive(name+" contents",PrimitiveType.Cylinder,Home,position+Vector3.up*.03f,new Vector3(.17f,.006f,.17f),fill);
        }
        private Transform Bowl(string name,Vector3 position,Material fill)
        {
            var basePart=Primitive(name,PrimitiveType.Cylinder,Home,position+Vector3.up*.025f,new Vector3(.3f,.025f,.3f),steel);
            var rim=Ring(name+" rim",Home,.145f,.018f,steel);rim.localPosition=position+Vector3.up*.07f;
            return Primitive(name+" contents",PrimitiveType.Cylinder,Home,position+Vector3.up*.05f,new Vector3(.255f,.01f,.255f),fill);
        }
        // ---- cosmetics (Tactical K9 Facility) ---------------------------------------------------------------------

        public void SetCosmetics(string collarStyleName)
        {
            if (collarStyleName != collarStyle) SetCollar(collarStyleName);
        }

        // ---- the kennel's upgrades --------------------------------------------------------------------------------

        private int kennelLevel;
        private int solidMask = Physics.DefaultRaycastLayers;
        private Transform upgrades;

        /// <summary>
        /// What the built kennel level adds around the bed: a toy box (any kennel); a cabinet for the dog's armour, a food
        /// barrel, a sack of K9 dog food and a wooden dog house over the bed (Working Dog Station); a metal table with a
        /// medical case and medical supplies on it, and the military K9 kennel in place of the dog house (Tactical K9
        /// Facility). The cabinet, barrel and table are copies of the hideout's own props; the toy box, food sack, dog house
        /// and military kennel are custom models (kennel_models.props), the food sack falling back to the hideout's own. Each is set down only where it clears the hideout's own geometry
        /// (tried on the other side of the bed if not); a piece with no room is left out. A prop the hideout doesn't have
        /// becomes simple shapes.
        /// </summary>
        public void SetKennelLevel(int level, int mask)
        {
            kennelLevel = level; solidMask = mask;
            if (Home != null) BuildUpgrades();
        }

        private void BuildUpgrades()
        {
            if (upgrades != null) UnityEngine.Object.Destroy(upgrades.gameObject);
            upgrades = null;
            RaiseBed(0);
            hasShelter = false;
            if (bedModel != null) bedModel.gameObject.SetActive(true);
            if (kennelLevel < 1) return;
            upgrades = new GameObject("Kennel upgrades").transform;
            upgrades.SetParent(Home, false);
            if (models == null) models = PropModel.Load(Plugin.AssetFolder, "kennel_models.props");
            Piece("Toy box", kennelLevel >= 3 ? ToyBoxBesideDoorAt : ToyBoxAt, new Vector3(.62f, .28f, .36f), p => Model("toybox", p, -15), null);
            if (kennelLevel < 2) { Physics.SyncTransforms(); return; }
            Material olive = Mat(new Color(.24f, .26f, .18f)), dark = Mat(new Color(.07f, .07f, .07f)), bin = Mat(new Color(.12f, .24f, .42f));
            Piece("K9 armour cabinet", CabinetAt, new Vector3(.61f, .96f, .34f), p => Copy(HideoutProps.Cabinet, p), p =>
            {
                Primitive("Locker body", PrimitiveType.Cube, p, new Vector3(0, .575f, 0), new Vector3(.55f, 1.15f, .45f), olive);
                Primitive("Locker door seam", PrimitiveType.Cube, p, new Vector3(0, .6f, .226f), new Vector3(.006f, 1.0f, .004f), dark);
                Primitive("Locker handle", PrimitiveType.Cube, p, new Vector3(.05f, .65f, .232f), new Vector3(.02f, .12f, .015f), steel);
                Primitive("Locker vents", PrimitiveType.Cube, p, new Vector3(0, 1.0f, .226f), new Vector3(.4f, .06f, .004f), dark);
            });
            Piece("Food storage barrel", BarrelAt, new Vector3(.5f, .65f, .45f), p => Copy(HideoutProps.Barrel, p), p =>
            {
                Primitive("Bin", PrimitiveType.Cube, p, new Vector3(0, .17f, 0), new Vector3(.42f, .34f, .32f), bin);
                Primitive("Bin lid", PrimitiveType.Cube, p, new Vector3(0, .35f, 0), new Vector3(.44f, .03f, .34f), dark);
            });
            Piece("Dog food bag", new Vector3(.55f, 0, bowlZ + .07f), new Vector3(.4f, .62f, .32f), p => Model("foodbag", p, -25) || Copy(HideoutProps.FoodBag, p, turn: -25), null);
            // The military kennel from level 3 (the dog house if its model is missing).
            if (kennelLevel < 3 || !Shelter("crate")) Shelter("doghouse");
            if (kennelLevel < 3) { Physics.SyncTransforms(); return; }
            Material white = Mat(new Color(.85f, .85f, .82f)), red = Mat(new Color(.7f, .08f, .06f));
            Piece("Veterinary table", TableAt, new Vector3(1.22f, .9f, .62f), p =>
            {
                if (HideoutProps.Spawn(HideoutProps.Table, p, Vector3.zero, out Bounds table) == null) return false;
                // The med station's medical case (without its fold-out tray) and a pile of supplies on top.
                HideoutProps.Spawn(HideoutProps.MedCase, p, new Vector3(.2f, 0, 0), out _, height: table.max.y, leaveOut: "Medical_Door");
                HideoutProps.Spawn(HideoutProps.MedSupplies, p, new Vector3(-.3f, 0, .05f), out _, turn: 30, height: table.max.y);
                return true;
            }, p =>
            {
                Primitive("Table top", PrimitiveType.Cube, p, new Vector3(0, .82f, 0), new Vector3(.9f, .04f, .55f), steel);
                foreach (float x in new[] { -.4f, .4f }) foreach (float z in new[] { -.23f, .23f })
                    Primitive("Table leg", PrimitiveType.Cube, p, new Vector3(x, .4f, z), new Vector3(.035f, .8f, .035f), steel);
                Primitive("First-aid case", PrimitiveType.Cube, p, new Vector3(.2f, .92f, 0), new Vector3(.3f, .16f, .2f), white);
                Primitive("Red cross", PrimitiveType.Cube, p, new Vector3(.2f, .92f, -.101f), new Vector3(.1f, .03f, .004f), red);
                Primitive("Red cross", PrimitiveType.Cube, p, new Vector3(.2f, .92f, -.101f), new Vector3(.03f, .1f, .004f), red);
            });
            Physics.SyncTransforms();
        }

        // A shelter model over the bed, the door facing the bowls. The dog house is stretched to the bed: as wide and deep
        // as the bed and a little more, its height in proportion. The military kennel keeps its own proportions and bed
        // (see MilitaryKennelHeight). The dog lying on the bed is inside; the shelter has no colliders, so the dog walks
        // in and out freely. False if the model isn't there.
        private bool Shelter(string name)
        {
            if (models == null || !models.Measure(name, out Bounds house, out _) || house.size.x <= 0 || house.size.z <= 0) return false;
            bool military = name == "crate";
            var piece = new GameObject(military ? "Military K9 kennel" : "Dog house").transform;
            piece.SetParent(upgrades, false);
            piece.localPosition = new Vector3(bedBounds.center.x, 0, bedBounds.center.z);
            Transform t = models.Spawn(name, piece, Vector3.zero, 1f);
            if (t == null) { UnityEngine.Object.Destroy(piece.gameObject); return false; }
            float sx, sy, sz;
            if (military)
            {
                sx = sy = sz = MilitaryKennelHeight / house.size.y;
                if (bedModel != null) bedModel.gameObject.SetActive(false);
            }
            else
            {
                sx = (bedBounds.size.x + DogHouseRoom * 2) / house.size.x; sz = (bedBounds.size.z + DogHouseRoom * 2) / house.size.z;
                sy = Mathf.Max(sx, sz);
            }
            float insideZ = military ? MilitaryKennelInsideZ : house.center.z;
            t.localScale = new Vector3(sx, sy, sz);
            t.localPosition = new Vector3(-house.center.x * sx, 0, -insideZ * sz);
            if (military) LightDoorLamp(t);
            hasShelter = true;
            shelterFrontZ = piece.localPosition.z + t.localPosition.z + (military ? MilitaryKennelFrontZ : house.max.z) * sz;
            shelterHalfX = house.extents.x * sx;
            // Its floor stands off the ground (the dog house's legs, the kennel's pallet): the bed goes up onto it, and the
            // dog with it.
            RaiseBed(models.FloorHeight(name) * sy);
            return true;
        }

        private void RaiseBed(float height)
        {
            foreach (Transform part in bedParts) if (part != null) part.localPosition += Vector3.up * (height - bedRaised);
            bedRaised = height;
        }

        private static bool Copy(string mesh, Transform piece, float turn = 0) => HideoutProps.Spawn(mesh, piece, Vector3.zero, out _, turn) != null;

        // One of the custom kennel models (kennel_models.props), its front (the door, the print) turned 'turn' degrees
        // from facing out of the kennel like the bowls.
        private bool Model(string name, Transform piece, float turn)
        {
            Transform t = models?.Spawn(name, piece, Vector3.zero, 1f);
            if (t == null) return false;
            t.localRotation = Quaternion.Euler(0, turn, 0);
            return true;
        }

        // The hideout prop (model) if it can be copied, else the stand-in (build; none if null).
        private void Piece(string name, Vector3 at, Vector3 size, Func<Transform, bool> model, Action<Transform> build, bool checkFloor = true)
        {
            foreach (Vector3 spot in new[] { at, new Vector3(-at.x, at.y, at.z) })
            {
                // Stood on the floor under it (not at the bed's height): a step or a dip there means somewhere else.
                if (!Floor(Home.TransformPoint(spot), out float floorY)) continue;
                Vector3 grounded = new Vector3(spot.x, Home.InverseTransformPoint(new Vector3(0, floorY, 0)).y, spot.z);
                Vector3 centre = Home.TransformPoint(grounded + Vector3.up * (size.y / 2 + .03f));
                if (checkFloor && Blocked(centre, size / 2 * .95f)) continue;
                var piece = new GameObject(name).transform;
                piece.SetParent(upgrades, false); piece.localPosition = grounded;
                if (!model(piece)) build?.Invoke(piece);
                return;
            }
            Plugin.Log.LogInfo($"No room for the kennel's {name.ToLowerInvariant()}; left out.");
        }

        // The walkable floor at 'point', within a few centimetres of the kennel's own floor.
        private bool Floor(Vector3 point, out float y)
        {
            y = 0; float best = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(point + Vector3.up * .4f, Vector3.down, .8f, solidMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .9f || hit.transform.IsChildOf(Home) || hit.transform.IsChildOf(dog) || hit.collider.GetComponentInParent<EFT.Player>() != null) continue;
                float gap = Mathf.Abs(hit.point.y - Home.position.y);
                if (gap <= .07f && gap < best) { best = gap; y = hit.point.y; }
            }
            return best < float.MaxValue;
        }

        private bool Blocked(Vector3 centre, Vector3 half)
        {
            foreach (Collider c in Physics.OverlapBox(centre, half, Home.rotation, solidMask, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(Home) || c.transform.IsChildOf(dog) || c.GetComponentInParent<EFT.Player>() != null) continue;
                return true;
            }
            return false;
        }

        public Vector3? FoodBowl => foodFill != null ? foodFill.position : (Vector3?)null;
        public Vector3? WaterBowl => waterFill != null ? waterFill.position : (Vector3?)null;

        // The contents sit lower in the dish as it empties.
        public void SetSupplies(float food,float water)
        {
            Fill(foodFill, food); Fill(waterFill, water);
        }
        private static void Fill(Transform contents, float level)
        {
            if(contents==null) return;
            contents.gameObject.SetActive(level>0.5f);
            Vector3 p=contents.localPosition; p.y=.012f+.03f*Mathf.Clamp01(level/100f); contents.localPosition=p;
        }
        public void Dispose()
        {
            if(Home!=null) UnityEngine.Object.Destroy(Home.gameObject);
            props?.Dispose(); props=null;
            models?.Dispose(); models=null;
            foreach(var resource in resources) if(resource!=null) UnityEngine.Object.Destroy(resource);
            resources.Clear();
        }
    }
}
