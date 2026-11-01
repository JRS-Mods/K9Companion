using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.WeaponModding;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The dog's gear and supplies as themselves wherever the game shows an item in 3D: the inspect window, and lying on
    /// the ground. The items are copies of game items (an armoured rig, a flash storage module, a ring for the collars, a
    /// tin, dried meat, paracord, a Grizzly medkit, medical tools, a badge), so the game builds their models; once it has, those
    /// are hidden and our own model stands in, at its real size where the game's model was, so loot lies as it would.
    /// For gear that's the model as it was made (gear_&lt;look&gt;_item.mesh, tools/convert_item_models.py), not the copy
    /// stretched round the dog, which looks odd off it; the supplies are gear_item_&lt;key&gt; (tools/convert_dog_items.py). In an inspect window it is turned to face the camera. A
    /// jacket with the action camera on it shows the camera on top. The game reuses item models from a pool: one built for the
    /// dog's gear gets its own back when it's put away, or when the game builds one of its own items with it.
    /// </summary>
    internal static class GearVisuals
    {
        // The game items the dog's gear and supplies are copied from: their models may come back from the pool still dressed.
        private static readonly HashSet<string> Sources = new HashSet<string>
        {
            "628cd624459354321c4b7fa2", "5c05300686f7746dce784e5d",   // plate carrier (jackets), flash storage module (kits)
            "5d235a5986f77443f6329bc6",                               // skull ring (collars)
            "57347d7224597744596b4e72", "57347da92459774491567cf5",   // small and large tin of beef stew (dog food)
            "65815f0e647e3d7246384e14", "5c12688486f77426843c7d32",   // dried meat (dog meat), paracord (training kit)
            "590c657e86f77412b013051d", "619cc01e0a7c3a1a2731940c",   // Grizzly (vet kit), medical tools (vet supply case)
            "5bc9bdb8d4351e003562b8a1",                               // silver badge (K9 tag)
        };
        private static readonly Dictionary<string, (Mesh mesh, Material material)> models = new Dictionary<string, (Mesh, Material)>();

        internal static void Patch(Harmony harmony)
        {
            var create = AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateItemAsync))
                         ?? throw new MissingMethodException(nameof(ObjectsFactory), nameof(ObjectsFactory.CreateItemAsync));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(GearVisuals), nameof(Created)));
            var position = AccessTools.Method(typeof(WeaponPreview), nameof(WeaponPreview.PositionGameObject))
                           ?? throw new MissingMethodException(nameof(WeaponPreview), nameof(WeaponPreview.PositionGameObject));
            harmony.Patch(position, postfix: new HarmonyMethod(typeof(GearVisuals), nameof(Previewed)));
        }

        private static void Created(Item item, ref Task<GameObject> __result)
        {
            if (item == null || __result == null) return;
            string look = DogGear.AnyLook(item);
            // The jacket's durability as the dog has worn it, for the inspect window and tooltips.
            if (DogGear.IsJacket(item)) DogArmour.ShowWear(item);
            if (look != null) __result = Dress(__result, item, look);
            else if (Sources.Contains(item.TemplateId.ToString())) __result = Undress(__result);
        }

        // In an inspect window: turn ours to the camera once the game has put the item under its preview pivot.
        private static void Previewed(GameObject itemGameObject)
        {
            var visual = itemGameObject != null ? itemGameObject.GetComponent<DogGearVisual>() : null;
            if (visual != null) visual.FacePreview(itemGameObject.transform.parent, itemGameObject.GetComponent<PreviewPivot>());
        }

        private static async Task<GameObject> Dress(Task<GameObject> made, Item item, string look)
        {
            GameObject model = await made;
            try
            {
                if (model == null) return model;
                var visual = model.GetComponent<DogGearVisual>() ?? model.AddComponent<DogGearVisual>();
                var parts = new List<string> { Off(look) };
                // The action camera on a jacket is drawn with it (the game's rig has no place for it).
                if (DogGear.IsJacket(item) && DogGear.CameraOn(item) != null) parts.Add(Off("camera_action"));
                visual.Show(parts, look);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"The {look} model couldn't be shown: {e.Message}"); }
            return model;
        }

        private static async Task<GameObject> Undress(Task<GameObject> made)
        {
            GameObject model = await made;
            if (model != null) model.GetComponent<DogGearVisual>()?.Restore();
            return model;
        }

        // The model of a piece of gear off the dog, or (if there isn't one) as worn.
        private static string Off(string look) => File.Exists(Path.Combine(Plugin.AssetFolder, $"gear_{look}_item.mesh")) ? look + "_item" : look;

        /// <summary>A gear model, read once: its mesh (as worn, or off the dog for an _item one), and its texture.</summary>
        internal static (Mesh mesh, Material material) Model(string part, string texture)
        {
            if (models.TryGetValue(part, out var cached) && cached.mesh != null) return cached;
            string meshPath = Path.Combine(Plugin.AssetFolder, "gear_" + part + ".mesh"), texturePath = Path.Combine(Plugin.AssetFolder, "gear_" + texture + ".jpg");
            if (!File.Exists(meshPath)) return (null, null);
            var mesh = new Mesh { name = "K9 gear " + part, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            using (var r = new BinaryReader(File.OpenRead(meshPath)))
            {
                if (r.ReadInt32() != 0x31544b4a) throw new InvalidDataException("Not a gear mesh: " + part);
                r.ReadBytes(r.ReadInt32());
                int count = r.ReadInt32();
                var positions = new Vector3[count]; var normals = new Vector3[count]; var uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    r.ReadBytes(32);   // bone weights: not needed off the dog
                }
                int blocks = r.ReadInt32();
                var triangles = new List<int>();
                for (int b = 0; b < blocks; b++) { int n = r.ReadInt32(); for (int i = 0; i < n; i++) triangles.Add(r.ReadInt32()); }
                mesh.vertices = positions; mesh.normals = normals; mesh.uv = uvs; mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
            }
            var material = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")) { name = "K9 gear " + texture };
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .3f);
            if (File.Exists(texturePath))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (ImageConversion.LoadImage(tex, File.ReadAllBytes(texturePath), false)) { tex.Compress(true); tex.Apply(true, true); material.mainTexture = tex; }
            }
            // The supplies' normal map (the relief their full-detail model had) and metal/smoothness map, as the kennel
            // props have them (PropModel): gear_<texture>_n.png and _ms.png, where the converter wrote them.
            string stem = Path.Combine(Plugin.AssetFolder, "gear_" + texture);
            Texture2D normal = DataTexture(stem + "_n.png", false), metalSmooth = DataTexture(stem + "_ms.png", true);
            if (normal != null && material.HasProperty("_BumpMap"))
            {
                mesh.RecalculateTangents();
                material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP");
            }
            if (metalSmooth != null && material.HasProperty("_MetallicGlossMap"))
            {
                material.SetTexture("_MetallicGlossMap", metalSmooth); material.EnableKeyword("_METALLICGLOSSMAP");
                if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", 1f);
            }
            return models[part] = (mesh, material);
        }

        // A map holding data rather than colour (normals, metal/smoothness): read as it is, without the colour correction,
        // and kept only on the graphics card. The normal map stays uncompressed (block compression shows on its relief).
        private static Texture2D DataTexture(string path, bool compress)
        {
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { name = Path.GetFileName(path) };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) return null;
            texture.anisoLevel = 4;
            if (compress) texture.Compress(true);
            texture.Apply(true, true);
            return texture;
        }
    }

    /// <summary>
    /// The gear's own model over a game item model (whose renderers it hides), until it's put away. It is drawn at its
    /// real size (the meshes are in metres, as worn), centred where the game's model was. On the ground it lies on its
    /// underside like the item it stands in for; in an inspect window it is turned to face the camera three-quarters on
    /// (the game turns its own model by that item's icon angle, which means nothing for ours).
    /// </summary>
    internal sealed class DogGearVisual : MonoBehaviour
    {
        // How the gear faces the inspect camera (which looks along +z): jackets side-on with the head end towards you,
        // head gear, the camera and the supplies nearly front-on; all tipped a little so the top shows.
        private static readonly Quaternion JacketView = Quaternion.Euler(-20, 0, 0) * Quaternion.Euler(0, 127, 0);
        private static readonly Quaternion SmallView = Quaternion.Euler(-15, 0, 0) * Quaternion.Euler(0, 155, 0);
        // A collar lies flat (its ring level): seen from well above, so the ring shows rather than its edge.
        private static readonly Quaternion CollarView = Quaternion.Euler(-60, 0, 0) * Quaternion.Euler(0, 155, 0);

        private readonly List<Renderer> hidden = new List<Renderer>();
        private readonly List<GameObject> shown = new List<GameObject>();
        private readonly List<Vector3> offsets = new List<Vector3>();   // each part's place on the first (the camera's on a jacket)
        private Bounds ours;
        private Vector3 centre;
        private bool jacket, collar;

        internal void Show(List<string> parts, string look)
        {
            Restore();
            jacket = look.StartsWith("harness_");
            collar = look.StartsWith("collar_");
            // Where the game's model is, in the item's own space (from the meshes, so it holds whether or not the game
            // has shown or hidden them yet). An item that's worn as well as dropped (a rig) has two models: the dropped one.
            DressItem dress = GetComponent<DressItem>();
            Transform model = dress != null && dress.LootPrefab != null ? dress.LootPrefab.transform : transform;
            Bounds? theirs = null;
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;   // meshes only, not effects
                if (r.enabled) { r.enabled = false; hidden.Add(r); }
                if (!r.transform.IsChildOf(model) || !(LocalBounds(r) is Bounds found)) continue;
                if (theirs is Bounds t) { t.Encapsulate(found); theirs = t; } else theirs = found;
            }
            var (first, _) = GearVisuals.Model(parts[0], look);
            if (first == null) { Restore(); return; }
            ours = first.bounds;
            centre = theirs?.center ?? Vector3.zero;
            // Lying as the game's model lies: our up along its thinnest side (a rig lies flat on its back or front).
            Quaternion lying = Quaternion.identity;
            if (theirs is Bounds them)
            {
                Vector3 size = them.size;
                Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                lying = Quaternion.FromToRotation(Vector3.up, thin);
            }
            foreach (string part in parts)
            {
                bool camera = part.StartsWith("camera_action");
                var (mesh, material) = GearVisuals.Model(part, camera ? "camera_action" : look);
                if (mesh == null) continue;
                var go = new GameObject("K9 gear model " + part);
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                // The camera off the dog stands on top of the jacket, a little forward of the middle, lens to the front.
                offsets.Add(camera && part.EndsWith("_item")
                    ? new Vector3(0, ours.max.y + mesh.bounds.extents.y, ours.center.z + ours.extents.z * .3f) - mesh.bounds.center
                    : Vector3.zero);
                shown.Add(go);
            }
            Place(lying, Vector3.one, centre);
        }

        /// <summary>
        /// In an inspect window the game has put the item under its preview pivot (turned by the item's icon angle,
        /// scaled, and shifted so the pivot point is in the middle); ours faces the camera from there, at its real size.
        /// </summary>
        internal void FacePreview(Transform pivot, PreviewPivot settings)
        {
            if (shown.Count == 0 || pivot == null) return;
            Vector3 scale = settings != null ? settings.scale : Vector3.one;
            var undo = new Vector3(1 / Mathf.Max(scale.x, 1e-4f), 1 / Mathf.Max(scale.y, 1e-4f), 1 / Mathf.Max(scale.z, 1e-4f));
            Quaternion turn = Quaternion.Inverse(pivot.localRotation) * (jacket ? JacketView : collar ? CollarView : SmallView);
            Place(turn, undo, settings != null ? settings.pivotPosition : centre);
        }

        // Ours turned and scaled as given, its middle on the given point of the item's own space.
        private void Place(Quaternion rotation, Vector3 scale, Vector3 at)
        {
            for (int i = 0; i < shown.Count; i++)
            {
                GameObject go = shown[i];
                if (go == null) continue;
                go.transform.localRotation = rotation;
                go.transform.localScale = scale;
                go.transform.localPosition = at - rotation * Vector3.Scale(ours.center - offsets[i], scale);
            }
        }

        // A renderer's mesh bounds in the item's own space.
        private Bounds? LocalBounds(Renderer r)
        {
            Bounds mesh;
            if (r is SkinnedMeshRenderer skinned) mesh = skinned.localBounds;
            else if (r.GetComponent<MeshFilter>() is MeshFilter filter && filter.sharedMesh != null) mesh = filter.sharedMesh.bounds;
            else return null;
            if (mesh.extents == Vector3.zero) return null;
            Matrix4x4 toItem = transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = mesh.center + Vector3.Scale(mesh.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = toItem.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            return new Bounds((min + max) / 2, max - min);
        }

        internal void Restore()
        {
            foreach (Renderer r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
            foreach (GameObject go in shown) if (go != null) Destroy(go);
            shown.Clear(); offsets.Clear();
        }

        // Put away (back into the game's pool): the game's model as it was.
        private void OnDisable() => Restore();
    }
}
