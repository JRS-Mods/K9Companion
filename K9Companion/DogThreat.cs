using System;
using System.Collections.Generic;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The game's AI only registers real Players as enemies (EnemyInfo needs a human skeleton, profile and inventory),
    /// so the dog is handled by one extra layer in each bot's own brain. The layer uses the bot's sight settings to notice
    /// the dog and hands the shooting to the bot's own aiming and trigger code. It steps aside whenever the bot has a
    /// visible human enemy, so normal combat always comes first.
    /// </summary>
    internal static class DogThreat
    {
        // Bots currently shooting at the dog; the dog's defensive mode treats them as threats.
        internal static readonly HashSet<BotOwner> Engagers = new HashSet<BotOwner>();
        // Each bot's dog layer, so a bitten bot can be told where the dog is.
        internal static readonly Dictionary<BotOwner, DogThreatLayer> Layers = new Dictionary<BotOwner, DogThreatLayer>();
        // The dogs bots can see: your own, and under Fika the other players' (DogReplica).
        internal static readonly List<IDogTarget> Targets = new List<IDogTarget>();

        // The bite throws the bot off balance through the game's own "flashed" reaction: it loses its aim on its target, its
        // accuracy drops for a moment and its danger layer takes over, so it can't simply shoot the dog. It also knows
        // exactly where the dog is now, and turns on it. Not a bot the bite just killed (its brain is gone, and the reaction
        // reads the gun it no longer holds); a reaction the game can't play costs the stagger, never the dog.
        internal static void Staggered(BotOwner bot, float stagger, Vector3 from, IDogTarget dog)
        {
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active) return;
            if (stagger > 0 && bot.WeaponManager?.CurrentWeapon != null)
            {
                try { bot.FlashGrenade?.AddBlindEffect(stagger, from); }
                catch (Exception e) { Plugin.Log.LogWarning("The bite's stagger: " + e.Message); }
            }
            if (Layers.TryGetValue(bot, out DogThreatLayer layer)) layer.Provoke(stagger, dog);
        }

        internal static void RaidOver() { Engagers.Clear(); Layers.Clear(); }

        // Same relationship the bot's group has with the dog's owner: scavs/raiders/rogues/bosses hostile to you are hostile to it.
        internal static bool HostileTo(BotOwner bot, Player owner)
        {
            BotsGroup group = bot.BotsGroup;
            return owner != null && group != null && !group.IsAlly(owner) && (group.IsEnemy(owner) || group.IsPlayerEnemy(owner));
        }

        // Ids/values that no game strategy or decision uses.
        internal const int LayerId = 9161;
        internal const BotLogicDecision Decision = (BotLogicDecision)9161;
        // Above the fight/boss layers (50-60), below fight requests, warnings, malfunctions and danger avoidance (65+).
        private const int Priority = 62;

        public static void Patch(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(StandartBotBrain), nameof(StandartBotBrain.Activate))
                         ?? throw new MissingMethodException(nameof(StandartBotBrain), nameof(StandartBotBrain.Activate));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DogThreat), nameof(BrainActivated)));
        }

        // Every shot anyone fires goes through here, on this machine: bots', yours and, under Fika, other players' and the
        // host's bots' as they're played out here. With its real line, so a near miss counts.
        public static void PatchGunfire(Harmony harmony)
        {
            var shot = AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot))
                       ?? throw new MissingMethodException(nameof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot));
            harmony.Patch(shot, postfix: new HarmonyMethod(typeof(DogThreat), nameof(ShotFired)));
        }

        private static bool shotFailed;
        private static void ShotFired(Player.FirearmController __instance, Vector3 shotPosition, Vector3 shotDirection)
        {
            if (shotFailed) return;
            try { Companion.Raid?.ShotFired(__instance._player, shotPosition, shotDirection); }
            catch (Exception e) { shotFailed = true; Plugin.Log.LogWarning("The dog won't react to gunfire this session: " + e); }
        }

        private static void BrainActivated(StandartBotBrain __instance)
        {
            try
            {
                BotOwner bot = __instance._owner;
                if (bot == null || __instance.BaseBrain == null || __instance.Agent == null) return;
                var layer = new DogThreatLayer(bot, Priority);
                if (__instance.BaseBrain.TryAddLayer(LayerId, layer, true))
                {
                    __instance.Agent._nodesDictionary[Decision] = new DogEngageNode(bot, layer);
                    Layers[bot] = layer;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not teach a bot about the dog: " + e.Message); }
        }
    }

    /// <summary>A dog as the bots see it, wherever it's really being run.</summary>
    internal interface IDogTarget
    {
        Vector3 AimPoint { get; }
        Vector3 HeadPoint { get; }
        Vector3 TrackedAimPoint { get; }
        bool Resting { get; }
        bool Low { get; }
        bool Targetable { get; }
        Player Owner { get; }
        void Noticed(BotOwner by);
    }

    internal sealed class DogThreatLayer : BaseLogicLayer
    {
        private float awareness, lastLook, lastSeenTime, nextLook, nextRelation, nextChoice, engagedSince, restUntil, bittenUntil;
        private bool hostile, wasActive;
        // The dog it's looking out for: the nearest, once a second, and it stays with one it's already fighting.
        internal IDogTarget Target { get; private set; }

        private bool engaged;
        public bool Engaged
        {
            get { return engaged; }
            private set
            {
                if (engaged == value) return;
                engaged = value;
                if (value) { DogThreat.Engagers.Add(_owner); Target?.Noticed(_owner); } else DogThreat.Engagers.Remove(_owner);
            }
        }
        public bool Visible { get; private set; }
        public bool Bitten => Time.time < bittenUntil;
        public Vector3 LastSeen { get; private set; }

        public DogThreatLayer(BotOwner bot, int priority) : base(bot, priority) { }

        public override string Name() => "K9DogThreat";

        public override AICoreActionResult<BotLogicDecision, CoreActionResultParams> GetDecision()
            => new AICoreActionResult<BotLogicDecision, CoreActionResultParams>(DogThreat.Decision, "Dog");

        // Any decision left over from another layer ends at once; ours runs until ShallUseNow lets go.
        public override AICoreActionEnd ShallEndCurrentDecision(AICoreActionResult<BotLogicDecision, CoreActionResultParams> curDecision)
            => new AICoreActionEnd("Dog", curDecision.Action != DogThreat.Decision);

        public override bool ShallUseNow()
        {
            bool use = false;
            try { use = Evaluate(); }
            catch (Exception e) { Plugin.Log.LogWarning("Dog threat check failed: " + e.Message); Forget(); }
            if (wasActive && !use)
            {
                _owner.ShootData?.EndShoot();
                _owner.AimingManager?.CurrentAiming?.LoseTarget();
            }
            wasActive = use;
            return use;
        }

        private bool Evaluate()
        {
            float now = Time.time;
            IDogTarget dog = Choose(now);
            if (dog == null || !dog.Targetable || _owner.IsDead || _owner.BotState != EBotState.Active || !IsHostile(dog.Owner))
            { Forget(); return false; }

            if (now >= nextLook)
            {
                // Sight checks are staggered like the game's own look sensor; noticing takes time, longer when far or off to the side.
                float since = Mathf.Min(now - lastLook, .5f);
                lastLook = now;
                nextLook = now + UnityEngine.Random.Range(.2f, .35f);
                Visible = CanSee(dog, out float reaction);
                if (Visible) { LastSeen = dog.AimPoint; lastSeenTime = now; awareness += since / reaction; }
                else if (!Engaged) awareness = Mathf.Max(0, awareness - since * .3f);
            }

            if (!Engaged && awareness >= 1 && now >= restUntil) { Engaged = true; engagedSince = now; }
            if (Engaged)
            {
                // Out of sight for a while, or a long fruitless exchange: back to normal duty, still half aware of the dog.
                bool lost = now - lastSeenTime > 4f;
                if (lost || now - engagedSince > 25f) { Engaged = false; awareness = .5f; restUntil = now + (lost ? 0 : 6f); }
            }

            // Just bitten, the dog comes first, even with you in sight: it's on the bot's leg.
            bool bitten = now < bittenUntil;
            if (bitten) { LastSeen = dog.AimPoint; return Engaged; }
            EnemyInfo goal = _owner.Memory?.GoalEnemy;
            if (goal != null && goal.IsVisible) return false;
            return Engaged && now - lastSeenTime < 1.5f;
        }

        private IDogTarget Choose(float now)
        {
            List<IDogTarget> all = DogThreat.Targets;
            if (all.Count == 0) { Target = null; return null; }
            if (Target != null && all.Contains(Target) && Target.Targetable && (Engaged || now < nextChoice)) return Target;
            nextChoice = now + 1;
            IDogTarget best = null; float nearest = float.MaxValue;
            Vector3 me = _owner.Position;
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].Targetable) continue;
                float d = (all[i].AimPoint - me).sqrMagnitude;
                if (d < nearest) { nearest = d; best = all[i]; }
            }
            if (best != Target) { Target = best; awareness = 0; nextRelation = 0; }
            return best;
        }

        // Bitten: the bot doesn't have to notice the dog any more, it knows where it is, and for a few seconds (after the
        // stagger) it deals with the dog first: turns to it and shoots it once it has it in view.
        internal void Provoke(float stagger, IDogTarget dog)
        {
            float now = Time.time;
            awareness = Mathf.Max(awareness, 1f); restUntil = 0;
            if (dog != null) { Target = dog; LastSeen = dog.AimPoint; lastSeenTime = now; nextRelation = 0; }
            if (!Engaged) { Engaged = true; engagedSince = now; }
            bittenUntil = now + stagger + 4f;
        }

        private void Forget() { Engaged = false; Visible = false; awareness = 0; }

        private bool IsHostile(Player owner)
        {
            if (owner == null) return false;
            if (Time.time < nextRelation) return hostile;
            nextRelation = Time.time + 2f;
            hostile = DogThreat.HostileTo(_owner, owner);
            return hostile;
        }

        private bool CanSee(IDogTarget dog, out float reaction)
        {
            reaction = 0;
            LookSensor look = _owner.LookSensor;
            Vector3 eye = _owner.GetPlayer.MainParts[BodyPartType.head].Position;
            Vector3 to = dog.AimPoint - eye;
            float distance = to.magnitude;
            // A dog is a smaller, lower target than a person, and harder still to pick out while lying still or slinking along.
            float range = (look.VisibleDist > 1 ? look.VisibleDist : _owner.Settings.Current.CurrentVisibleDistance) * (dog.Resting || dog.Low ? .5f : .75f);
            if (distance > range || distance > look.MaxShootDist) return false;
            if (!BotReceiver.CanReceiveFromPoint(dog.AimPoint, eye, _owner.LookDirection, look.VISIBLE_ANGLE)) return false;
            if (Physics.Linecast(eye, dog.AimPoint, look.Mask) && Physics.Linecast(eye, dog.HeadPoint, look.Mask)) return false;
            bool peripheral = Vector3.Dot(_owner.LookDirection.normalized, to / Mathf.Max(distance, .01f)) < .77f;
            reaction = Mathf.Lerp(.3f, 2.4f, distance / range) * (peripheral ? 1.8f : 1f) * (dog.Low ? 1.6f : dog.Resting ? 1.4f : 1f);
            // Through bushes it's only a shape.
            if (Physics.Linecast(eye, dog.AimPoint, LayersMaskController.Foliage, QueryTriggerInteraction.Collide)) reaction *= 1.8f;
            return true;
        }
    }

    internal sealed class DogEngageNode : BaseNode<CoreActionResultParams>
    {
        private readonly DogThreatLayer layer;
        private readonly DogAiming aiming;
        private readonly AimingResultParams aim = new AimingResultParams(null);

        public DogEngageNode(BotOwner bot, DogThreatLayer threat) : base(bot)
        {
            layer = threat;
            aiming = new DogAiming(bot);
        }

        public override void UpdateNodeByBrain(CoreActionResultParams data)
        {
            IDogTarget dog = layer.Target;
            if (dog == null || !layer.Engaged) return;
            if (_owner.WeaponManager.IsMelee) _owner.WeaponManager.Selector.ChangeToMain();
            _owner.Sprint(false);
            _owner.StopMove();
            if (layer.Visible)
            {
                // The bot's own aiming applies its skill, scatter and aim time, then pulls its own trigger.
                aim.PointToShoot = dog.TrackedAimPoint;
                aiming.UpdateNodeByBrain(aim);
            }
            // Just bitten it knows where the dog is; otherwise it looks where it last saw it.
            else _owner.Steering.LookToPoint(layer.Bitten ? dog.AimPoint : layer.LastSeen);
        }
    }

    internal sealed class DogAiming : Aiming
    {
        public DogAiming(BotOwner bot) : base(bot) { }
    }
}
