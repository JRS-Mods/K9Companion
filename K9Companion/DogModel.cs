using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace K9Companion
{
    /// <summary>Rotations as vectors, for adding and fading them. Only plain arithmetic.</summary>
    internal static class PoseMath
    {
        public static Quaternion Mul(Quaternion a, Quaternion b)
            => new Quaternion(a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                              a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        // The rotation as axis times angle (radians), the short way round.
        public static Vector3 Log(Quaternion q)
        {
            if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
            float s = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (s < 1e-5f) return new Vector3(q.x * 2, q.y * 2, q.z * 2);
            float k = 2 * (float)Math.Atan2(s, q.w) / s;
            return new Vector3(q.x * k, q.y * k, q.z * k);
        }

        public static Quaternion Exp(Vector3 r)
        {
            float a = (float)Math.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z);
            if (a < 1e-5f) return new Quaternion(r.x * .5f, r.y * .5f, r.z * .5f, 1);
            float s = (float)Math.Sin(a * .5f) / a;
            return new Quaternion(r.x * s, r.y * s, r.z * s, (float)Math.Cos(a * .5f));
        }
    }

    internal sealed class DogModel : IDisposable
    {
        private struct Pose
        {
            public Vector3 P, S;
            public Quaternion Q;
            public static Pose Blend(Pose a, Pose b, float t)
            { return new Pose { P = Vector3.Lerp(a.P, b.P, t), Q = Quaternion.Slerp(a.Q, b.Q, t), S = Vector3.Lerp(a.S, b.S, t) }; }
        }
        // Loop: the last frame is the first again (a cycle), so frames carry on round the end.
        private sealed class Clip { public int Frames; public float Fps; public Pose[] Poses; public bool Loop; }

        // A clip's number on the wire (DogNet): every copy of the mod has the same list.
        public static int ClipNumber(string name) => name == null ? -1 : Array.IndexOf(Required, name);
        public static string ClipName(int number) => number >= 0 && number < Required.Length ? Required[number] : null;

        public static readonly string[] Required = { "Stand_00", "Loco_Walk", "Loco_Trot", "Loco_Run", "Loco_Sprint", "Sitting_00", "Trans_Stand_To_Sitting", "Trans_Sitting_To_Stand", "Loco_Walk_L", "Loco_Walk_R", "Loco_Trot_L", "Loco_Trot_R", "Loco_Run_L", "Loco_Run_R", "Loco_Sprint_L", "Loco_Sprint_R", "Trans_TurnL90", "Trans_TurnR90", "Loco_Sneak", "Loco_Sneak_L", "Loco_Sneak_R", "Stand_Sneak_00", "Loco_WalkSlow", "Loco_WalkSlow_L", "Loco_WalkSlow_R", "Stand_01", "Stand_02", "Sitting_01", "Sitting_02", "Lying_00", "Lying_01", "Lying_02", "Sleeping_01", "Trans_Stand_To_Lying", "Trans_Lying_To_Stand", "Trans_Lying_to_Sleeping", "Trans_Sleeping_to_Lying", "Death_Stand_L", "Death_Stand_R", "Death_Stand_L_Pose", "Death_Stand_R_Pose", "Hit_ChestL_Light", "Hit_ChestR_Light", "Hit_ChestL_Heavy", "Hit_ChestR_Heavy", "Attack_BiteL", "Attack_BiteR", "Agressive_01", "Jump_Stand", "Jump_Trot", "Jump_Run" };

        private Transform[] bones;
        private Transform root;
        private Matrix4x4[] bind;
        private Pose[] displayed, previous;
        private Vector3[] applied;
        // The box every pose of the dog fits in (lying, a jump's height), in the model's own space: what the game culls by.
        private Bounds poseBounds;
        private readonly List<Renderer> renderers = new List<Renderer>();
        // Clip data never changes, so every dog of a breed (raid, hideout, the raid screen's, other players') shares one
        // copy, kept per breed's file.
        private static readonly Dictionary<string, Dictionary<string, Clip>> sharedClips = new Dictionary<string, Dictionary<string, Clip>>();
        private static readonly Dictionary<string, Task<Dictionary<string, Clip>>> clipsLoading = new Dictionary<string, Task<Dictionary<string, Clip>>>();
        private Dictionary<string, Clip> clips;
        private readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();
        private string current;
        private float time;
        // Going from one clip to another (inertial blending): the new clip plays at once and what differs from the pose
        // the dog was really in, and how fast that was moving, dies away over the blend. Nothing freezes while it does,
        // and a new clip can cut in at any moment, as it just starts again from wherever the dog is.
        private const float DefaultBlend = .28f, MaxTurnRate = 20f, MaxSlideRate = 5f;
        private Vector3[] offsetTurn, turnRate, offsetSlide, slideRate;
        private float sinceSwitch, settleRate, nextBlend = DefaultBlend, lastDt = 1 / 60f;
        private bool settling;
        private int[] legBones;
        // The head and neck turning on top of the animation (LookAt).
        private Transform[] lookChain;
        private static readonly float[] LookShare = { .1f, .15f, .2f, .25f, .3f };
        private float[] lookShare = LookShare;
        private float lookYaw, lookPitch, wantYaw, wantPitch;

        public string Current => current;
        // Set for dogs that only lie about (the hideout's other dogs): unseen, their bones aren't moved.
        public bool SkipUnseen { get; set; }
        public Breed Breed { get; private set; }
        public int Coat { get; private set; }
        // Its growth (size against its model as made, Growth) and its whole size on screen (with the Scale setting).
        public float Growth { get; private set; } = 1;
        public float Size => Root != null ? Root.localScale.x : Growth;
        // What it's playing, as another player's machine needs to play the same (DogNet).
        public string Layer { get; private set; }
        public float LayerWeight { get; private set; }
        public float Playback { get; private set; }
        public bool Looping { get; private set; }
        public bool Walking { get; private set; }
        public int Starts { get; private set; }
        public float LookYaw => lookYaw;
        public float LookPitch => lookPitch;
        public Transform Bone(string name) => Array.Find(bones, b => b.name == name);
        // The model's own space (the one the gear meshes are made in): scaled with the dog.
        public Transform Root => root;

        private static Vector3 V(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        private static Pose ReadPose(BinaryReader r)
        {
            return new Pose { P = V(r), Q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), S = V(r) };
        }
        private static int Count(BinaryReader r, int max)
        { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("Invalid dog asset count: " + n); return n; }
        private static string Text(BinaryReader r) { return Encoding.UTF8.GetString(r.ReadBytes(Count(r, 4096))); }

        // Reads a breed's animations in the background (when the game starts, and when a dog of that breed is due): plain
        // data, a lot of it, that needs nothing of Unity's.
        internal static void WarmUp(Breed breed)
        {
            string path = breed.DogFile;
            lock (sharedClips)
                if (!sharedClips.ContainsKey(path) && !clipsLoading.ContainsKey(path)) clipsLoading[path] = Task.Run(() => ReadClips(path));
        }

        internal static bool ClipsReady(Breed breed)
        {
            lock (sharedClips) return !clipsLoading.TryGetValue(breed.DogFile, out var t) || t.IsCompleted;
        }

        private static Dictionary<string, Clip> ClipsOf(string path)
        {
            Task<Dictionary<string, Clip>> loading;
            lock (sharedClips)
            {
                if (sharedClips.TryGetValue(path, out var ready)) return ready;
                clipsLoading.TryGetValue(path, out loading);
            }
            // Ready by now, normally: if not, this waits for the background read, and it's read here if that failed.
            Dictionary<string, Clip> read = loading != null && !loading.IsFaulted ? loading.Result : ReadClips(path);
            lock (sharedClips) { sharedClips[path] = read; clipsLoading.Remove(path); }
            return read;
        }

        private static Dictionary<string, Clip> ReadClips(string path)
        {
            using (var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20)))
            {
                if (r.ReadInt32() != 0x31474f44) throw new InvalidDataException("Unsupported k9dog.dog version");
                int n = Count(r, 512);
                for (int i = 0; i < n; i++) { Text(r); r.BaseStream.Seek(4 + 40, SeekOrigin.Current); }
                int meshCount = Count(r, 64);
                for (int m = 0; m < meshCount; m++)
                {
                    Text(r);
                    int nv = Count(r, 1000000);
                    r.BaseStream.Seek(nv * 64L, SeekOrigin.Current);
                    int subs = Count(r, 64);
                    for (int k = 0; k < subs; k++) { int ni = Count(r, 6000000); r.BaseStream.Seek(ni * 4L, SeekOrigin.Current); }
                }
                var read = new Dictionary<string, Clip>();
                int nc = Count(r, 128);
                for (int c = 0; c < nc; c++)
                {
                    string name = Text(r); int nf = Count(r, 10000); float fps = r.ReadSingle();
                    if (nf < 2 || fps <= 0 || fps > 240) throw new InvalidDataException("Invalid animation timing");
                    var clip = new Clip { Frames = nf, Fps = fps, Poses = new Pose[nf * n] };
                    for (int i = 0; i < clip.Poses.Length; i++) clip.Poses[i] = ReadPose(r);
                    if (name.StartsWith("Loco_")) CloseLoop(clip, n);
                    clip.Loop = nf > 3 && EndsAsItBegins(clip, n);
                    read[name] = clip;
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing dog data");
                return read;
            }
        }

        /// <summary>
        /// A dog of that breed in that coat (1 to breed.Coats), at the given size against its real one (the Scale setting),
        /// grown to 'growth' of its model as made (Growth: young dogs smaller, grown ones a little bigger).
        /// </summary>
        public static DogModel Load(Breed breed, int coat, Transform parent, float scale, float growth = 1)
        {
            var result = new DogModel { Breed = breed ?? Breed.Shepherd, Coat = coat, Growth = growth };
            try { result.Read(result.Breed, coat, parent, scale * growth); return result; }
            catch { result.Dispose(); throw; }
        }

        private void Read(Breed breed, int coat, Transform parent, float scale)
        {
            var visual = new GameObject("K9 dog");
            visual.transform.SetParent(parent, false);
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new InvalidOperationException("No supported lit shader found in the game.");
            Texture2D texture = Shared(breed.CoatFile(coat) + "|" + breed.AlphaFile, () => TextureCache.LoadCoat(breed.CoatFile(coat), breed.AlphaFile));
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.anisoLevel = 8;
            var material = new Material(shader) { name = "K9 coat", mainTexture = texture, color = Color.white };
            resources.Add(material);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.18f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            // The breeds' fur strands (on cards round the body's edge) are cut out of their cards by the coat's alpha. The
            // game's Standard shader has a cut-out variant with a normal map only with specular highlights off (deferred and
            // forward alike), and fur barely has any, so those go.
            if (breed.AlphaFile != null && material.HasProperty("_Cutoff"))
            {
                material.SetFloat("_Mode", 1); material.SetFloat("_Cutoff", .4f);
                material.EnableKeyword("_ALPHATEST_ON"); material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", 0);
                material.SetOverrideTag("RenderType", "TransparentCutout");
                material.renderQueue = 2450;   // the alpha-test queue
            }
            // The breeds come with the fur's relief and its shading in the folds (the shepherd has its colour alone). The
            // game's Standard shader has normal-map variants but none for a metal/gloss map, so fur sheen stays the one
            // value above; the folds' shading (ambient occlusion) needs no variant.
            Texture2D normal = DataTexture(breed.NormalFile, TextureCache.Data.Normal), occlusion = DataTexture(breed.OcclusionFile, TextureCache.Data.Gray);
            if (normal != null && material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP");
            }
            if (occlusion != null && material.HasProperty("_OcclusionMap"))
            {
                material.SetTexture("_OcclusionMap", occlusion);
                if (material.HasProperty("_OcclusionStrength")) material.SetFloat("_OcclusionStrength", 1f);
            }
            using (var r = new BinaryReader(File.OpenRead(breed.DogFile)))
            {
                if (r.ReadInt32() != 0x31474f44) throw new InvalidDataException("Unsupported k9dog.dog version");
                int n = Count(r, 512);
                if (n == 0) throw new InvalidDataException("Dog has no skeleton");
                bones = new Transform[n]; displayed = new Pose[n]; previous = new Pose[n];
                applied = Enumerable.Repeat(new Vector3(float.NaN, float.NaN, float.NaN), n).ToArray();
                offsetTurn = new Vector3[n]; turnRate = new Vector3[n]; offsetSlide = new Vector3[n]; slideRate = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    string name = Text(r); int p = r.ReadInt32();
                    if (p < -1 || p >= i) throw new InvalidDataException("Bone parents must precede children");
                    bones[i] = new GameObject(name).transform;
                    bones[i].SetParent(p < 0 ? visual.transform : bones[p], false);
                    displayed[i] = ReadPose(r); Apply(i, displayed[i]);
                }
                Array.Copy(displayed, previous, n);
                bind = new Matrix4x4[n];
                for (int i = 0; i < n; i++) bind[i] = bones[i].worldToLocalMatrix * visual.transform.localToWorldMatrix;
                root = visual.transform;
                int meshCount = Count(r, 64);
                for (int m = 0; m < meshCount; m++)
                {
                    GameObject body = ReadMesh(r, material, visual.transform, bind);
                    if (normal != null) body.GetComponent<SkinnedMeshRenderer>().sharedMesh.RecalculateTangents();
                }
            }
            clips = ClipsOf(breed.DogFile);
            lookChain = new[] { "RigNeck1", "RigNeck2", "RigNeck3", "RigNeck4", "RigHead" }.Select(Bone).Where(b => b != null).ToArray();
            // The shepherd's five neck and head bones share the turn as LookShare has it; a breed with fewer (one neck bone)
            // shares the whole turn among them, more towards the head.
            lookShare = lookChain.Length == LookShare.Length ? LookShare
                : Enumerable.Range(1, lookChain.Length).Select(i => i / (lookChain.Length * (lookChain.Length + 1) / 2f)).ToArray();
            legBones = Enumerable.Range(0, bones.Length).Where(i => bones[i].name.Contains("Leg")).ToArray();
            foreach (string name in Required)
                if (!clips.ContainsKey(name)) throw new InvalidDataException("Missing animation: " + name);
            visual.transform.localScale = Vector3.one * scale;
            DogSnow.Add(root);
        }

        // One skinned mesh block (as written by tools/convert_blender.py and convert_jacket.py), bound to this skeleton.
        private GameObject ReadMesh(BinaryReader r, Material material, Transform visual, Matrix4x4[] bindposes)
        {
            int n = bones.Length;
            string name = Text(r); int nv = Count(r, 1000000);
            var vertices = new Vector3[nv]; var normals = new Vector3[nv];
            var uv = new Vector2[nv]; var weights = new BoneWeight[nv];
            for (int i = 0; i < nv; i++)
            {
                vertices[i] = V(r); normals[i] = V(r); uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                var w = new BoneWeight
                {
                    boneIndex0 = r.ReadInt32(), boneIndex1 = r.ReadInt32(), boneIndex2 = r.ReadInt32(), boneIndex3 = r.ReadInt32(),
                    weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle()
                };
                if (w.boneIndex0 < 0 || w.boneIndex0 >= n || w.boneIndex1 < 0 || w.boneIndex1 >= n ||
                    w.boneIndex2 < 0 || w.boneIndex2 >= n || w.boneIndex3 < 0 || w.boneIndex3 >= n)
                    throw new InvalidDataException("Bone index outside skeleton");
                weights[i] = w;
            }
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            resources.Add(mesh);
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.boneWeights = weights; mesh.bindposes = bindposes;
            int subs = Count(r, 64); mesh.subMeshCount = subs;
            var materials = new Material[subs];
            for (int s = 0; s < subs; s++)
            {
                int ni = Count(r, 6000000); var indices = new int[ni];
                for (int i = 0; i < ni; i++)
                { indices[i] = r.ReadInt32(); if (indices[i] < 0 || indices[i] >= nv) throw new InvalidDataException("Invalid vertex index"); }
                mesh.SetTriangles(indices, s); materials[s] = material;
            }
            mesh.RecalculateBounds();
            // The body comes first: the box round every pose is made from it, and the gear worn uses the same.
            if (poseBounds.size == Vector3.zero) poseBounds = Around(mesh.bounds);
            var go = new GameObject(name); go.transform.SetParent(visual, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            // The bounds are kept in the model's own space (rootBone = the model, which doesn't animate). They were in the
            // first bone's, which carries the skeleton's centimetre scale (0.01): the "4 m" box was 4 cm, at the new breeds'
            // feet, and the whole dog vanished whenever that spot was off screen or behind something.
            renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = visual; renderer.sharedMaterials = materials;
            renderer.quality = SkinQuality.Bone4; renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            // Its bounds are set wide enough for every pose, so it isn't skinned while nobody can see it.
            renderer.updateWhenOffscreen = false;
            renderer.localBounds = poseBounds;
            renderers.Add(renderer);
            return go;
        }

        // Standing, lying on its side, rolled over dead or at the top of a jump: the body's rest box, grown to hold them all.
        private static Bounds Around(Bounds body)
        {
            // (A standing leap stretches a short-backed dog out far beyond its length: the Rottweiler's hind paw went 18 cm
            // past a box half its height longer than it.)
            float h = Mathf.Max(.2f, body.max.y);
            return new Bounds(new Vector3(0, h * .8f, body.center.z), 2 * new Vector3(h + .1f, h * 1.3f + .1f, body.extents.z + h * .75f + .15f));
        }

        /// <summary>Whether any camera (or a shadow it casts) needs the dog drawn this frame.</summary>
        public bool Seen
        {
            get { foreach (Renderer r in renderers) if (r != null && r.isVisible) return true; return false; }
        }

        // Something the dog wears (jacket.mesh + its texture), skinned to the dog's own bones so it moves with every
        // animation. Null when the files aren't there; the caller then falls back to its own simple version.
        // The model is the one fitted to this dog's breed (in its folder); the texture is the same for every dog.
        public GameObject LoadWearable(string folder, string meshFile, string textureFile)
        {
            string meshPath = Path.Combine(Breed.IsShepherd ? folder : Breed.Folder, meshFile), texturePath = Path.Combine(folder, textureFile);
            if (!File.Exists(meshPath) || !File.Exists(texturePath)) return null;
            Texture2D texture = Shared(texturePath, () => TextureCache.Load(texturePath));
            var material = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")) { name = meshFile, mainTexture = texture, color = Color.white };
            resources.Add(material);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.3f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            using (var r = new BinaryReader(File.OpenRead(meshPath)))
            {
                if (r.ReadInt32() != 0x31544b4a) throw new InvalidDataException("Unsupported " + meshFile + " version");
                GameObject worn = ReadMesh(r, material, root, bind);
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing data in " + meshFile);
                return worn;
            }
        }

        // A map holding data rather than colour (normals, shading): read as it is, without the colour correction.
        private Texture2D DataTexture(string path, TextureCache.Data kind)
        {
            Texture2D texture = Shared(path + "|" + kind, () => TextureCache.LoadData(path, kind));
            if (texture != null) texture.anisoLevel = 4;
            return texture;
        }

        // Only what changed is set: every transform write is work for Unity (the skin's bounds, children), and a bone's
        // scale almost never changes.
        private void Apply(int i, Pose p)
        {
            Transform b = bones[i];
            b.SetLocalPositionAndRotation(p.P, p.Q);
            if (p.S != applied[i]) { b.localScale = p.S; applied[i] = p.S; }
        }

        // Some gait clips end a little away from where they begin (a front leg by 10 degrees in the walk, the tongue by 17 in
        // the sprint), which showed as a jerk every stride. What's missing is shared out over the cycle.
        private static void CloseLoop(Clip clip, int boneCount)
        {
            int last = clip.Frames - 1;
            for (int b = 0; b < boneCount; b++)
            {
                Vector3 miss = PoseMath.Log(PoseMath.Mul(clip.Poses[b].Q, PoseMath.Conjugate(clip.Poses[last * boneCount + b].Q)));
                if (miss.sqrMagnitude < .00008f) continue;   // half a degree
                for (int k = 1; k <= last; k++)
                {
                    int at = k * boneCount + b;
                    clip.Poses[at].Q = PoseMath.Mul(PoseMath.Exp(miss * ((float)k / last)), clip.Poses[at].Q);
                }
            }
        }

        // Every bone ends within a degree and a millimetre (in the skeleton's centimetres: 0.1) of where it began.
        private static bool EndsAsItBegins(Clip clip, int boneCount)
        {
            int last = (clip.Frames - 1) * boneCount;
            for (int b = 0; b < boneCount; b++)
            {
                Pose s = clip.Poses[b], e = clip.Poses[last + b];
                float dot = Math.Abs(s.Q.x * e.Q.x + s.Q.y * e.Q.y + s.Q.z * e.Q.z + s.Q.w * e.Q.w);
                if (dot < .99996f || (s.P - e.P).sqrMagnitude > .01f) return false;
            }
            return true;
        }

        // How long the next change of clip takes to settle.
        public void NextBlend(float seconds) { nextBlend = seconds; }

        private Pose Mixed(Clip clip, float frame, Clip other, float weight, int bone)
        {
            Pose p = Sample(clip, frame, bone);
            if (other == null) return p;
            float along = frame / (clip.Frames - 1);
            return Pose.Blend(p, Sample(other, Mathf.Min(along * (other.Frames - 1), other.Frames - 1.001f), bone), weight);
        }

        private void Begin(string name, string layer, float weight, float start)
        {
            Clip clip = clips[name];
            Clip other = layer != null && weight > 0.001f ? clips[layer] : null;
            float last = clip.Frames - 1;
            float frame = Mathf.Max(0, Mathf.Min(start * clip.Fps, last - 1.001f)), next = Mathf.Min(frame + 1, last - .001f);
            for (int i = 0; i < bones.Length; i++)
            {
                Pose to = Mixed(clip, frame, other, weight, i), then = Mixed(clip, next, other, weight, i);
                Quaternion now = displayed[i].Q;
                offsetTurn[i] = PoseMath.Log(PoseMath.Mul(now, PoseMath.Conjugate(to.Q)));
                offsetSlide[i] = displayed[i].P - to.P;
                // How the pose was moving against how the clip moves: carried over, so the change doesn't jolt the bones.
                Vector3 was = PoseMath.Log(PoseMath.Mul(now, PoseMath.Conjugate(previous[i].Q))) / lastDt;
                Vector3 goes = PoseMath.Log(PoseMath.Mul(then.Q, PoseMath.Conjugate(to.Q))) * clip.Fps;
                turnRate[i] = Vector3.ClampMagnitude(was - goes, MaxTurnRate);
                slideRate[i] = Vector3.ClampMagnitude((displayed[i].P - previous[i].P) / lastDt - (then.P - to.P) * clip.Fps, MaxSlideRate);
            }
            // A critically damped settle is 95% done after 4.74 / rate.
            settleRate = 4.74f / Mathf.Max(nextBlend, .02f); sinceSwitch = 0; settling = true;
            nextBlend = DefaultBlend;
            current = name; time = start; Starts++;
        }

        public bool Tick(string name, float speed, float dt, bool loop = true) => Tick(name, null, 0, speed, dt, loop);

        public bool Tick(string name, string layer, float weight, float speed, float dt, bool loop = true)
        {
            if (current != name) Begin(name, layer, weight, 0);
            Clip clip = clips[current];
            // (A breed's one-off clips at its own pace: Breed.Timing.)
            time += dt * Breed.Timing(current, speed);
            float last = clip.Frames - 1;
            float frame = time * clip.Fps;
            bool done = !loop && frame >= last;
            frame = loop ? frame % last : Mathf.Min(frame, last - 0.001f);
            Progress = frame / last;
            Clip other = layer != null && weight > 0.001f ? clips[layer] : null;
            // Nobody sees it, nor its shadow: the clip runs on, but the bones are left where they are.
            if (SkipUnseen && !Seen)
            {
                if (settling) { sinceSwitch += dt; if (settleRate * sinceSwitch > 7) settling = false; }
                Layer = layer; LayerWeight = weight; Playback = speed; Looping = loop; Walking = false;
                wantYaw = wantPitch = 0;
                return done;
            }
            float keep = 0, keepRate = 0;
            if (settling)
            {
                sinceSwitch += dt;
                float t = settleRate * sinceSwitch, fade = Mathf.Exp(-t);
                keep = (1 + t) * fade; keepRate = sinceSwitch * fade;
                if (t > 7) settling = false;
            }
            Array.Copy(displayed, previous, displayed.Length);
            for (int i = 0; i < bones.Length; i++)
            {
                Pose p = Mixed(clip, frame, other, weight, i);
                if (settling)
                {
                    p.Q = PoseMath.Mul(PoseMath.Exp(offsetTurn[i] * keep + turnRate[i] * keepRate), p.Q);
                    p.P += offsetSlide[i] * keep + slideRate[i] * keepRate;
                }
                displayed[i] = p; Apply(i, p);
            }
            if (dt > 0) lastDt = Mathf.Max(dt, .005f);
            Layer = layer; LayerWeight = weight; Playback = speed; Looping = loop; Walking = false;
            TurnHead(dt);
            return done;
        }

        // A gait: it takes up from the frame of its cycle where the legs are closest to what they're doing now, rather than
        // the start of the clip with the legs wherever that has them.
        public void TickGait(string name, string lean, float weight, float speed, float dt)
        {
            if (current != name)
            {
                Clip clip = clips[name];
                NextBlend(.24f);
                Begin(name, lean, weight, ClosestFrame(clip) / clip.Fps);
            }
            Tick(name, lean, weight, speed, dt);
            Walking = true;
        }

        private int ClosestFrame(Clip clip)
        {
            int best = 0, count = bones.Length;
            float least = float.MaxValue;
            for (int k = 0; k < clip.Frames - 1; k++)
            {
                float apart = 0;
                foreach (int b in legBones)
                {
                    Quaternion a = clip.Poses[k * count + b].Q, d = displayed[b].Q;
                    float dot = a.x * d.x + a.y * d.y + a.z * d.z + a.w * d.w;
                    apart += 1 - dot * dot;
                }
                if (apart < least) { least = apart; best = k; }
            }
            return best;
        }

        // Where the head and neck turn this frame, in degrees from straight ahead (yaw to the right, pitch down), on top
        // of whatever's playing. Eased, and let go by not asking.
        public void LookAt(float yaw, float pitch) { wantYaw = Mathf.Clamp(yaw, -70, 70); wantPitch = Mathf.Clamp(pitch, -30, 45); }

        private void TurnHead(float dt)
        {
            float ease = 1 - Mathf.Exp(-9 * dt);
            lookYaw += (wantYaw - lookYaw) * ease; lookPitch += (wantPitch - lookPitch) * ease;
            wantYaw = wantPitch = 0;
            if (Mathf.Abs(lookYaw) + Mathf.Abs(lookPitch) < .2f) return;
            Vector3 up = root.up, right = root.right;
            for (int i = 0; i < lookChain.Length; i++)
                lookChain[i].rotation = Quaternion.AngleAxis(lookYaw * lookShare[i], up) * Quaternion.AngleAxis(lookPitch * lookShare[i], right) * lookChain[i].rotation;
        }

        public void Restart() { current = null; }

        // Plays part of a clip (from/to as 0-1 progress), starting at 'from' whenever restart is set or another clip was
        // playing. Returns true once 'to' is reached.
        public bool TickSegment(string name, float from, float to, float speed, float dt, bool restart)
        {
            if (restart || current != name)
            {
                Clip clip = clips[name];
                Begin(name, null, 0, from * (clip.Frames - 1) / clip.Fps);
            }
            Tick(name, speed, dt, false);
            return Progress >= to;
        }

        // Where in a clip (0-1) a bone snaps back the fastest; for the bite clips that is the jaw closing on its target.
        public float SnapProgress(string clipName, string boneName)
        {
            Clip clip = clips[clipName];
            int bone = Array.FindIndex(bones, b => b.name == boneName);
            if (bone < 0) return .5f;
            int at = clip.Frames / 2; float biggest = 0;
            for (int k = 1; k < clip.Frames; k++)
            {
                float closing = Quaternion.Angle(Quaternion.identity, clip.Poses[(k - 1) * bones.Length + bone].Q)
                              - Quaternion.Angle(Quaternion.identity, clip.Poses[k * bones.Length + bone].Q);
                if (closing > biggest) { biggest = closing; at = k; }
            }
            return at / (clip.Frames - 1f);
        }

        public float Progress { get; private set; }

        // Between two of the clip's frames along a smooth curve through the frames either side (Catmull-Rom), rather than in
        // a straight line from one to the next: the clips are sampled at 30 a second, and a fast leg swing drawn straight
        // between them turned sharply at every frame. A loop's frames run on round its end.
        private Pose Sample(Clip clip, float frame, int bone)
        {
            int n = bones.Length, last = clip.Frames - 1;
            int i1 = (int)frame; float t = frame - i1;
            if (i1 >= last) { i1 = last - 1; t = 1; }
            if (i1 < 0) { i1 = 0; t = 0; }
            int i2 = i1 + 1;
            int i0 = i1 > 0 ? i1 - 1 : clip.Loop ? last - 1 : 0;
            int i3 = i2 < last ? i2 + 1 : clip.Loop ? 1 : last;
            Pose p0 = clip.Poses[i0 * n + bone], p1 = clip.Poses[i1 * n + bone], p2 = clip.Poses[i2 * n + bone], p3 = clip.Poses[i3 * n + bone];
            float t2 = t * t, t3 = t2 * t;
            float a = -.5f * t3 + t2 - .5f * t, b = 1.5f * t3 - 2.5f * t2 + 1, c = -1.5f * t3 + 2 * t2 + .5f * t, d = .5f * t3 - .5f * t2;
            Quaternion q0 = Near(p0.Q, p1.Q), q2 = Near(p2.Q, p1.Q), q3 = Near(p3.Q, q2), q1 = p1.Q;
            var q = new Quaternion(a * q0.x + b * q1.x + c * q2.x + d * q3.x, a * q0.y + b * q1.y + c * q2.y + d * q3.y,
                                   a * q0.z + b * q1.z + c * q2.z + d * q3.z, a * q0.w + b * q1.w + c * q2.w + d * q3.w);
            float len = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (len < 1e-6f) q = Quaternion.Slerp(p1.Q, p2.Q, t);
            else { float k = 1 / len; q.x *= k; q.y *= k; q.z *= k; q.w *= k; }
            return new Pose { P = a * p0.P + b * p1.P + c * p2.P + d * p3.P, Q = q, S = Vector3.LerpUnclamped(p1.S, p2.S, t) };
        }

        // The same rotation as q, on the same side of the sphere as 'like' (so the curve doesn't swing the long way round).
        private static Quaternion Near(Quaternion q, Quaternion like)
            => q.x * like.x + q.y * like.y + q.z * like.z + q.w * like.w < 0 ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;

        // Pictures shared by every dog showing them (the hideout's dog, the DOG tab's and the raid screen's pictures of it, the
        // raid's dog made ready while loading): one copy on the graphics card, freed with the last dog using it.
        private static readonly Dictionary<string, (Texture2D Texture, int Users)> sharedTextures = new Dictionary<string, (Texture2D, int)>();
        private readonly List<string> sharedUsed = new List<string>();

        private Texture2D Shared(string key, Func<Texture2D> load)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (sharedTextures.TryGetValue(key, out var known) && known.Texture != null)
                sharedTextures[key] = (known.Texture, known.Users + 1);
            else
            {
                Texture2D made = load();
                if (made == null) return null;
                sharedTextures[key] = known = (made, 1);
            }
            sharedUsed.Add(key);
            return sharedTextures[key].Texture;
        }

        public void Dispose()
        {
            DogSnow.Remove(root);
            foreach (var resource in resources) if (resource != null) UnityEngine.Object.Destroy(resource);
            resources.Clear();
            foreach (string key in sharedUsed)
            {
                if (!sharedTextures.TryGetValue(key, out var known)) continue;
                if (known.Users > 1) sharedTextures[key] = (known.Texture, known.Users - 1);
                else { sharedTextures.Remove(key); if (known.Texture != null) UnityEngine.Object.Destroy(known.Texture); }
            }
            sharedUsed.Clear();
        }
    }
}
