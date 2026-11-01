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
        private sealed class Clip { public int Frames; public float Fps; public Pose[] Poses; }

        // A clip's number on the wire (DogNet): every copy of the mod has the same list.
        public static int ClipNumber(string name) => name == null ? -1 : Array.IndexOf(Required, name);
        public static string ClipName(int number) => number >= 0 && number < Required.Length ? Required[number] : null;

        public static readonly string[] Required = { "Stand_00", "Loco_Walk", "Loco_Trot", "Loco_Run", "Loco_Sprint", "Sitting_00", "Trans_Stand_To_Sitting", "Trans_Sitting_To_Stand", "Loco_Walk_L", "Loco_Walk_R", "Loco_Trot_L", "Loco_Trot_R", "Loco_Run_L", "Loco_Run_R", "Loco_Sprint_L", "Loco_Sprint_R", "Trans_TurnL90", "Trans_TurnR90", "Loco_Sneak", "Loco_Sneak_L", "Loco_Sneak_R", "Stand_Sneak_00", "Loco_WalkSlow", "Loco_WalkSlow_L", "Loco_WalkSlow_R", "Stand_01", "Stand_02", "Sitting_01", "Sitting_02", "Lying_00", "Lying_01", "Lying_02", "Sleeping_01", "Trans_Stand_To_Lying", "Trans_Lying_To_Stand", "Trans_Lying_to_Sleeping", "Trans_Sleeping_to_Lying", "Death_Stand_L", "Death_Stand_R", "Death_Stand_L_Pose", "Death_Stand_R_Pose", "Hit_ChestL_Light", "Hit_ChestR_Light", "Hit_ChestL_Heavy", "Hit_ChestR_Heavy", "Attack_BiteL", "Attack_BiteR", "Agressive_01" };

        private Transform[] bones;
        private Transform root;
        private Matrix4x4[] bind;
        private Pose[] displayed, previous;
        // Clip data never changes, so every dog (raid, hideout, the raid screen's, other players') shares one copy.
        private static Dictionary<string, Clip> sharedClips;
        private static Task<Dictionary<string, Clip>> clipsLoading;
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
        private float lookYaw, lookPitch, wantYaw, wantPitch;

        public string Current => current;
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

        // Reads the animations in the background when the game starts: plain data, a lot of it, that needs nothing of Unity's.
        internal static void WarmUp(string folder)
        {
            if (sharedClips == null && clipsLoading == null) clipsLoading = Task.Run(() => ReadClips(Path.Combine(folder, "k9dog.dog")));
        }

        internal static bool ClipsReady => clipsLoading == null || clipsLoading.IsCompleted;

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
                    read[name] = clip;
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing dog data");
                return read;
            }
        }

        public static DogModel Load(string folder, Transform parent, float scale)
        {
            var result = new DogModel();
            try { result.Read(folder, parent, scale); return result; }
            catch { result.Dispose(); throw; }
        }

        private void Read(string folder, Transform parent, float scale)
        {
            var visual = new GameObject("K9 dog");
            visual.transform.SetParent(parent, false);
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new InvalidOperationException("No supported lit shader found in the game.");
            Texture2D texture = TextureCache.Load(Path.Combine(folder, "k9dog.png"));
            resources.Add(texture);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.anisoLevel = 4;
            var material = new Material(shader) { name = "K9 coat", mainTexture = texture, color = Color.white };
            resources.Add(material);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.18f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            using (var r = new BinaryReader(File.OpenRead(Path.Combine(folder, "k9dog.dog"))))
            {
                if (r.ReadInt32() != 0x31474f44) throw new InvalidDataException("Unsupported k9dog.dog version");
                int n = Count(r, 512);
                if (n == 0) throw new InvalidDataException("Dog has no skeleton");
                bones = new Transform[n]; displayed = new Pose[n]; previous = new Pose[n];
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
                for (int m = 0; m < meshCount; m++) ReadMesh(r, material, visual.transform, bind);
            }
            // Ready by now, normally: if not, this waits for the background read, and it's read here if that failed.
            if (sharedClips == null)
            {
                if (clipsLoading != null && !clipsLoading.IsFaulted) sharedClips = clipsLoading.Result;
                else sharedClips = ReadClips(Path.Combine(folder, "k9dog.dog"));
            }
            clips = sharedClips;
            lookChain = new[] { "RigNeck1", "RigNeck2", "RigNeck3", "RigNeck4", "RigHead" }.Select(Bone).Where(b => b != null).ToArray();
            legBones = Enumerable.Range(0, bones.Length).Where(i => bones[i].name.Contains("Leg")).ToArray();
            foreach (string name in Required)
                if (!clips.ContainsKey(name)) throw new InvalidDataException("Missing animation: " + name);
            visual.transform.localScale = Vector3.one * scale;
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
            var go = new GameObject(name); go.transform.SetParent(visual, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0]; renderer.sharedMaterials = materials;
            renderer.quality = SkinQuality.Bone4; renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            // Its bounds are set wide enough for every pose, so it isn't skinned while nobody can see it.
            renderer.updateWhenOffscreen = false;
            renderer.localBounds = new Bounds(new Vector3(0, 0.75f, 0), new Vector3(4, 4, 4));
            return go;
        }

        // Something the dog wears (jacket.mesh + its texture), skinned to the dog's own bones so it moves with every
        // animation. Null when the files aren't there; the caller then falls back to its own simple version.
        public GameObject LoadWearable(string folder, string meshFile, string textureFile)
        {
            string meshPath = Path.Combine(folder, meshFile), texturePath = Path.Combine(folder, textureFile);
            if (!File.Exists(meshPath) || !File.Exists(texturePath)) return null;
            Texture2D texture = TextureCache.Load(texturePath);
            resources.Add(texture);
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

        private void Apply(int i, Pose p)
        { bones[i].localPosition = p.P; bones[i].localRotation = p.Q; bones[i].localScale = p.S; }

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
            time += dt * speed;
            float last = clip.Frames - 1;
            float frame = time * clip.Fps;
            bool done = !loop && frame >= last;
            frame = loop ? frame % last : Mathf.Min(frame, last - 0.001f);
            Progress = frame / last;
            Clip other = layer != null && weight > 0.001f ? clips[layer] : null;
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
                lookChain[i].rotation = Quaternion.AngleAxis(lookYaw * LookShare[i], up) * Quaternion.AngleAxis(lookPitch * LookShare[i], right) * lookChain[i].rotation;
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

        private Pose Sample(Clip clip, float frame, int bone)
        {
            int first = (int)frame;
            return Pose.Blend(clip.Poses[first * bones.Length + bone], clip.Poses[(first + 1) * bones.Length + bone], frame - first);
        }

        public void Dispose()
        { foreach (var resource in resources) if (resource != null) UnityEngine.Object.Destroy(resource); resources.Clear(); }
    }
}
