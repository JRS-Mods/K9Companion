using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace K9Companion
{
    /// <summary>
    /// Static props for the hideout kennel, built from the mod's own models: the bed and bowls
    /// (tools/convert_props.py, kennel.props) and the crate, dog house, toy box and food bag (tools/convert_kennel_models.py,
    /// kennel_models.props), each with JPEG textures. If a file is missing the kennel does without it (simple shapes, or
    /// the hideout's own props), so the kennel never depends on it.
    /// </summary>
    internal sealed class PropModel : IDisposable
    {
        private readonly Dictionary<string, (Mesh mesh, Material material)> parts = new Dictionary<string, (Mesh, Material)>();
        private readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();

        public static PropModel Load(string folder, string file = "kennel.props")
        {
            string path = Path.Combine(folder, file);
            if (!File.Exists(path)) { Plugin.Log.LogWarning(file + " not found; the kennel does without it."); return null; }
            var model = new PropModel();
            try { model.Read(folder, path); return model; }
            catch (Exception e) { Plugin.Log.LogError(file + " couldn't be read; the kennel does without it: " + e.Message); model.Dispose(); return null; }
        }

        private static int Count(BinaryReader r, int max) { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("Bad count " + n); return n; }
        private static string Text(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(Count(r, 1024)));

        private void Read(string folder, string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != 0x31505250) throw new InvalidDataException("Not a kennel.props file");
                int meshes = Count(r, 16);
                for (int m = 0; m < meshes; m++)
                {
                    string name = Text(r), texture = Text(r);
                    int nv = Count(r, 500000);
                    var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv];
                    for (int i = 0; i < nv; i++)
                    {
                        pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        nrm[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    int ni = Count(r, 3000000); var tris = new int[ni];
                    for (int i = 0; i < ni; i++) { tris[i] = r.ReadInt32(); if (tris[i] < 0 || tris[i] >= nv) throw new InvalidDataException("Bad index"); }
                    var mesh = new Mesh { name = name, indexFormat = nv > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16, vertices = pos, normals = nrm, uv = uv, triangles = tris };
                    mesh.RecalculateBounds(); mesh.RecalculateTangents(); resources.Add(mesh);
                    parts[name] = (mesh, MakeMaterial(Path.Combine(folder, texture), name == "bowl"));
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing data");
            }
        }

        private Material MakeMaterial(string texturePath, bool glossy)
        {
            var material = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse"));
            resources.Add(material);
            if (File.Exists(texturePath))
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                resources.Add(texture);
                if (ImageConversion.LoadImage(texture, File.ReadAllBytes(texturePath), false)) { texture.anisoLevel = 4; material.mainTexture = texture; }
            }
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", glossy ? .55f : .12f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", glossy ? .15f : 0f);
            // The model's own normal map (the relief its full-detail mesh had) and metal/smoothness map, where the
            // converter wrote them (kennel_<name>_n.png, _ms.png); without them the colour alone, as before.
            string stem = Path.Combine(Path.GetDirectoryName(texturePath) ?? "", Path.GetFileNameWithoutExtension(texturePath));
            Texture2D normal = DataTexture(stem + "_n.png"), metalSmooth = DataTexture(stem + "_ms.png");
            if (normal != null && material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP");
            }
            if (metalSmooth != null && material.HasProperty("_MetallicGlossMap"))
            {
                material.SetTexture("_MetallicGlossMap", metalSmooth); material.EnableKeyword("_METALLICGLOSSMAP");
                if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", 1f);
            }
            return material;
        }

        // A map holding data rather than colour (normals, metal/smoothness): read as it is, without the colour correction.
        private Texture2D DataTexture(string path)
        {
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { name = Path.GetFileName(path) };
            resources.Add(texture);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) return null;
            texture.anisoLevel = 4;
            return texture;
        }

        public Transform Spawn(string name, Transform parent, Vector3 localPosition, float scale)
        {
            if (!parts.TryGetValue(name, out var part)) return null;
            var go = new GameObject("K9 " + name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition; go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = part.material; renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
            return go.transform;
        }

        // A part's unscaled size, and how high its middle is (for the bed: the top of the cushion the dog lies on).
        public bool Measure(string name, out Bounds bounds, out float middleTop)
        {
            bounds = default; middleTop = 0;
            if (!parts.TryGetValue(name, out var part)) return false;
            bounds = part.mesh.bounds;
            Vector3 c = bounds.center, e = bounds.extents * .3f;
            foreach (Vector3 v in part.mesh.vertices)
                if (Mathf.Abs(v.x - c.x) < e.x && Mathf.Abs(v.z - c.z) < e.z && v.y > middleTop) middleTop = v.y;
            return true;
        }

        /// <summary>How high a part's inside floor is (unscaled): the lowest upward-facing surface in its middle, off the ground.</summary>
        public float FloorHeight(string name)
        {
            if (!parts.TryGetValue(name, out var part)) return 0;
            Bounds b = part.mesh.bounds;
            Vector3 c = b.center, e = b.extents * .3f;
            Vector3[] v = part.mesh.vertices, n = part.mesh.normals;
            if (n.Length != v.Length) return 0;
            float floor = float.MaxValue;
            for (int i = 0; i < v.Length; i++)
                if (Mathf.Abs(v[i].x - c.x) < e.x && Mathf.Abs(v[i].z - c.z) < e.z && n[i].y > .8f && v[i].y > b.min.y + .02f && v[i].y < floor) floor = v[i].y;
            return floor == float.MaxValue ? 0 : floor - b.min.y;
        }

        public void Dispose()
        {
            foreach (var resource in resources) if (resource != null) UnityEngine.Object.Destroy(resource);
            resources.Clear(); parts.Clear();
        }
    }
}
