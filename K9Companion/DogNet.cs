using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>What another player's game needs to show a dog right now. Sent often; losing one doesn't matter.</summary>
    internal struct DogState
    {
        public int Owner;
        public Vector3 Position;
        public float Yaw, Pitch, LookYaw, LookPitch, LayerWeight, Playback;
        public short Clip, Layer;
        public byte Starts;
        public bool Looping, Walking, Low, Resting, Targetable, Dead;
    }

    /// <summary>What the dog wears. Sent now and then, so someone joining late gets it too.</summary>
    internal struct DogLooks
    {
        public int Owner;
        public string Vest, Head, Collar;
        public bool Camera;
        public float Scale;
    }

    internal enum DogNote : byte { Sound, Gone, Bitten, Spotted, AskHostile, Hostile, NotHostile }

    /// <summary>The Fika connection (K9Companion.Fika.dll). Only ever there when Fika is installed.</summary>
    internal interface IDogLink
    {
        bool Online { get; }
        bool Headless { get; }
        // More than one person in this raid (a headless host doesn't count).
        bool InGroup { get; }
        // A bot being run by the host, seen here as Fika's copy of it: no brain on this machine.
        bool IsRemoteBot(Player player);
        int NetIdOf(Player player);
        Player PlayerOf(int netId);
        void Send(DogState state);
        void Send(DogLooks looks);
        void Send(DogNote note, int owner, string text, float amount, Vector3 at);
    }

    /// <summary>
    /// The dog under Fika. Each player's game runs that player's own dog; everyone else gets a copy (DogReplica) that bots on
    /// the host can see and aim at. Bullets are worked out on every machine, so a bot's shot really lands on the owner's
    /// machine, on the real dog. A bite on a bot goes through Fika's own damage messages, as your own shots do, and this
    /// only adds the stagger, the question of which side a bot is on (only the host knows) and the dog being spotted.
    /// None of this runs without Fika: the link is a separate assembly, loaded only if Fika is there.
    /// </summary>
    internal static class DogNet
    {
        private const string FikaId = "com.fika.core", LinkFile = "K9Companion.Fika.dll", LinkType = "K9Companion.Fika.FikaLink";
        private const float AskEvery = 2f, Forget = 8f;

        private struct Standing { public bool Hostile; public float Asked, Answered; }

        private static IDogLink link;
        private static readonly Dictionary<string, Standing> standings = new Dictionary<string, Standing>();
        private static readonly Dictionary<int, DogReplica> replicas = new Dictionary<int, DogReplica>();

        internal static bool Installed => Chainloader.PluginInfos.ContainsKey(FikaId);
        internal static bool Online => link != null && link.Online;
        internal static bool Headless => link != null && link.Headless;

        internal static bool InGroup
        {
            get
            {
                if (link == null) return false;
                try { return link.InGroup; }
                catch (Exception) { return false; }
            }
        }

        internal static void Load()
        {
            if (!Installed) return;
            try
            {
                string path = Path.Combine(Plugin.AssetFolder, LinkFile);
                if (!File.Exists(path)) { Plugin.Log.LogWarning("Fika is installed but " + LinkFile + " isn't: other players won't see the dog, nor it their bots."); return; }
                // It refers back to this plugin and to Fika, both already loaded (asked for as it first uses each one).
                AppDomain.CurrentDomain.AssemblyResolve += AlreadyLoaded;
                link = (IDogLink)Activator.CreateInstance(Assembly.LoadFrom(path).GetType(LinkType, true), true);
                Plugin.Log.LogInfo("Fika found: dogs are shared with the other players in the raid.");
            }
            catch (Exception e) { Plugin.Log.LogError("The Fika link couldn't be started (the dog still works, on your machine only): " + e); link = null; }
        }

        private static Assembly AlreadyLoaded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }

        internal static bool IsRemoteBot(Player player) => link != null && player != null && link.IsRemoteBot(player);

        // Which side a host's bot is on, from the host. Until it answers (a moment), the bot is left alone.
        internal static bool HostileBot(Player bot, Player owner)
        {
            if (!Online) return false;
            string id = bot.ProfileId;
            standings.TryGetValue(id, out Standing s);
            float now = Time.time;
            if (now - s.Answered > Forget && now - s.Asked > AskEvery)
            {
                s.Asked = now;
                standings[id] = s;
                Guard(() => link.Send(DogNote.AskHostile, link.NetIdOf(owner), id, 0, default));
            }
            return s.Hostile;
        }

        internal static void Sound(string name)
        {
            if (Online && Companion.Raid?.Owner != null) Guard(() => link.Send(DogNote.Sound, link.NetIdOf(Companion.Raid.Owner), name, 0, default));
        }

        internal static void Bitten(Player bot, float stagger, Vector3 from)
        {
            if (Online && Companion.Raid?.Owner != null) Guard(() => link.Send(DogNote.Bitten, link.NetIdOf(Companion.Raid.Owner), bot.ProfileId, stagger, from));
        }

        internal static void DogGone()
        {
            if (Online && Companion.Raid?.Owner != null) Guard(() => link.Send(DogNote.Gone, link.NetIdOf(Companion.Raid.Owner), null, 0, default));
        }

        internal static void Spotted(int owner, string by)
        {
            if (Online) Guard(() => link.Send(DogNote.Spotted, owner, by, 0, default));
        }

        internal static void SendState(DogState state)
        {
            state.Owner = link.NetIdOf(Companion.Raid.Owner);
            Guard(() => link.Send(state));
        }

        internal static void SendLooks(DogLooks looks)
        {
            looks.Owner = link.NetIdOf(Companion.Raid.Owner);
            Guard(() => link.Send(looks));
        }

        internal static Player PlayerOf(int netId) => link?.PlayerOf(netId);

        // ---- from the network ------------------------------------------------------------------------------------------

        internal static void Received(DogState state)
        {
            if (state.Owner == MyNetId()) return;
            if (!replicas.TryGetValue(state.Owner, out DogReplica copy) || copy == null)
            {
                copy = DogReplica.Create(state.Owner, link != null && link.Headless);
                if (copy == null) return;
                replicas[state.Owner] = copy;
            }
            copy.Received(state);
        }

        internal static void Received(DogLooks looks)
        {
            if (replicas.TryGetValue(looks.Owner, out DogReplica copy) && copy != null) copy.Dress(looks);
        }

        internal static void Received(DogNote note, int owner, string text, float amount, Vector3 at)
        {
            switch (note)
            {
                case DogNote.Sound:
                    if (replicas.TryGetValue(owner, out DogReplica barking) && barking != null) barking.Play(text);
                    break;
                case DogNote.Gone:
                    if (replicas.TryGetValue(owner, out DogReplica gone) && gone != null) UnityEngine.Object.Destroy(gone.gameObject);
                    replicas.Remove(owner);
                    break;
                case DogNote.Spotted:
                    if (owner == MyNetId()) Companion.Raid?.Noticed(null);
                    break;
                case DogNote.Hostile:
                case DogNote.NotHostile:
                    standings[text] = new Standing { Hostile = note == DogNote.Hostile, Asked = Time.time, Answered = Time.time };
                    break;
                case DogNote.Bitten:
                    // Here on the host: the bot is real.
                    BotOwner bitten = World?.GetAlivePlayerByProfileID(text)?.AIData?.BotOwner;
                    if (bitten != null)
                    {
                        replicas.TryGetValue(owner, out DogReplica biter);
                        DogThreat.Staggered(bitten, amount, at, biter);
                    }
                    break;
            }
        }

        // Asked by a client, about one of the bots here on the host.
        internal static bool Answer(int owner, string botId)
        {
            Player bot = World?.GetAlivePlayerByProfileID(botId);
            BotOwner brain = bot?.AIData?.BotOwner;
            Player asking = link?.PlayerOf(owner);
            if (brain == null || asking == null) return false;
            return !brain.IsDead && brain.BotState == EBotState.Active && !ModCompat.Dormant(botId) && DogThreat.HostileTo(brain, asking);
        }

        internal static void RaidOver()
        {
            foreach (DogReplica copy in replicas.Values) if (copy != null) UnityEngine.Object.Destroy(copy.gameObject);
            replicas.Clear(); standings.Clear();
            DogThreat.RaidOver();
        }

        private static GameWorld World => Comfort.Common.Singleton<GameWorld>.Instantiated ? Comfort.Common.Singleton<GameWorld>.Instance : null;

        private static int MyNetId()
        {
            Player me = World?.MainPlayer;
            return me != null && link != null ? link.NetIdOf(me) : -1;
        }

        // A network problem must never reach the dog.
        private static void Guard(Action send)
        {
            try { send(); }
            catch (Exception e) { Plugin.Log.LogWarning("Dog sync: " + e.Message); }
        }
    }

    /// <summary>
    /// Another player's dog as this machine shows it: played from what the owner's game sends, a moment behind so the
    /// movement is smooth. Bots here can see and shoot at it (it has no hitbox here; the real one, on its owner's machine,
    /// is what the bullets hit). On a headless host there's no one to show it to, so it's only a position.
    /// </summary>
    internal sealed class DogReplica : MonoBehaviour, IDogTarget
    {
        private const float Behind = .15f, TimeOut = 6f;

        private struct Snapshot { public DogState State; public float At; }

        private readonly Snapshot[] history = new Snapshot[8];
        private int newest = -1;
        private DogModel model;
        private CompanionDecor decor;
        private CompanionAudio sound;
        private Transform pelvis, chest, head;
        private DogState state;
        private int starts = -1, ownerNet;
        private bool hasState;
        private Player owner;

        internal static DogReplica Create(int ownerNet, bool headless)
        {
            try
            {
                var go = new GameObject("K9 (another player's)");
                var copy = go.AddComponent<DogReplica>();
                copy.ownerNet = ownerNet;
                if (!headless)
                {
                    copy.model = DogModel.Load(Plugin.AssetFolder, go.transform, Plugin.Scale.Value);
                    copy.decor = new CompanionDecor(go.transform, copy.model);
                    copy.sound = new CompanionAudio(go);
                    copy.pelvis = copy.model.Bone("RigPelvis"); copy.chest = copy.model.Bone("RigChest"); copy.head = copy.model.Bone("RigHead");
                }
                DogThreat.Targets.Add(copy);
                Plugin.Log.LogInfo("Another player's dog has arrived (player " + ownerNet + ").");
                return copy;
            }
            catch (Exception e) { Plugin.Log.LogError("Another player's dog couldn't be shown: " + e); return null; }
        }

        internal void Received(DogState received)
        {
            newest = (newest + 1) % history.Length;
            history[newest] = new Snapshot { State = received, At = Time.time };
            state = received; hasState = true;
        }

        internal void Dress(DogLooks looks)
        {
            if (decor == null) return;
            decor.SetVest(looks.Vest); decor.SetHead(looks.Head); decor.SetCamera(looks.Camera); decor.SetCosmetics(looks.Collar);
            if (looks.Scale > 0) model.Root.localScale = Vector3.one * looks.Scale;
        }

        internal void Play(string name) { sound?.Play(name); }

        private void Update()
        {
            if (!hasState) return;
            if (Time.time - history[newest].At > TimeOut) { Destroy(gameObject); return; }
            MoveAlong();
            if (model == null) return;
            string clip = DogModel.ClipName(state.Clip);
            if (clip == null) return;
            if (state.Starts != starts && !state.Walking) model.Restart();
            starts = state.Starts;
            model.Root.localRotation = Quaternion.Euler(state.Pitch, 0, 0);
            model.LookAt(state.LookYaw, state.LookPitch);
            string layer = DogModel.ClipName(state.Layer);
            if (state.Walking) model.TickGait(clip, layer, state.LayerWeight, state.Playback, Time.deltaTime);
            else model.Tick(clip, layer, state.LayerWeight, state.Playback, Time.deltaTime, state.Looping);
            decor?.Update();
            if (!state.Dead) sound?.Breathe(.15f, .95f); else sound?.Breathe(0, 1);
        }

        // Between the two messages either side of a moment ago; past the last one, held there.
        private void MoveAlong()
        {
            float showing = Time.time - Behind;
            Snapshot later = history[newest], earlier = later;
            for (int k = 1; k < history.Length; k++)
            {
                Snapshot s = history[(newest - k + history.Length) % history.Length];
                if (s.At <= 0) break;
                earlier = s;
                if (s.At <= showing) break;
                later = s;
            }
            float span = later.At - earlier.At;
            float t = span > .001f ? Mathf.Clamp01((showing - earlier.At) / span) : 1;
            transform.SetPositionAndRotation(Vector3.Lerp(earlier.State.Position, later.State.Position, t),
                Quaternion.Euler(0, Mathf.LerpAngle(earlier.State.Yaw, later.State.Yaw, t), 0));
        }

        private void OnDestroy()
        {
            DogThreat.Targets.Remove(this);
            sound?.Dispose(); decor?.Dispose(); model?.Dispose();
        }

        public Vector3 AimPoint => pelvis != null ? (pelvis.position + chest.position) * .5f : transform.position + Vector3.up * .5f;
        public Vector3 HeadPoint => head != null ? head.position : transform.position + Vector3.up * .65f + transform.forward * .6f;
        public Vector3 TrackedAimPoint => AimPoint;
        public bool Resting => state.Resting;
        public bool Low => state.Low;
        public bool Targetable => hasState && state.Targetable && !state.Dead;
        public Player Owner => owner != null ? owner : owner = DogNet.PlayerOf(ownerNet);
        public void Noticed(BotOwner by) => DogNet.Spotted(ownerNet, by?.ProfileId);
    }

    /// <summary>
    /// The map's navmesh for a Fika client. Every map keeps its navmesh in its "_AI" scene (custom_AI, woods_AI, ...),
    /// the one scene the game marks offline-only; Fika makes a client skip it (only the host runs bots), which left the
    /// dog nowhere to stand: "No nearby navigation surface". When your dog is coming along, that scene loads as it does
    /// in single player and on the host. Without a dog, Fika's own choice stands.
    /// </summary>
    internal static class DogNavMesh
    {
        internal static void Patch(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetFromConfigAsync))
                         ?? throw new MissingMethodException(nameof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetFromConfigAsync));
            // Last: Fika's prefix sets DisableServerScenes for a client, so this has to come after it.
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DogNavMesh), nameof(BeforeScenes)) { priority = Priority.Last });
        }

        private static void BeforeScenes(ref ScenePresetLoadConfig preset)
        {
            try
            {
                if (!preset.DisableServerScenes || !DogNet.Installed || DogNet.Headless) return;
                // A Fika client loads the map before the raid-start hook (DogBag.BeforeRaid) decides, so decide the same way here.
                bool coming = DogBag.Deployed ?? (Plugin.Active.Value && K9.NotDeployable() == null);
                if (!coming) return;
                preset.DisableServerScenes = false;
                Plugin.Log.LogInfo("Loading the map's AI scene too: it holds the navmesh the dog walks on (Fika skips it on clients).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't keep the map's navmesh for the dog: " + e.Message); }
        }
    }
}
