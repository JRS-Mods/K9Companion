using System;
using System.Collections.Generic;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The kennel's furniture, copied from props the hideout scene already has (the pet bed and bowls from the rest space
    /// and kitchen, a cabinet, a food barrel, the med station's metal table and medical case), so it looks like the rest
    /// of the hideout. They're found by their first-detail mesh, even where the area they belong to isn't built (its
    /// objects are only switched off). A copy keeps nothing but the model: its scripts, colliders and the rest are
    /// stripped before it is switched on. The originals are only read. A prop the hideout doesn't have returns null and
    /// the kennel uses its older models or simple shapes.
    /// </summary>
    internal static class HideoutProps
    {
        public const string Bed = "Pet_bed_LOD0", Bowl = "Pet_bowl_LOD0", FoodBag = "Pet_feed_LOD0", Barrel = "Food_barrel_closed_LOD0",
                            Cabinet = "Old_Electrical_Cabinet_C_LOD0", Table = "metal_table001", MedCase = "Medical_case_LOD0",
                            MedSupplies = "item_barter_medical_pile_LOD0";
        private static readonly HashSet<string> Wanted = new HashSet<string> { Bed, Bowl, FoodBag, Barrel, Cabinet, Table, MedCase, MedSupplies };

        // The pet bed's inside floor, in its own units (measured from the model), for the cushion the dog lies on.
        public const float BedFloor = .032f;

        // Hideout models are made Z-up with their front to -Y: this stands one up with its front to the kennel's front (+Z).
        private static readonly Quaternion Upright = Quaternion.Euler(-90, 0, 0);

        private static readonly Dictionary<string, MeshFilter> sources = new Dictionary<string, MeshFilter>();
        private static int scannedFrame = -1;

        private static MeshFilter Source(string mesh)
        {
            if (sources.TryGetValue(mesh, out var found) && found != null) return found;
            if (scannedFrame == Time.frameCount) return null;
            // A new hideout visit (or the first): look through the scene's meshes once.
            scannedFrame = Time.frameCount;
            sources.Clear();
            foreach (var filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                var shared = filter.sharedMesh;
                if (shared == null || !Wanted.Contains(shared.name) || sources.ContainsKey(shared.name)) continue;
                if (filter.name.StartsWith("K9 ", StringComparison.Ordinal) || filter.GetComponentInParent<HideoutCopy>(true) != null) continue;
                sources[shared.name] = filter;
            }
            if (sources.Count < Wanted.Count)
                foreach (string name in Wanted) if (!sources.ContainsKey(name)) Plugin.Log.LogWarning($"Hideout prop {name} not found; the kennel uses a stand-in.");
            return sources.TryGetValue(mesh, out found) ? found : null;
        }

        /// <summary>
        /// A copy of the prop, standing upright on <paramref name="parent"/>'s floor at <paramref name="position"/>,
        /// facing its front turned by <paramref name="turn"/> degrees, at <paramref name="scale"/> (along the model's own
        /// width, depth and height), its base at <paramref name="height"/>. <paramref name="bounds"/> is where the prop's
        /// main mesh ended up, in the parent's frame. Parts whose mesh name starts with one of <paramref name="leaveOut"/>
        /// are dropped.
        /// </summary>
        public static Transform Spawn(string mesh, Transform parent, Vector3 position, out Bounds bounds, float turn = 0,
                                      Vector3? scale = null, float height = 0, params string[] leaveOut)
        {
            bounds = default;
            var source = Source(mesh);
            if (source == null) return null;
            // The prop: the LOD group the mesh belongs to, or the mesh's own object when it has none.
            var group = source.GetComponentInParent<LODGroup>(true);
            Transform original = group != null ? group.transform : source.transform;
            try
            {
                // Copied into a switched-off holder so none of its scripts wake before they're gone.
                var holder = new GameObject("K9 " + mesh.Replace("_LOD0", "")).transform;
                holder.gameObject.SetActive(false);
                holder.SetParent(parent, false);
                holder.localPosition = position; holder.localRotation = Quaternion.Euler(0, turn, 0);
                holder.gameObject.AddComponent<HideoutCopy>();
                Transform copy = UnityEngine.Object.Instantiate(original.gameObject, holder, false).transform;
                copy.name = original.name;
                copy.gameObject.SetActive(true);
                Strip(copy, leaveOut);

                // Upright: the main mesh's rotation within the prop is undone, then the model stood up.
                Quaternion within = Quaternion.Inverse(original.rotation) * source.transform.rotation;
                copy.localPosition = Vector3.zero;
                copy.localRotation = Upright * Quaternion.Inverse(within);
                Vector3 own = Divide(original.lossyScale, source.transform.lossyScale);
                copy.localScale = Vector3.Scale(scale ?? Vector3.one, own);

                // Stood on the floor, centred on the spot.
                Transform main = Find(copy, mesh) ?? copy;
                bounds = Measure(main.GetComponent<MeshFilter>()?.sharedMesh, main.localToWorldMatrix, holder.worldToLocalMatrix);
                copy.localPosition = new Vector3(-bounds.center.x, height - bounds.min.y, -bounds.center.z);
                bounds = Measure(main.GetComponent<MeshFilter>()?.sharedMesh, main.localToWorldMatrix, parent.worldToLocalMatrix);

                holder.gameObject.SetActive(true);
                return holder;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't copy hideout prop {mesh}: {e.Message}");
                return null;
            }
        }

        // Keep the look only: meshes, renderers and LOD groups.
        private static void Strip(Transform copy, string[] leaveOut)
        {
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
            {
                string name = filter.sharedMesh != null ? filter.sharedMesh.name : "";
                foreach (string part in leaveOut)
                    if (name.StartsWith(part, StringComparison.Ordinal) && filter.transform != copy) { UnityEngine.Object.DestroyImmediate(filter.gameObject); break; }
            }
            // Scripts first: some insist on the colliders or bodies they use.
            for (int pass = 0; pass < 3; pass++)
                foreach (var component in copy.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform || component is MeshFilter || component is MeshRenderer || component is LODGroup) continue;
                    if (pass == 0 && !(component is MonoBehaviour)) continue;
                    try { UnityEngine.Object.DestroyImmediate(component); } catch (Exception) { }
                }
            // The game may have hidden the original's renderers (culling); the copy shows.
            foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true)) { renderer.enabled = true; renderer.forceRenderingOff = false; }
            foreach (var group in copy.GetComponentsInChildren<LODGroup>(true)) group.enabled = true;
        }

        private static Transform Find(Transform copy, string mesh)
        {
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && filter.sharedMesh.name == mesh) return filter.transform;
            return null;
        }

        private static Bounds Measure(Mesh mesh, Matrix4x4 meshToWorld, Matrix4x4 worldToFrame)
        {
            if (mesh == null) return new Bounds(Vector3.zero, Vector3.zero);
            Bounds local = mesh.bounds;
            Matrix4x4 m = worldToFrame * meshToWorld;
            var result = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0 ? a.x / b.x : 1, b.y != 0 ? a.y / b.y : 1, b.z != 0 ? a.z / b.z : 1);

        /// <summary>Marks the kennel's copies, so they're never mistaken for the hideout's own props.</summary>
        private sealed class HideoutCopy : MonoBehaviour { }
    }
}
