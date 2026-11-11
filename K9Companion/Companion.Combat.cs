using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT;
using UnityEngine.AI;
using UnityEngine;

namespace K9Companion
{
    public sealed partial class Companion
    {
        // The raid dog hostile AI can perceive; null in the hideout and between raids.
        internal static Companion Raid;

        // What the dog may do this frame; refreshed from the shared settings at the start of every tick.
        private DogRules rules;
        private bool combatWas;
        private float health;
        private bool dead;
        // Dead to everything else (no orders, no hits, reported as killed), but a veterinary kit can still bring it back.
        private bool downed;
        private float downedUntil, nextDownedCall;
        private Transform interactZone;
        private string armourFor;
        private float armourLeft, jacketSpeed = 1;
        private string hitClip, deathClip, deathPose;
        private Transform hitboxes, pelvisBone, chestBone, headBone;
        private CapsuleCollider torso, neck;
        private SphereCollider skull;

        private readonly CombatBrain brain = new CombatBrain();
        private Player attackTarget;
        private float nextScan, nextReachCheck, nextSightCheck, biteImpact;
        private bool reachable, inSight, biting, biteApplied, strikeDone;
        private string biteClip;
        // Who recently hurt the owner or the dog (profile id -> time): what Defensive mode answers.
        private readonly Dictionary<string, float> provokers = new Dictionary<string, float>();
        // Targets the dog gave up on (profile id -> time until it leaves them alone).
        private readonly Dictionary<string, float> ignored = new Dictionary<string, float>();
        private Player ownerSubscribed;


        internal Vector3 AimPoint { get; private set; }

        // Where the dog's body was a moment ago. Bots aim here: they track a running dog with a reaction lag, so a dog on
        // the move is much harder to hit than one standing still.
        private const float TrackingLag = .18f;
        private readonly Vector3[] aimHistory = new Vector3[32];
        private readonly float[] aimTimes = new float[32];
        private int aimHead;
        internal Vector3 TrackedAimPoint
        {
            get
            {
                float wanted = Time.time - TrackingLag;
                for (int k = 0; k < aimHistory.Length; k++)
                {
                    int i = (aimHead - k + aimHistory.Length) % aimHistory.Length;
                    if (aimTimes[i] <= wanted && aimTimes[i] > 0) return aimHistory[i];
                }
                return AimPoint;
            }
        }

        private void RecordAim()
        {
            aimHead = (aimHead + 1) % aimHistory.Length;
            aimHistory[aimHead] = AimPoint; aimTimes[aimHead] = Time.time;
        }
        internal Vector3 HeadPoint { get; private set; }
        internal float HealthNow => health;
        internal float HealthMax => rules.Health;
        internal bool IsDead => dead;
        internal bool Downed => downed;
        // Just over its head: where its health bar floats.
        internal Vector3 OverheadPoint => dog == null ? Vector3.zero : (headBone != null ? headBone.position : dog.transform.position + Vector3.up * .7f) + Vector3.up * .35f;
        // In combat mode this raid (only then can it be hurt).
        internal bool CanBeHurt => rules.Combat;
        // Seconds left to revive a downed dog; negative when there's no clock (or it isn't down).
        internal float DownedSecondsLeft => downed && downedUntil > 0 ? Mathf.Max(0, downedUntil - Time.time) : -1;
        // Killed for good (not lying downed, waiting to be revived).
        internal bool Killed => dead && !downed;
        private string DeadNote => Plugin.DogName.Value + (downed ? " is down" : " is dead");
        internal float DistanceTo(Player player) => dog == null || player == null ? float.MaxValue : Vector3.Distance(dog.transform.position, player.Transform.position);
        internal Player Owner => CurrentPlayer;
        internal bool Resting => stance != Stance.Stand || shownSpeed < .15f;
        internal bool Targetable => dog != null && dog.activeInHierarchy && hitboxes != null && !dead && rules.CanBeTargeted;
        private bool HasTask => attackTarget != null;

        private void InitCombat()
        {
            rules = Plugin.Rules(false);
            combatWas = rules.Combat;
            // A dog still nursing wounds from an earlier combat raid starts with what it has left.
            health = rules.Health * (Plugin.CarryOverInjuries.Value && needs != null ? Mathf.Clamp(needs.Health, 1, 100) / 100f : 1f);
            ownerSubscribed = CurrentPlayer;
            if (ownerSubscribed != null) ownerSubscribed.BeingHitAction += OwnerHit;
            pelvisBone = model.Bone("RigPelvis"); chestBone = model.Bone("RigChest"); headBone = model.Bone("RigHead");
            // Built inactive so the ballistic components wake up with their material already set.
            var root = new GameObject("K9 hitboxes");
            root.SetActive(false);
            root.transform.SetParent(dog.transform, false);
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true; body.useGravity = false;
            torso = Hitbox<CapsuleCollider>(root.transform, "Torso", 1f);
            neck = Hitbox<CapsuleCollider>(root.transform, "Neck", 1.25f);
            skull = Hitbox<SphereCollider>(root.transform, "Head", 1.8f);
            torso.radius = .16f; torso.direction = 2;
            neck.radius = .09f; neck.direction = 2;
            skull.radius = .11f;
            hitboxes = root.transform;
            // What your interact key finds when you look at the dog ("Open jacket"); alive or dead, the jacket can be reached.
            var zone = new GameObject("K9 interaction") { layer = LayerMask.NameToLayer("Interactive") };
            zone.transform.SetParent(dog.transform, false);
            var box = zone.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = new Vector3(.36f, .5f, .95f);
            zone.AddComponent<DogInteractive>().Dog = this;
            interactZone = zone.transform;
            Raid = this;
            DogThreat.Targets.Add(this);
            audio.Played = DogNet.Sound;
        }

        // What the bots' dog layer goes by.
        Vector3 IDogTarget.AimPoint => AimPoint;
        Vector3 IDogTarget.HeadPoint => HeadPoint;
        Vector3 IDogTarget.TrackedAimPoint => TrackedAimPoint;
        bool IDogTarget.Resting => Resting;
        bool IDogTarget.Low => Low;
        bool IDogTarget.Targetable => Targetable;
        Player IDogTarget.Owner => Owner;
        void IDogTarget.Noticed(BotOwner by) => Noticed(by);

        private T Hitbox<T>(Transform parent, string name, float multiplier) where T : Collider
        {
            var go = new GameObject(name) { layer = LayerMask.NameToLayer("HitCollider") };
            go.transform.SetParent(parent, false);
            go.AddComponent<DogHitbox>().Init(this, multiplier);
            return go.AddComponent<T>();
        }

        // Breathing, all the time: quiet and slow at rest, softer still asleep, harder and faster after a run (it builds over
        // a few seconds of running and settles over half a minute) or when it's hurt (the worse, the harder).
        private void TickBreathing()
        {
            if (dog == null) return;
            if (dead) { audio?.Breathe(0, 1); return; }
            bool running = stance == Stance.Stand && shownSpeed > .15f && gait >= Gait.Run;
            exertion = Mathf.Clamp01(exertion + (running ? Time.deltaTime / 6f : -Time.deltaTime / 30f));
            float hurt = rules.Combat && rules.Health > 0 ? Mathf.Clamp01((1 - health / rules.Health) * 1.3f) : 0;
            float effort = Mathf.Max(exertion, hurt);
            bool asleep = stance == Stance.Sleeping || stance == Stance.SleepDown;
            // Barely there at rest (you hear it right beside the dog), harder only after running or when hurt.
            if (asleep) audio?.Breathe(.08f, .85f);
            else audio?.Breathe(.32f + .68f * effort, .92f + .25f * effort);
        }

        private void UpdateHitboxes()
        {
            if (hitboxes == null) return;
            Vector3 hip = pelvisBone.position, chest = chestBone.position, head = headBone.position;
            AimPoint = (hip + chest) * .5f; HeadPoint = head;
            RecordAim();
            if (interactZone != null)
            {
                interactZone.position = AimPoint;
                Vector3 along = chest - hip;
                if (along.sqrMagnitude > .0001f) interactZone.rotation = Quaternion.LookRotation(along, Vector3.up);
            }
            // With CombatMode off (or once dead) there is nothing for bullets to hit, so the dog cannot be hurt.
            bool on = Targetable;
            if (hitboxes.gameObject.activeSelf != on) hitboxes.gameObject.SetActive(on);
            if (!on) return;
            Span(torso, hip, chest);
            Span(neck, chest, head);
            skull.transform.position = head;
        }

        private static void Span(CapsuleCollider c, Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            c.transform.position = (a + b) * .5f;
            if (len > .01f) c.transform.rotation = Quaternion.LookRotation(d / len, Vector3.up);
            c.height = len + c.radius * 2;
        }

        internal void TakeHit(DamageInfo info, float multiplier)
        {
            if (!Targetable || rules.Immortal) return;
            // Stray rounds from the owner still draw blood but don't hurt the dog.
            Player owner = CurrentPlayer;
            IPlayer shooter = info.Player?.iPlayer;
            if (owner != null && shooter != null && shooter.ProfileId == owner.ProfileId) return;
            // Nor do your team-mates' (in Fika, or anywhere else a group plays together).
            if (owner != null && shooter != null && !string.IsNullOrEmpty(owner.GroupId) && shooter.GroupId == owner.GroupId) return;
            float damage = info.Damage * multiplier;
            if (multiplier <= 1f) damage = Armour(info, damage);   // the jacket covers the chest and back (the torso hitbox)
            if (damage <= 0) return;
            health -= damage;
            RecordHealth();
            bool fromLeft = Vector3.Dot(info.Direction, dog.transform.right) > 0;
            Provoked(info);
            Noticed(null);
            StopTreatment();
            if (health <= 0) { Die(fromLeft); return; }
            audio?.Play("whine");
            if (stance == Stance.Stand || stance == Stance.Turn)
            {
                stance = Stance.Stand;
                hitClip = "Hit_Chest" + (fromLeft ? "L" : "R") + (damage >= 35 ? "_Heavy" : "_Light");
            }
        }

        // The worn jacket's protection: stops or lets the round through by class against its penetration, and wears down.
        // A plate where the round comes in takes it first (its own class; its wear is the plate's, kept with it).
        private float Armour(DamageInfo info, float damage)
        {
            CompoundItem jacket = DogBag.FindInRaid(CurrentPlayer);
            ArmorComponent plate = DogGear.PlateFor(jacket, info.Direction, dog.transform)?.GetItemComponent<ArmorComponent>();
            if (plate != null && plate.ArmorClass > 0 && plate.Repairable != null && plate.Repairable.Durability > 0)
            {
                RepairableComponent wearing = plate.Repairable;
                float plateChance = DogArmour.PenetrationChance(plate.ArmorClass, wearing.Durability, Mathf.Max(1, wearing.MaxDurability), info.PenetrationPower);
                wearing.Durability = Mathf.Max(0, wearing.Durability - DogArmour.Wear(info.PenetrationPower, info.ArmorDamage));
                plate.Item.RaiseRefreshEvent(false, false);
                return UnityEngine.Random.value < plateChance ? damage * .9f : damage * .15f;   // stopped: blunt trauma only
            }
            DogArmour.Tier tier = DogArmour.Of(jacket);
            if (tier == null || tier.Class == 0) return damage;
            // Wear is remembered per jacket between raids (repaired at the kennel), so a battered jacket stays battered.
            K9.Wear wear = K9.WearOf(jacket.Id.ToString(), tier.Durability);
            if (armourFor != jacket.Id.ToString()) { armourFor = jacket.Id.ToString(); armourLeft = wear.Now; }
            if (armourLeft <= 0) return damage;
            float chance = DogArmour.PenetrationChance(tier.Class, armourLeft, tier.Durability, info.PenetrationPower);
            armourLeft = Mathf.Max(0, armourLeft - DogArmour.Wear(info.PenetrationPower, info.ArmorDamage));
            wear.Now = armourLeft; armourChanged = true;
            return UnityEngine.Random.value < chance ? damage * .9f : damage * .15f;   // stopped: blunt trauma only
        }

        private void Die(bool fromLeft)
        {
            dead = true; health = 0; hitClip = null; actionClip = null; speed = shownSpeed = 0;
            downed = Plugin.Downable && !IsHideout;
            RecordHealth();
            K9.DogDied();
            CancelTasks();
            if (RenameDialog.Owner == this) RenameDialog.Close();
            deathClip = fromLeft ? "Death_Stand_L" : "Death_Stand_R";
            deathPose = deathClip + "_Pose";
            // The whole body goes down along the ground: not the walking tilt as well.
            bodyPitch = 0; if (model?.Root != null) model.Root.localRotation = Quaternion.identity;
            LayAlongGround();
            audio?.Play("whine");
            string name = Plugin.DogName.Value;
            if (downed)
            {
                float time = Plugin.BleedOut;
                downedUntil = time > 0 ? Time.time + time : 0;
                nextDownedCall = Time.time + DownedCallEvery;
                string left = time > 0 ? $" You have {Mathf.RoundToInt(time)} s." : "";
                Say($"{name} is down: revive it with a K9 veterinary kit.{left}");
                EFT.Communications.NotificationManager.DisplayWarningNotification($"{name} is down: revive it with a K9 veterinary kit.{left}");
            }
            else Say(name + " has been killed.");
            // Whatever is still in its bag when the raid ends is lost; anything you take out first is yours.
            if (!IsHideout && (DogBag.FindInRaid(CurrentPlayer) != null || DogGear.RaidHead != null || DogGear.RaidCamera != null)) DogBag.DogDied();
            Plugin.Log.LogInfo(downed ? "Companion down." : "Companion killed.");
        }

        private const float DownedCallEvery = 20f;

        // Lying where it fell, waiting for you: the treatment runs from here, as nothing else does for a dead dog.
        private void TickDowned(Player owner)
        {
            if (treatItem != null)
            {
                if (Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach + 1f) StopTreatment();
                else
                {
                    if (downedUntil > 0) downedUntil += Time.deltaTime;
                    if (treatStart > 0 && Time.time >= treatStart) Kneel(owner);
                    if (treatItem != null && treatStart <= 0 && Time.time >= treatUntil) TreatmentEnded(true);
                    return;
                }
            }
            string name = Plugin.DogName.Value;
            if (downedUntil > 0 && Time.time >= downedUntil)
            {
                downed = false;
                Say(name + " has died of its wounds.");
                EFT.Communications.NotificationManager.DisplayWarningNotification(name + " has died of its wounds.");
                Plugin.Log.LogInfo("Companion died of its wounds.");
                return;
            }
            if (Time.time < nextDownedCall) return;
            nextDownedCall = Time.time + DownedCallEvery;
            audio?.Play("whine");
            Say(downedUntil > 0 ? $"{name} is down: {Mathf.CeilToInt(downedUntil - Time.time)} s left to revive it." : name + " is down: revive it with a K9 veterinary kit.");
        }

        // Revived: on the ground it fell on, lying down, and up from there like any other time it's called.
        private void GetUp()
        {
            dead = downed = false;
            hitClip = deathClip = deathPose = null;
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            if (GroundAt(dog.transform.position, out Vector3 ground)) dog.transform.position = ground;
            stance = Stance.Lying;
            idle = 0; lastProgress = Time.time;
            K9.DogRevived();
            DogBag.DogRevived();
            Plugin.Log.LogInfo("Companion revived.");
        }

        // The fall is animated on level ground. On a slope the body is first tilted to lie along the ground under it
        // (front to back and side to side, up to 40 degrees); once it is down, anything of its body still in the ground
        // (the hill rising on the side it fell to) is lifted out.
        private void LayAlongGround()
        {
            Transform t = dog.transform;
            Vector3 c = t.position, along = t.forward * .45f, across = t.right * .35f;
            if (!GroundAt(c + along, out Vector3 front) || !GroundAt(c - along, out Vector3 back)
                || !GroundAt(c + across, out Vector3 right) || !GroundAt(c - across, out Vector3 left)) return;
            Vector3 up = Vector3.Cross(front - back, right - left).normalized;
            if (up.y < 0) up = -up;
            up = Vector3.RotateTowards(Vector3.up, up, 40 * Mathf.Deg2Rad, 0);
            t.rotation = Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0, yaw, 0);
            t.position = new Vector3(c.x, (front.y + back.y + right.y + left.y) / 4, c.z);
        }

        private void LiftOutOfGround()
        {
            float lift = 0;
            foreach (string name in new[] { "RigPelvis", "RigSpine1", "RigSpine2", "RigSpine3", "RigChest", "RigNeck2", "RigNeck4", "RigHead" })
            {
                Transform bone = model.Bone(name);
                // A bone a little under the surface, or deep in the body lying on it: the ground above it, or close under.
                if (bone != null && Physics.Raycast(bone.position + Vector3.up * .5f, Vector3.down, out RaycastHit hit, 1f, groundMask, QueryTriggerInteraction.Ignore))
                    lift = Mathf.Max(lift, hit.point.y + .08f - bone.position.y);
            }
            if (lift > .01f) dog.transform.position += Vector3.up * Mathf.Min(lift, .5f);
        }

        private bool GroundAt(Vector3 p, out Vector3 ground)
        {
            bool found = Physics.Raycast(p + Vector3.up, Vector3.down, out RaycastHit hit, 2.5f, groundMask, QueryTriggerInteraction.Ignore);
            ground = found ? hit.point : p;
            return found;
        }

        // Applies the shared rules. CombatMode off means no attacking and no damage for a living dog. Death is final for
        // the raid: no setting change brings the dog back, and switching modes never refills its health (only a veterinary
        // kit brings back a downed dog).
        private void SyncRules()
        {
            rules = Plugin.Rules(IsHideout);
            if (hitboxes == null) return;
            if (!rules.Combat && combatWas) CancelTasks();
            combatWas = rules.Combat;
            if (dead) { health = 0; return; }
            health = Mathf.Min(health, rules.Health);
            if (!rules.CanAttack && brain.State != CombatState.Idle) StopFight();
        }

        // Dead dogs and hit flinches own the frame: no commands, no movement.
        private bool TickCombat(float dt, Player owner)
        {
            SyncRules();
            if (dead)
            {
                if (deathClip != null && model.Tick(deathClip, 1, dt, false)) { deathClip = null; LiftOutOfGround(); }
                if (deathClip == null) model.Tick(deathPose, 1, dt);
                if (downed) TickDowned(owner);
                return true;
            }
            if (hitboxes != null)
            {
                UpdateEngagement(owner);
            }
            if (hitClip == null) return false;
            speed = shownSpeed = 0; lastProgress = Time.time;
            if (model.Tick(hitClip, 1, dt, false)) hitClip = null;
            return true;
        }

        private void CancelTasks()
        {
            StopFight();
            CancelMeal();
            bedTrip = false;
            hunt = null;
        }

        // ---- attacking -------------------------------------------------------------------------------------------
        // Detection picks a target; CombatBrain runs the fight (Idle -> Chase -> Strike/Recover -> Return).

        private const float ScentRange = 8f, MouthReach = .6f, ChaseStop = .85f, BitePenetration = 12f;

        // Badly hurt dogs stop starting fights of their own and break off the ones they are in.
        private bool Wounded => rules.Combat && health < rules.Health * .2f;

        // Only AI that are hostile to the owner's side. Never the owner, other people, the owner's group or the bot's allies.
        private static bool IsEnemy(Player p, Player owner)
        {
            if (p == null || owner == null || p == owner || p.HealthController == null || !p.HealthController.IsAlive) return false;
            if (!string.IsNullOrEmpty(owner.GroupId) && p.GroupId == owner.GroupId) return false;
            // Only a bot that's really out there: not one switched off or parked by a spawn or AI mod (ORBIT's dormant
            // "ghost" bots, for one, stay in the living players' list while they're shut down far from you).
            if (!p.gameObject.activeInHierarchy || ModCompat.Dormant(p.ProfileId)) return false;
            // A bot run by the Fika host: only the host can say which side it's on.
            if (DogNet.IsRemoteBot(p)) return DogNet.HostileBot(p, owner);
            if (!p.IsAI) return false;
            BotOwner bot = p.AIData?.BotOwner;
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active) return false;
            return DogThreat.HostileTo(bot, owner);
        }

        private void OwnerHit(DamageInfo info, EBodyPart part, float absorbed) => Provoked(info);

        private void Provoked(DamageInfo info)
        {
            IPlayer attacker = info.Player?.iPlayer;
            if (attacker == null || !(attacker.IsAI || DogNet.IsRemoteBot(attacker as Player))) return;
            provokers[attacker.ProfileId] = Time.time;
            if (attacker is Player shooter) { lastShooter = shooter; lastShotAt = Time.time; }
        }

        // ---- being shot at --------------------------------------------------------------------------------------------
        // Whoever fired last at you or at the dog: a hit, or a shot that came close. With 'defends you' or 'any enemy' the dog
        // goes for them with no order given, within the defend range; on order, it only tells you where they are.

        private const float NearMiss = 2.5f, RememberShot = 12f, SwitchFrom = 8f;
        // An enemy this much nearer than the one shooting at you (or the one it's after) is dealt with first: the one in
        // front of you before one firing from across the field.
        private const float NearerBy = 10f;
        private Player lastShooter;
        private float lastShotAt;
        private bool defending;
        // Called off (Come here, Bring to me): no fights of its own picking for a while, so it stays with you while you're
        // under fire. An order of yours (Over There) ends it.
        private const float CallOffTime = 30;
        private float calledOffUntil;
        private bool CalledOff => Time.time < calledOffUntil;
        private bool PicksFights => rules.AutoEngage && !CalledOff;

        /// <summary>Drops any fight and keeps out of new ones of its own picking for <see cref="CallOffTime"/> seconds.</summary>
        private void CallOff()
        {
            if (attackTarget != null || brain.State != CombatState.Idle) StopFight();
            hunt = null;
            calledOffUntil = Time.time + CallOffTime;
        }

        // Every shot anyone fires (DogThreat.PatchGunfire).
        internal void ShotFired(Player shooter, Vector3 from, Vector3 direction)
        {
            Player owner = CurrentPlayer;
            if (dead || dog == null || owner == null || shooter == null || shooter == owner || !rules.Combat) return;
            if (!Gunfire.PassesNear(from, direction, Body(owner), NearMiss) && !Gunfire.PassesNear(from, direction, AimPoint, NearMiss)) return;
            if (!IsEnemy(shooter, owner)) return;
            provokers[shooter.ProfileId] = Time.time;
            lastShooter = shooter; lastShotAt = Time.time;
        }

        private Player Shooter(Player owner)
        {
            Player shooter = lastShooter;
            if (shooter == null || Time.time - lastShotAt > RememberShot || !IsEnemy(shooter, owner)) return null;
            if (ignored.TryGetValue(shooter.ProfileId, out float until) && Time.time < until) return null;
            if (Vector3.Distance(shooter.Transform.position, owner.Transform.position) > Plugin.DefendReach) return null;
            return Reachable(shooter.Transform.position) ? shooter : null;
        }

        private void GoForShooter(Player shooter)
        {
            // With an order's patience: it may be a long way off, and out of sight.
            Engage(shooter, true);
            defending = true;
            audio?.Play("bark");
            Say(Plugin.DogName.Value + " goes for " + shooter.Profile.Nickname + ": they're shooting at you.");
        }

        // Defensive mode: shooting at the dog, having hurt you or the dog lately, or having you in its sights right now.
        private bool Threatens(Player p, Player owner)
        {
            if (provokers.TryGetValue(p.ProfileId, out float when) && Time.time - when < 30) return true;
            BotOwner bot = p.AIData?.BotOwner;
            // A bot on the Fika host: all that can be told from here is whether it's facing you with a clear shot.
            if (bot == null)
            {
                Vector3 to = owner.Transform.position - p.Transform.position;
                return to.magnitude < 60 && Vector3.Dot(p.LookDirection.normalized, to.normalized) > .8f && ClearLine(Body(p), Body(owner));
            }
            if (DogThreat.Engagers.Contains(bot)) return true;
            EnemyInfo goal = bot.Memory?.GoalEnemy;
            return goal?.Person != null && goal.Person.ProfileId == owner.ProfileId && goal.IsVisible;
        }

        private static Vector3 Body(Player p) => p.MainParts[BodyPartType.body].Position;

        private static bool ClearLine(Vector3 from, Vector3 to)
            => !Physics.Linecast(from, to, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);

        private void Engage(Player target, bool ordered)
        {
            attackTarget = target; defending = false;
            brain.Engage(Time.time, ordered);
            reachable = inSight = true; nextReachCheck = nextSightCheck = 0;
            biting = strikeDone = false; lastProgress = Time.time;
            corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            StartStalk();
            WakeUp();
        }

        private void StopFight()
        {
            attackTarget = null; biting = strikeDone = defending = false;
            ResetStalk();
            corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            brain.Reset();
        }

        private void UpdateEngagement(Player owner)
        {
            if (brain.State == CombatState.Idle)
            {
                if (Wounded || command != Command.Follow || actionClip != null || Time.time < nextScan) return;
                nextScan = Time.time + .5f;
                Player under = rules.CanAttack ? Shooter(owner) : null;
                Player found = FindEnemy(owner);
                bool nearerFirst = under != null && found != null && found != under && DistanceTo(found) + NearerBy < DistanceTo(under);
                if (under != null && !nearerFirst)
                {
                    if (PicksFights) { GoForShooter(under); return; }
                    Alert(under, owner);
                }
                if (found == null) return;
                // Passive: it only alerts you, and goes in when ordered (Over There).
                if (!PicksFights) { Alert(found, owner); return; }
                Engage(found, false);
                if (!stalking) audio?.Play("bark");
                Say(Plugin.DogName.Value + (stalking ? " is stalking " : " goes for ") + found.Profile.Nickname + ".");
                return;
            }
            if (!rules.CanAttack || command != Command.Follow) { StopFight(); return; }
            // Someone else opens up on you while it's on its way back, or chasing something it picked itself: them first. Never
            // over an order of yours, nor when it's about to get its teeth into what it's after.
            if (PicksFights && !Wounded && Time.time >= nextScan
                && (brain.State == CombatState.Return || (brain.State == CombatState.Chase && (!brain.Ordered || defending))))
            {
                nextScan = Time.time + .5f;
                Player under = Shooter(owner), near = FindEnemy(owner);
                float current = DistanceTo(attackTarget);
                if (near != null && near != attackTarget && current > SwitchFrom
                    && DistanceTo(near) + NearerBy < Mathf.Min(current, under != null ? DistanceTo(under) : float.MaxValue))
                {
                    Engage(near, false);
                    audio?.Play("bark");
                    Say(Plugin.DogName.Value + " turns on " + near.Profile.Nickname + ": nearer.");
                    return;
                }
                if (under != null && under != attackTarget && current > SwitchFrom) { GoForShooter(under); return; }
            }
            if (Wounded && (!brain.Ordered || defending) && brain.State != CombatState.Return)
            {
                StopFight(); Say(Plugin.DogName.Value + " is too badly hurt to fight.");
                return;
            }
            CombatState before = brain.State;
            CombatState now = brain.Update(Sense(owner), brain.Ordered ? Mathf.Max(Plugin.OrderReach, defending ? Plugin.DefendReach : 0) : rules.EngageDistance, rules.BiteCooldown / NeedsFactor);
            strikeDone = false;
            if (now == CombatState.Return && before != CombatState.Return)
            {
                if (brain.Abandoned && attackTarget != null) ignored[attackTarget.ProfileId] = Time.time + 20;
                if (brain.Ordered) homeUntil = Time.time + 60;   // runs back from a far-off fight
                attackTarget = null; biting = defending = false;
                ResetStalk();
                corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
                if (brain.Reason != null) Say(brain.Reason + " Coming back.");
            }
            else if (now == CombatState.Strike && before != CombatState.Strike) StartBite();
        }

        // A working dog's indication: a growl and a bark, and where the enemy is from you. Once per enemy every half minute.
        private readonly Dictionary<string, float> alerted = new Dictionary<string, float>();
        private void Alert(Player enemy, Player owner)
        {
            if (alerted.TryGetValue(enemy.ProfileId, out float until) && Time.time < until) return;
            alerted[enemy.ProfileId] = Time.time + 30;
            audio?.Play("bark");
            Vector3 to = enemy.Transform.position - owner.Transform.position; to.y = 0;
            Vector3 look = owner.LookDirection; look.y = 0;
            float angle = Vector3.SignedAngle(look, to, Vector3.up);
            string side = Mathf.Abs(angle) < 35 ? "ahead" : Mathf.Abs(angle) > 135 ? "behind you" : angle > 0 ? "to your right" : "to your left";
            Say($"{Plugin.DogName.Value} growls: enemy {side}, about {Mathf.RoundToInt(to.magnitude)} m. Order Over There to send it in.");
        }

        private CombatSense Sense(Player owner)
        {
            var s = new CombatSense
            {
                Now = Time.time, Follow = Plugin.Distance.Value, StrikeDone = strikeDone, Stalking = stalking,
                DogFromOwner = Vector3.Distance(dog.transform.position, owner.Transform.position)
            };
            if (attackTarget == null || !(s.TargetValid = IsEnemy(attackTarget, owner))) return s;
            Vector3 at = attackTarget.Transform.position;
            s.TargetFromOwner = Vector3.Distance(at, owner.Transform.position);
            if (Time.time >= nextReachCheck) { nextReachCheck = Time.time + 1; reachable = Reachable(at); }
            if (Time.time >= nextSightCheck) { nextSightCheck = Time.time + .2f; inSight = ClearLine(HeadPoint, Body(attackTarget)); }
            s.Reachable = reachable; s.LineOfSight = inSight;
            s.InReach = BiteSpot(out _, out _);
            s.CanFight = !dead && hitClip == null && (stance == Stance.Stand || stance == Stance.Turn);
            s.Progressing = Time.time - lastProgress < 5;
            return s;
        }

        // The nearest enemy it notices and can get to. One it can't reach (across water, up on a roof) is passed over for
        // the next nearest, rather than nobody (a few path checks at most).
        private readonly List<(Player Who, float Distance)> candidates = new List<(Player, float)>();
        private Player FindEnemy(Player owner)
        {
            if (world == null) return null;
            Vector3 me = dog.transform.position, mine = owner.Transform.position;
            candidates.Clear();
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                if (ignored.TryGetValue(p.ProfileId, out float until) && Time.time < until) continue;
                Vector3 at = p.Transform.position;
                if (Vector3.Distance(at, mine) > rules.EngageDistance) continue;
                candidates.Add((p, Vector3.Distance(at, me)));
            }
            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int pathChecks = 0;
            foreach (var (p, d) in candidates)
            {
                if (rules.Aggression == Aggression.Defensive && !Threatens(p, owner)) continue;
                // The dog notices what it can see, or smell and hear close by. Walls hide anything further away.
                if (d > ScentRange && !ClearLine(HeadPoint, Body(p))) continue;
                if (Reachable(p.Transform.position)) return p;
                if (++pathChecks >= 3) break;
            }
            return null;
        }

        private bool Reachable(Vector3 point)
        {
            if (WalkingWithoutNavmesh) return HideoutRoute(dog.transform.position, point, out _);
            if (NavMesh.SamplePosition(dog.transform.position, out NavMeshHit from, 1.5f, NavMesh.AllAreas) &&
                NavMesh.SamplePosition(point, out NavMeshHit to, 2f, NavMesh.AllAreas))
            {
                var test = new NavMeshPath();
                if (NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, test) && test.status == NavMeshPathStatus.PathComplete) return true;
            }
            // Off the navmesh (see RefreshPath): close by, over the floor itself.
            return Flat(dog.transform.position, point) < 12 && Mathf.Abs(dog.transform.position.y - point.y) < 4
                   && HideoutRoute(dog.transform.position, point, out _);
        }

        // The closest biteable part within mouth reach, with nothing solid between. Legs first; arms and body only if
        // nothing lower is in reach; never the head.
        private bool BiteSpot(out BodyPartCollider part, out Vector3 point)
        {
            part = null; point = default;
            if (attackTarget == null || attackTarget.PlayerBones == null ||
                (attackTarget.Transform.position - dog.transform.position).sqrMagnitude > 6.25f) return false;
            Vector3 mouth = HeadPoint + dog.transform.forward * .18f;
            float best = MouthReach;
            foreach (BodyPartCollider c in attackTarget.PlayerBones.BodyPartColliders)
            {
                if (c == null || c.Collider == null || !c.Collider.enabled || c.BodyPartType == EBodyPart.Head) continue;
                Vector3 p;
                try { p = c.Collider.ClosestPoint(mouth); }
                catch { continue; }
                float d = Vector3.Distance(mouth, p);
                if (c.BodyPartType != EBodyPart.LeftLeg && c.BodyPartType != EBodyPart.RightLeg) d += .15f;
                if (d >= best || !ClearLine(mouth, p)) continue;
                best = d; part = c; point = p;
            }
            return part != null;
        }

        private void StartBite()
        {
            biting = true; biteApplied = strikeDone = false; lunged = 0;
            bool right = BiteSpot(out _, out Vector3 p) && Vector3.Dot(p - dog.transform.position, dog.transform.right) > 0;
            biteClip = right ? "Attack_BiteR" : "Attack_BiteL";
            // Damage lands on the frame the jaw snaps shut, read from the animation itself.
            biteImpact = model.SnapProgress(biteClip, "RigJaw1");
            model.Restart();
        }

        // Striking or recovering: the dog stands its ground facing the target and owns the frame.
        // It stays fully exposed to fire, since the hitboxes are never switched off during a fight.
        private bool TickBite(float dt)
        {
            if (attackTarget == null || (brain.State != CombatState.Strike && brain.State != CombatState.Recover)) { biting = false; return false; }
            WakeUp();
            if (stance != Stance.Stand) { speed = 0; Animate(dt); return true; }
            lastProgress = Time.time; speed = shownSpeed = 0;
            Vector3 delta = attackTarget.Transform.position - dog.transform.position; delta.y = 0;
            if (delta.sqrMagnitude > .01f) yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 540 * dt);
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            if (brain.State == CombatState.Strike && biting)
            {
                if (!biteApplied) Lunge(dt);
                bool finished = model.Tick(biteClip, 1, dt, false);
                if (!biteApplied && model.Progress >= biteImpact) { biteApplied = true; LandBite(); }
                if (finished) { biting = false; strikeDone = true; }
            }
            else model.Tick("Agressive_01", 1, dt);
            return true;
        }

        // Until the jaw snaps shut the dog closes in on the part it's going for, so the jaw (where the animation has it this
        // frame) meets the leg rather than snapping short of it. On the level, a little at a time, never more than 40 cm.
        private float lunged;
        private void Lunge(float dt)
        {
            if (!BiteSpot(out _, out Vector3 point)) return;
            Transform jaw = model.Bone("RigJaw1");
            Vector3 mouth = jaw != null ? jaw.position + dog.transform.forward * .05f : HeadPoint + dog.transform.forward * .18f;
            Vector3 gap = point - mouth; gap.y = 0;
            float step = Mathf.Min(gap.magnitude - .02f, 2.5f * dt, .4f - lunged);
            if (step <= 0) return;
            dog.transform.position += gap.normalized * step;
            lunged += step;
        }

        // At the jaw-snap frame: if the dog still has hold of the target (whatever part it reached), it goes for the throat,
        // under the helmet and any collar, so armour doesn't stop it. Each enemy dies in its own number of bites (Bites to
        // kill, bosses their own). If the target stepped away, the bite misses.
        private void LandBite()
        {
            Player owner = CurrentPlayer;
            if (owner == null || world == null || !IsEnemy(attackTarget, owner) || !BiteSpot(out _, out Vector3 point)) return;
            BodyPartCollider throat = Throat(attackTarget);
            if (throat?.playerBridge == null) return;
            float damage = DogRules.ThroatBite(attackTarget.HealthController.GetBodyPartHealth(EBodyPart.Head).Maximum, BitesFor(attackTarget));
            Vector3 dir = (point - HeadPoint).normalized;
            var info = new DamageInfo
            {
                DamageType = EDamageType.Melee, Damage = damage, PenetrationPower = BitePenetration, ArmorDamage = 0,
                StaminaBurnRate = 2, Direction = dir, HitPoint = throat.Collider.bounds.center, HitNormal = -dir, HitCollider = throat.Collider,
                HittedBallisticCollider = throat, IsForwardHit = true, SourceId = string.Empty,
                Player = world.GetEverExistedBridgeByProfileID(owner.ProfileId)
            };
            // Through the game's (and with Fika, the network's) own hit handling, with no collider named: no armour covers
            // "none", as with the game's own hits that have no collider. A problem in there costs this bite, not the dog.
            try { throat.playerBridge.ApplyShot(info, EBodyPart.Head, EBodyPartColliderType.None, 0, ShotId.EMPTY_SHOT_ID); }
            catch (System.Exception e) { Plugin.Log.LogWarning("The dog's bite: " + e.Message); }
            audio?.Play("bark");
            // That bite killed it: nothing left to stagger.
            if (attackTarget.HealthController != null && !attackTarget.HealthController.IsAlive) return;
            float stagger = Plugin.Stagger;
            BotOwner bitten = attackTarget.AIData?.BotOwner;
            // A bot on the Fika host reels and turns on the dog over there.
            if (bitten == null) DogNet.Bitten(attackTarget, stagger, dog.transform.position);
            else DogThreat.Staggered(bitten, stagger, dog.transform.position, this);
        }

        // The front of the neck if the skeleton has it, else any part of the head.
        private static BodyPartCollider Throat(Player p)
        {
            BodyPartCollider head = null;
            foreach (BodyPartCollider c in p.PlayerBones?.BodyPartColliders ?? System.Array.Empty<BodyPartCollider>())
            {
                if (c == null || c.Collider == null || c.BodyPartType != EBodyPart.Head) continue;
                if (c.BodyPartColliderType == EBodyPartColliderType.NeckFront) return c;
                head = head ?? c;
            }
            return head;
        }

        // Rolled once per enemy, so with 2.5 half of them take two bites and half three. Guards count as everyone else.
        private readonly Dictionary<string, int> bitesNeeded = new Dictionary<string, int>();
        private int BitesFor(Player p)
        {
            if (bitesNeeded.TryGetValue(p.ProfileId, out int n)) return n;
            ProfileSettings who = p.Profile?.Info?.Settings;
            bool boss = who != null && who.IsBoss() && !who.IsFollower();
            n = DogRules.Bites(boss ? rules.BossBitesToKill : rules.BitesToKill, UnityEngine.Random.value);
            bitesNeeded[p.ProfileId] = n;
            return n;
        }

        // The dog's health between raids (only combat can hurt it). A killed dog comes home in critical condition.
        private void RecordHealth()
        {
            if (needs == null || !Plugin.CarryOverInjuries.Value || !rules.Combat) return;
            float before = needs.Health;
            needs.Health = Mathf.Clamp(health / Mathf.Max(1, rules.Health) * 100f, 0, 100);
            // Saved straight away on death or a big change, so a crash or Alt+F4 can't undo it.
            if (dead || before - needs.Health >= 10 || Time.time >= nextHealthSave)
            {
                nextHealthSave = Time.time + 20;
                NeedsStore.Save();
                Plugin.Log.LogInfo($"Dog's health now {needs.Health:0}%" + (dead ? " (killed: it comes home in critical condition)." : "."));
            }
        }
        private float nextHealthSave;

        private bool armourChanged;

        private void CleanupCombat()
        {
            if (armourChanged) { armourChanged = false; K9.Save(); }
            attackTarget = null; lastShooter = null;
            bitesNeeded.Clear();
            if (Raid == this) DogNet.DogGone();
            if (Raid == this) Raid = null;
            DogThreat.Targets.Remove(this);
            // Other players' dogs may still be about (Fika): the bots' records are cleared when the raid's over instead.
            if (!DogNet.Online) DogThreat.RaidOver();
            if (ownerSubscribed != null) ownerSubscribed.BeingHitAction -= OwnerHit;
            ownerSubscribed = null;
            hitboxes = null;
        }
    }

    /// <summary>The raid's navigation mesh and colliders, for CoverRoute.</summary>
    internal sealed class NavCoverWorld : ICoverWorld
    {
        private readonly NavMeshPath path = new NavMeshPath();

        public bool Snap(Vector3 point, float radius, out Vector3 on)
        {
            bool ok = NavMesh.SamplePosition(point, out NavMeshHit hit, radius, NavMesh.AllAreas);
            on = ok ? hit.position : point;
            return ok;
        }

        public bool CanWalk(Vector3 from, Vector3 to) => !NavMesh.Raycast(from, to, out _, NavMesh.AllAreas);

        // The same mask the bots' own eyes use, so hidden from this is hidden from them.
        public bool Blocked(Vector3 from, Vector3 to) => Physics.Linecast(from, to, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);

        public bool Screened(Vector3 from, Vector3 to) => Physics.Linecast(from, to, LayersMaskController.Foliage, QueryTriggerInteraction.Collide);

        public bool Path(Vector3 from, Vector3 to, List<Vector3> corners)
        {
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            corners.AddRange(path.corners);
            return true;
        }

        public float EdgeDistance(Vector3 point) => NavMesh.FindClosestEdge(point, out NavMeshHit hit, NavMesh.AllAreas) ? hit.distance : float.MaxValue;
    }

    /// <summary>
    /// Going for an enemy without being seen. Far off it runs, by a way that keeps out of sight of the target and any other
    /// enemy about (CoverRoute). Close in, it trots while it's hidden and goes low and slow where it can be seen. At the edge of
    /// cover it waits a moment if someone's looking at the ground ahead, and crosses a short gap in one dash. Within rushing
    /// distance, or once it has been noticed close to, it stops hiding: a bark, a sprint, the bite.
    /// The way bots notice the dog (DogThreatLayer) takes all this in: low, it has to be closer and takes longer to spot,
    /// and behind cover it can't be seen at all.
    /// That's the Stealth approach. Auto does the same with anything further off, but goes straight in at anything really
    /// close, and back to stalking if it gets away. Direct just runs at it. They can be changed in the middle of a chase.
    /// </summary>
    public sealed partial class Companion
    {
        // How close before it stops hiding: with the target in plain view, and if it has been spotted.
        private const float RushRange = 9f, SpottedRush = 18f;
        // Further along the route than this it's still just getting there; inside it, it's careful; inside quiet range it creeps.
        private const float ProwlRange = 30f, QuietRange = 13f;
        private const float PlanEvery = 8f, TargetMoved = 5f, PlanBudget = 1.2f;
        private const float LongestHold = 2.6f, TotalHold = 7f, ShortGap = 6f;
        private const float WatchRange = 70f, AheadLook = 3.5f, Noticeable = .25f;
        // A bot's own numbers where it's on this machine; otherwise (a Fika client) the game's usual ones.
        private const float UsualViewCos = -.64f, UsualViewRange = 120f;

        private readonly CoverRoute route = new CoverRoute();
        private readonly NavCoverWorld cover = new NavCoverWorld();
        private readonly List<Watcher> watchers = new List<Watcher>();
        private bool stalking, rushing, spotted, planning, stalkLow, dashing, seenHere, seenAhead, watchedAhead;
        private float nextWatch, nextPlan, noRouteUntil, holdUntil, holdLeft, gapAhead, nextRushBark;
        private Vector3 plannedFor;

        // Crouched: it takes longer to notice and has to be closer. (Bots read this; so does the walk.)
        internal bool Low => stalkLow || (crouching && !HasTask && shownSpeed < 1.5f);

        private void StartStalk()
        {
            ResetStalk();
            if (Plugin.Approaching == DogApproach.Direct || WalkingWithoutNavmesh) return;
            stalking = true; holdLeft = TotalHold;
        }

        private void ResetStalk()
        {
            stalking = rushing = spotted = planning = stalkLow = dashing = false;
            seenHere = seenAhead = watchedAhead = false;
            nextWatch = nextPlan = noRouteUntil = holdUntil = 0;
            route.Cancel();
        }

        // A bot has noticed the dog, or something has hit it.
        internal void Noticed(BotOwner by)
        {
            if (!stalking || spotted) return;
            spotted = true; holdUntil = 0; nextPlan = 0;
            Plugin.Log.LogInfo("Stalk: spotted" + (by != null ? " by " + by.Profile?.Nickname : "") + ".");
        }

        // The end of hiding.
        private void Rush()
        {
            stalking = false; rushing = true; stalkLow = dashing = planning = false; holdUntil = 0;
            route.Cancel();
            corners = Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            // Once, however often Auto changes its mind as the target comes and goes.
            if (Time.time < nextRushBark) return;
            nextRushBark = Time.time + 6;
            audio?.Play("bark");
            if (attackTarget != null) Say(Plugin.DogName.Value + " goes in!");
        }

        // How fast to go this frame while stalking; below zero when it isn't, and the plain chase takes over. A problem in
        // here only ends the sneaking: it still goes for the target.
        private float TickStalk(Vector3 from, Tuning t)
        {
            try { return StalkPace(from, t); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stalk given up: " + e);
                Rush();
                return -1;
            }
        }

        private float StalkPace(Vector3 from, Tuning t)
        {
            if (attackTarget == null) { stalkLow = false; return -1; }
            DogApproach how = WalkingWithoutNavmesh ? DogApproach.Direct : Plugin.Approaching;
            Vector3 at = attackTarget.Transform.position;
            float straight = Vector3.Distance(from, at);
            if (how == DogApproach.Direct)
            {
                if (stalking) Rush();
                stalkLow = false;
                return -1;
            }
            if (!stalking)
            {
                // Rushing under Auto, it goes back to stalking if the target has got away. (Stealth's rush is for good. Not
                // rushing at all means the approach has just been changed from Direct.)
                if (rushing && (how != DogApproach.Auto || ApproachRule.GoDirect(straight, Plugin.AutoRush, true))) { stalkLow = false; return -1; }
                stalking = true; rushing = spotted = false;
                holdLeft = TotalHold; holdUntil = nextWatch = nextPlan = 0;
                corners = Array.Empty<Vector3>(); corner = 0;
            }
            bool close = how == DogApproach.Auto && ApproachRule.GoDirect(straight, Plugin.AutoRush, false);
            if (close || (straight <= (spotted ? SpottedRush : RushRange) && inSight) || straight <= 3.5f) { Rush(); return -1; }
            if (Time.time >= nextWatch) { nextWatch = Time.time + .25f; LookOut(from, attackTarget); }
            if (!KeepRoute(from, at)) { Rush(); return -1; }
            if (corner >= corners.Length) { lastProgress = Time.time; stalkLow = true; return 0; }   // waiting for a new route

            float left = PathLeft(from);
            float run = Locomotion.Speed(Gait.Run, t) * .8f, trot = Locomotion.Speed(Gait.Trot, t) * .95f, prowl = Locomotion.StalkSpeed(t);
            if (spotted) { stalkLow = false; return Locomotion.Speed(Gait.Run, t); }
            if (left > ProwlRange) { stalkLow = false; return seenHere ? Locomotion.Speed(Gait.Run, t) : run; }

            if (Time.time < holdUntil)
            {
                if (watchedAhead) { lastProgress = Time.time; stalkLow = true; return 0; }
                holdUntil = 0;
            }
            if (!seenHere && seenAhead)
            {
                if (watchedAhead && holdLeft > 0)
                {
                    float hold = Mathf.Min(LongestHold, holdLeft);
                    holdLeft -= hold; holdUntil = Time.time + hold;
                    lastProgress = Time.time; stalkLow = true;
                    return 0;
                }
                dashing = gapAhead <= ShortGap;
            }
            else if (seenHere && !seenAhead) dashing = true;   // hidden again just ahead
            else if (!seenHere && !seenAhead) dashing = false;
            if (dashing) { stalkLow = false; return run; }
            if (seenHere || seenAhead) { stalkLow = true; return prowl; }
            stalkLow = left <= QuietRange;
            return stalkLow ? prowl : trot;
        }

        // The route, re-planned when the target has moved off or it has gone stale; false when there's no way at all.
        private bool KeepRoute(Vector3 from, Vector3 to)
        {
            if (Time.time < noRouteUntil) return false;
            if (planning)
            {
                if (route.Step(PlanBudget))
                {
                    planning = false;
                    if (route.Points != null && route.Points.Length > 1) UseRoute(route.Points);
                }
                return true;
            }
            bool stale = corner >= corners.Length || (to - plannedFor).sqrMagnitude > TargetMoved * TargetMoved || Time.time >= nextPlan;
            if (!stale) return true;
            nextPlan = Time.time + PlanEvery; plannedFor = to;
            if (!route.Begin(cover, from, to, watchers)) { noRouteUntil = Time.time + 2; return false; }
            planning = true;
            // Off along the plain path while the better one is worked out (it takes a moment).
            if (corner >= corners.Length && route.Plain != null) UseRoute(route.Plain);
            return true;
        }

        private void UseRoute(Vector3[] points)
        {
            corners = points; corner = Mathf.Min(1, points.Length - 1); pathTarget = points[points.Length - 1];
            pathExpires = float.MaxValue;
        }

        // Who's about, and what they can see of here and of the way ahead.
        private void LookOut(Vector3 from, Player prey)
        {
            GatherWatchers(from, prey != null ? prey.Transform.position : from, prey);
            seenHere = Noticeable <= CoverRoute.Exposure(cover, from, watchers);
            Vector3 ahead = PointAlong(from, AheadLook);
            seenAhead = Noticeable <= CoverRoute.Exposure(cover, ahead, watchers);
            watchedAhead = seenAhead && watchers.Any(w => Vector3.Dot(w.Look, (ahead - w.Eye).normalized) > .7f);
            gapAhead = 0;
            if (!seenAhead) return;
            // How much of the way ahead is in the open.
            for (float d = AheadLook; d <= AheadLook + 9; d += 1.5f)
            {
                if (Noticeable > CoverRoute.Exposure(cover, PointAlong(from, d), watchers)) break;
                gapAhead += 1.5f;
            }
        }

        private void GatherWatchers(Vector3 from, Vector3 around, Player prey)
        {
            watchers.Clear();
            Player owner = CurrentPlayer;
            if (world == null || owner == null) return;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                if (p != prey && (p.Transform.position - from).sqrMagnitude > WatchRange * WatchRange && (p.Transform.position - around).sqrMagnitude > WatchRange * WatchRange) continue;
                if (p.MainParts == null || !p.MainParts.TryGetValue(BodyPartType.head, out EnemyPart head)) continue;
                LookSensor look = p.AIData?.BotOwner?.LookSensor;
                watchers.Add(new Watcher
                {
                    Eye = head.Position, Look = p.LookDirection.normalized, Prey = p == prey,
                    Weight = p == prey ? 1f : .8f, ViewCos = look != null ? look.VISIBLE_ANGLE : UsualViewCos,
                    Range = look != null && look.VisibleDist > 1 ? look.VisibleDist * .75f : UsualViewRange
                });
            }
            // The target and the nearest few are all that matter.
            if (watchers.Count > 5)
            {
                Vector3 me = from;
                watchers.Sort((a, b) => a.Prey != b.Prey ? (a.Prey ? -1 : 1) : (a.Eye - me).sqrMagnitude.CompareTo((b.Eye - me).sqrMagnitude));
                watchers.RemoveRange(5, watchers.Count - 5);
            }
        }

        private float PathLeft(Vector3 from)
        {
            float left = 0; Vector3 p = from;
            for (int i = corner; i < corners.Length; i++) { left += Flat(p, corners[i]); p = corners[i]; }
            return left;
        }

        private Vector3 PointAlong(Vector3 from, float distance)
        {
            Vector3 p = from;
            for (int i = corner; i < corners.Length; i++)
            {
                float leg = Flat(p, corners[i]);
                if (leg >= distance && leg > .001f) return Vector3.Lerp(p, corners[i], distance / leg);
                distance -= leg; p = corners[i];
            }
            return p;
        }

        // The run out to a hunt: a way that keeps out of sight of the spot itself (anyone could be there) and of any enemy
        // about, while it's a long way off. Close to, or once there, the plain path. Below zero when it's on the plain path.
        private float TickHuntRoute(Vector3 from, Vector3 spot, Tuning t)
        {
            if (Plugin.Approaching == DogApproach.Direct || huntArrived > 0 || WalkingWithoutNavmesh) return -1;
            float left = Flat(from, spot);
            if (left < HuntPlainWithin) { stalkLow = false; return -1; }
            try
            {
                if (Time.time >= nextWatch)
                {
                    nextWatch = Time.time + .5f;
                    GatherWatchers(from, spot, null);
                    watchers.Add(new Watcher
                    {
                        Eye = spot + Vector3.up * 1.5f, Look = (from - spot).normalized, Weight = .7f,
                        ViewCos = -1, Range = UsualViewRange * .7f, Prey = true
                    });
                }
                if (!KeepRoute(from, spot)) return -1;
                if (corner >= corners.Length) { lastProgress = Time.time; return 0; }
                return Locomotion.Speed(Gait.Run, t) * .8f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The hunt's covered route: " + e.Message);
                noRouteUntil = Time.time + 30;
                return -1;
            }
        }

        // Where the head points: at the target, along the way ahead, round the ground when sniffing, at you when it's close by.
        private void TickLook(Vector3 from, Player owner)
        {
            if (dead || (stance != Stance.Stand && stance != Stance.Sitting)) return;
            if (Sniffing)
            {
                float t = Time.time;
                model.LookAt(Mathf.Sin(t * 2.3f) * 30f, 8f + Mathf.Sin(t * 5.3f) * 7f);
                return;
            }
            Vector3 eye = from + Vector3.up * .55f + Quaternion.Euler(0, yaw, 0) * Vector3.forward * .55f;
            Vector3 at;
            float weight = 1;
            if (attackTarget != null) at = Body(attackTarget);
            else if (shownSpeed > .4f && corner < corners.Length) { at = Ahead(from) + Vector3.up * .4f; weight = .7f; }
            else if (Vector3.Distance(from, owner.Transform.position) < 5f) { at = owner.Transform.position + Vector3.up * 1.3f; weight = .75f; }
            else return;
            Vector3 to = at - eye;
            float flat = new Vector2(to.x, to.z).magnitude;
            if (flat < .05f) return;
            float turn = Mathf.DeltaAngle(yaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            // Behind it: it doesn't try to look over its shoulder.
            if (Mathf.Abs(turn) > 120) return;
            model.LookAt(turn * weight, -Mathf.Atan2(to.y, flat) * Mathf.Rad2Deg * weight);
        }

        // The body follows the slope under it (on stairs and hillsides it was held level, legs in the ground).
        private void TickBody(Vector3 at, float dt)
        {
            if (IsHideout || model?.Root == null) return;
            Vector3 along = Quaternion.Euler(0, yaw, 0) * Vector3.forward * BodyHalf;
            float wanted = 0;
            if (GroundAt(at + along, out Vector3 front) && GroundAt(at - along, out Vector3 back))
                wanted = Mathf.Clamp(Mathf.Atan2(back.y - front.y, BodyHalf * 2) * Mathf.Rad2Deg, -MaxSlope, MaxSlope);
            bodyPitch += (wanted - bodyPitch) * (1 - Mathf.Exp(-9 * dt));
            model.Root.localRotation = Quaternion.Euler(bodyPitch, 0, 0);
        }
        private const float BodyHalf = .42f, MaxSlope = 28f;
        private float bodyPitch;
    }

    /// <summary>Whether a shot was at someone: its line passes this close, on its way out rather than behind the muzzle.</summary>
    internal static class Gunfire
    {
        public static bool PassesNear(UnityEngine.Vector3 from, UnityEngine.Vector3 direction, UnityEngine.Vector3 point, float within)
        {
            UnityEngine.Vector3 to = point - from;
            float along = UnityEngine.Vector3.Dot(to, direction);
            return along > 0 && (to - direction * along).sqrMagnitude <= within * within;
        }
    }

    internal enum CombatState { Idle, Chase, Strike, Recover, Return }

    /// <summary>What the dog can tell about its fight this frame. Filled in by the companion, judged by CombatBrain.</summary>
    internal struct CombatSense
    {
        public float Now;
        public bool TargetValid;   // alive, AI, still hostile to the owner
        public bool Reachable;     // a complete navmesh path exists (re-checked every second)
        public bool LineOfSight;   // nothing solid between the dog's head and the target's body
        public bool InReach;       // a biteable body part is within mouth reach, with no wall in between
        public bool CanFight;      // alive, standing, not flinching, not badly wounded
        public bool Progressing;   // has moved or been in contact recently (not stuck)
        public bool StrikeDone;    // the bite animation has finished
        public bool Stalking;      // working its way in out of sight: it knows where the target is by scent
        public float TargetFromOwner, DogFromOwner, Follow;
    }

    /// <summary>
    /// The dog's fight in five states: Idle, Chase the target, Strike (one bite animation), Recover (cooldown, facing the
    /// target) and Return to the owner. It holds no Unity state, so the rules can be tested on their own.
    /// </summary>
    internal sealed class CombatBrain
    {
        public const float LoseSightAfter = 5f, ChaseLimit = 30f, FightLimit = 60f;
        // A stalk is slower than a straight chase.
        public const float StalkChase = 70f, StalkFight = 100f;
        // Sent in on your order (perhaps far off, by scent): it keeps tracking longer before it gives up.
        public const float OrderedLoseSight = 15f, OrderedChase = 90f, OrderedFight = 120f;

        private float engagedAt, lastSeen, nextStrike;

        public CombatState State { get; private set; }
        public bool Ordered { get; private set; }
        // Why the last fight ended; null while fighting or when nothing needs saying.
        public string Reason { get; private set; }
        // True when the dog gave up on a target that is still alive, so it shouldn't pick it straight back up.
        public bool Abandoned { get; private set; }

        public void Engage(float now, bool ordered)
        {
            State = CombatState.Chase; Ordered = ordered; Reason = null; Abandoned = false;
            engagedAt = lastSeen = now;
        }

        public void Reset() { State = CombatState.Idle; Ordered = false; Reason = null; Abandoned = false; }

        public CombatState Update(in CombatSense s, float engageDistance, float cooldown)
        {
            if (State == CombatState.Idle) return State;
            if (State == CombatState.Return)
            {
                if (s.DogFromOwner <= s.Follow + 1.5f) State = CombatState.Idle;
                return State;
            }
            if (!s.TargetValid) return Leave("Target down.", false);
            if (s.LineOfSight || s.Stalking) lastSeen = s.Now;
            bool fighting = State == CombatState.Strike || State == CombatState.Recover;
            // engageDistance is the ordered range for an ordered fight (the companion passes whichever applies).
            float range = Ordered ? engageDistance : engageDistance * 1.5f;
            if (s.TargetFromOwner > range || s.DogFromOwner > range * 1.2f + 10) return Leave("Too far from you.", true);
            if (s.Now - engagedAt > (Ordered ? OrderedFight : s.Stalking ? StalkFight : FightLimit)) return Leave("Breaking off.", true);
            if (!fighting)
            {
                if (!s.Reachable) return Leave("Can't reach the target.", true);
                if (s.Now - lastSeen > (Ordered ? OrderedLoseSight : LoseSightAfter)) return Leave("Lost the target.", true);
                if (!s.Progressing) return Leave("Can't get to the target.", true);
                if (s.Now - engagedAt > (Ordered ? OrderedChase : s.Stalking ? StalkChase : ChaseLimit)) return Leave("Gave up the chase.", true);
            }

            bool canBite = s.CanFight && s.InReach && s.LineOfSight;
            switch (State)
            {
                case CombatState.Chase:
                    if (canBite) State = s.Now >= nextStrike ? CombatState.Strike : CombatState.Recover;
                    break;
                case CombatState.Strike:
                    // A flinch or a fall cancels the bite; either way the cooldown starts.
                    if (!s.CanFight || s.StrikeDone) { State = CombatState.Recover; nextStrike = s.Now + cooldown; }
                    break;
                case CombatState.Recover:
                    if (!s.InReach || !s.LineOfSight) State = CombatState.Chase;
                    else if (canBite && s.Now >= nextStrike) State = CombatState.Strike;
                    break;
            }
            return State;
        }

        private CombatState Leave(string reason, bool abandoned)
        {
            State = CombatState.Return; Reason = reason; Abandoned = abandoned;
            return State;
        }
    }

    /// <summary>Someone the dog would rather not be seen by.</summary>
    internal struct Watcher
    {
        public Vector3 Eye, Look;
        public float Weight, ViewCos, Range;
        public bool Prey;
    }

    /// <summary>What the route planner asks of the world (the raid's navigation mesh and colliders, see NavCoverWorld).</summary>
    internal interface ICoverWorld
    {
        bool Snap(Vector3 point, float radius, out Vector3 on);
        bool CanWalk(Vector3 from, Vector3 to);
        // Something solid is in the way.
        bool Blocked(Vector3 from, Vector3 to);
        // Bushes or the like are in the way: not cover from a bullet, but harder to be seen through.
        bool Screened(Vector3 from, Vector3 to);
        bool Path(Vector3 from, Vector3 to, List<Vector3> corners);
        float EdgeDistance(Vector3 point);
    }

    /// <summary>
    /// A way to somewhere that keeps out of sight. The navigation mesh's shortest path is widened into a band of walkable
    /// points either side of it; each point costs more the more the watchers can see of a dog standing there (out of their
    /// view it's nearly free, in the open in front of them it's dear) and a search finds the cheapest way through. So the
    /// dog goes round the far side of a wall, along a hedge or behind a building when that's worth the extra distance, and
    /// straight when there's nothing to hide behind.
    /// It works a little each frame, so a long route never costs a stall. Holds no Unity objects.
    /// </summary>
    internal sealed class CoverRoute
    {
        private const int Sides = 5;
        private const float MinSpacing = 3f, MaxSpacing = 6f, StationsWanted = 36f;
        private const float ExposedCost = 8f, CloseToOtherEnemy = 8f, OtherEnemyCost = 5f, EdgeHug = 1.2f, EdgeBonus = .15f, NotSeen = .12f;
        // Inside this distance of the end it's going to rush anyway, so being seen matters less.
        private const float RushZone = 10f;
        private const float DogLow = .45f, DogHigh = .7f, CheapestMetre = .8f;

        private enum Stage { Sampling, Searching, Smoothing, Done }

        private struct Node { public Vector3 P; public float Cost, Exposure; public int Row, Column; }

        private readonly List<Vector3> spine = new List<Vector3>();
        private readonly List<Vector3> stations = new List<Vector3>();
        private readonly List<Vector3> sideways = new List<Vector3>();
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<Watcher> eyes = new List<Watcher>();
        private readonly List<int> found = new List<int>();
        private readonly List<Vector3> smoothed = new List<Vector3>();
        private readonly Stopwatch clock = new Stopwatch();
        private ICoverWorld world;
        private Stage stage = Stage.Done;
        private int[] grid;
        private float spacing;
        private int rows, sampleRow, sampleColumn, start, goal, smoothAt;
        // The search.
        private float[] cost;
        private int[] came;
        private bool[] closed;
        private readonly List<KeyValuePair<float, int>> open = new List<KeyValuePair<float, int>>();

        // The shortest way, usable while the better one is being worked out.
        public Vector3[] Plain { get; private set; }
        // The route, first point where it starts; null until Step says it's done.
        public Vector3[] Points { get; private set; }
        public bool Busy => stage != Stage.Done;

        public bool Begin(ICoverWorld surroundings, Vector3 from, Vector3 to, IList<Watcher> watchers)
        {
            world = surroundings; Points = null; Plain = null; stage = Stage.Done;
            eyes.Clear(); eyes.AddRange(watchers);
            if (!world.Snap(from, 1.5f, out Vector3 a) || !world.Snap(to, 2.5f, out Vector3 b)) return false;
            spine.Clear();
            if (!world.Path(a, b, spine) || spine.Count < 2) return false;
            Plain = spine.ToArray();
            MakeStations();
            nodes.Clear(); open.Clear();
            grid = new int[rows * (Sides * 2 + 1)];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
            sampleRow = sampleColumn = 0;
            stage = Stage.Sampling;
            return true;
        }

        // Works for about this long; true once it's finished (Points has the route).
        public bool Step(float milliseconds)
        {
            if (stage == Stage.Done) return true;
            clock.Restart();
            long budget = (long)(milliseconds * Stopwatch.Frequency / 1000f);
            while (stage != Stage.Done && clock.ElapsedTicks < budget)
            {
                switch (stage)
                {
                    case Stage.Sampling: SampleOne(); break;
                    case Stage.Searching: SearchOne(); break;
                    case Stage.Smoothing: SmoothOne(); break;
                }
            }
            return stage == Stage.Done;
        }

        public void Cancel() { stage = Stage.Done; }

        // How much of a dog standing there the watchers can see, 0 (nothing) to 1 (right in front of them, close).
        internal static float Exposure(ICoverWorld world, Vector3 ground, IList<Watcher> eyes)
        {
            float worst = 0;
            Vector3 low = ground + Vector3.up * DogLow, high = ground + Vector3.up * DogHigh;
            for (int i = 0; i < eyes.Count; i++)
            {
                Watcher e = eyes[i];
                Vector3 to = low - e.Eye;
                float distance = to.magnitude;
                if (distance > e.Range || distance < .01f) continue;
                float facing = Vector3.Dot(e.Look, to / distance);
                float view = facing >= .77f ? 1f : facing >= e.ViewCos ? .6f : NotSeen;
                float amount = e.Weight * view * Mathf.Lerp(1f, .45f, distance / e.Range);
                if (amount <= worst) continue;
                // A dog is low: a crate or a low wall that wouldn't hide a man hides it. Both must be blocked, as bots look for either.
                if (world.Blocked(e.Eye, low) && world.Blocked(e.Eye, high)) continue;
                if (world.Screened(e.Eye, low)) amount *= .5f;
                if (amount > worst) worst = amount;
            }
            return worst;
        }

        private void MakeStations()
        {
            float length = 0;
            for (int i = 1; i < spine.Count; i++) length += Flat(spine[i - 1], spine[i]);
            spacing = Mathf.Clamp(length / StationsWanted, MinSpacing, MaxSpacing);
            stations.Clear(); sideways.Clear();
            stations.Add(spine[0]);
            float next = spacing;
            float walked = 0;
            for (int i = 1; i < spine.Count; i++)
            {
                float leg = Flat(spine[i - 1], spine[i]);
                while (leg > .001f && next <= walked + leg)
                {
                    if (length - next > spacing * .5f) stations.Add(Vector3.Lerp(spine[i - 1], spine[i], (next - walked) / leg));
                    next += spacing;
                }
                walked += leg;
            }
            stations.Add(spine[spine.Count - 1]);
            rows = stations.Count;
            for (int r = 0; r < rows; r++)
            {
                Vector3 along = stations[Mathf.Min(r + 1, rows - 1)] - stations[Mathf.Max(r - 1, 0)];
                along.y = 0;
                along = along.sqrMagnitude > .0001f ? along.normalized : Vector3.forward;
                sideways.Add(new Vector3(along.z, 0, -along.x));
            }
        }

        // One point of the band. It starts narrow at the dog and widens, and stays wide right up to the far end: coming in
        // from the side or from behind means reaching the target from anywhere around it.
        private void SampleOne()
        {
            if (sampleRow >= rows)
            {
                start = grid[Sides]; goal = grid[(rows - 1) * (Sides * 2 + 1) + Sides];
                BeginSearch();
                return;
            }
            int column = sampleColumn - Sides;
            bool wanted = Mathf.Abs(column) * spacing <= spacing * 2 + sampleRow * spacing * .8f;
            if (wanted)
            {
                Vector3 centre = stations[sampleRow];
                Vector3 p = centre;
                bool ok = column == 0 || (world.Snap(centre + sideways[sampleRow] * (column * spacing), spacing * .45f, out p) && Mathf.Abs(p.y - centre.y) < 2f);
                if (ok)
                {
                    float seen = Exposure(world, p, eyes);
                    float price = 1 + ExposedCost * seen * (Flat(p, stations[rows - 1]) < RushZone ? .35f : 1f);
                    for (int i = 0; i < eyes.Count; i++)
                        if (!eyes[i].Prey && (eyes[i].Eye - p).sqrMagnitude < CloseToOtherEnemy * CloseToOtherEnemy) { price += OtherEnemyCost; break; }
                    if (world.EdgeDistance(p) < EdgeHug) price -= EdgeBonus;
                    grid[sampleRow * (Sides * 2 + 1) + sampleColumn] = nodes.Count;
                    nodes.Add(new Node { P = p, Cost = price, Exposure = seen, Row = sampleRow, Column = sampleColumn });
                }
            }
            if (++sampleColumn > Sides * 2) { sampleColumn = 0; sampleRow++; }
        }

        private void BeginSearch()
        {
            int n = nodes.Count;
            cost = new float[n]; came = new int[n]; closed = new bool[n];
            for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; came[i] = -1; }
            cost[start] = 0;
            Push(Remaining(start), start);
            stage = Stage.Searching;
        }

        private float Remaining(int node) => Flat(nodes[node].P, nodes[goal].P) * CheapestMetre;

        private void Push(float priority, int node)
        {
            open.Add(new KeyValuePair<float, int>(priority, node));
            int i = open.Count - 1;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (open[up].Key <= open[i].Key) break;
                KeyValuePair<float, int> swap = open[up]; open[up] = open[i]; open[i] = swap;
                i = up;
            }
        }

        private int Pop()
        {
            int top = open[0].Value, end = open.Count - 1;
            open[0] = open[end]; open.RemoveAt(end);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, least = i;
                if (l < end && open[l].Key < open[least].Key) least = l;
                if (r < end && open[r].Key < open[least].Key) least = r;
                if (least == i) break;
                KeyValuePair<float, int> swap = open[least]; open[least] = open[i]; open[i] = swap;
                i = least;
            }
            return top;
        }

        private void SearchOne()
        {
            if (open.Count == 0) { Finish(Plain); return; }   // no way through the band: the plain path it is
            int at = Pop();
            if (closed[at]) return;
            closed[at] = true;
            if (at == goal) { Collect(); return; }
            Node here = nodes[at];
            for (int dr = -2; dr <= 2; dr++)
            {
                int row = here.Row + dr;
                if (row < 0 || row >= rows) continue;
                for (int dc = -2; dc <= 2; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    // Next rows for the most part; the second row out only straight on, to step over a gap.
                    if (Mathf.Abs(dr) == 2 && dc != 0) continue;
                    int column = here.Column + dc;
                    if (column < 0 || column > Sides * 2) continue;
                    int next = grid[row * (Sides * 2 + 1) + column];
                    if (next < 0 || closed[next]) continue;
                    Node there = nodes[next];
                    float length = Vector3.Distance(here.P, there.P);
                    float through = cost[at] + length * (here.Cost + there.Cost) * .5f;
                    if (through >= cost[next] || !world.CanWalk(here.P, there.P)) continue;
                    cost[next] = through; came[next] = at;
                    Push(through + Remaining(next), next);
                }
            }
        }

        private void Collect()
        {
            found.Clear();
            for (int at = goal; at >= 0; at = came[at]) found.Add(at);
            found.Reverse();
            smoothed.Clear(); smoothed.Add(nodes[found[0]].P);
            smoothAt = 0;
            stage = Stage.Smoothing;
        }

        // Straightens the route where that doesn't mean walking somewhere it can be seen and it wasn't before.
        private void SmoothOne()
        {
            int last = found.Count - 1;
            if (smoothAt >= last) { Finish(smoothed.ToArray()); return; }
            int reach = Mathf.Min(smoothAt + 5, last);
            Node from = nodes[found[smoothAt]];
            int best = smoothAt + 1;
            for (int k = reach; k > smoothAt + 1; k--)
            {
                Node to = nodes[found[k]];
                float worst = 0;
                for (int i = smoothAt; i <= k; i++) worst = Mathf.Max(worst, nodes[found[i]].Exposure);
                if (world.CanWalk(from.P, to.P) && Exposure(world, Vector3.Lerp(from.P, to.P, .5f), eyes) <= worst + .05f) { best = k; break; }
            }
            smoothed.Add(nodes[found[best]].P);
            smoothAt = best;
        }

        private void Finish(Vector3[] route)
        {
            Points = route;
            stage = Stage.Done;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0;
            return Vector3.Distance(a, b);
        }
    }

    // Bullets from players and AI reach any BallisticCollider on the HitCollider layer; Body material gives the usual blood impact.
    internal sealed class DogHitbox : BallisticCollider
    {
        private Companion owner;
        private float multiplier;

        internal void Init(Companion companion, float damageMultiplier)
        {
            owner = companion; multiplier = damageMultiplier;
            TypeOfMaterial = MaterialType.Body;
            PenetrationChance = 0; RicochetChance = 0; FragmentationChance = 0; TrajectoryDeviationChance = 0;
        }

        public override PlayerHitInfo ApplyHit(DamageInfo damageInfo, ShotId shotID)
        {
            base.ApplyHit(damageInfo, shotID);
            if (owner != null) owner.TakeHit(damageInfo, multiplier);
            return null;
        }
    }

    /// <summary>
    /// What each jacket tier does for the dog in a raid. The jackets are containers, not the game's armour items, so the
    /// protection is applied by the dog's own hit handling (Companion.TakeHit): rounds that hit the chest or back are
    /// stopped or let through EFT-style, by armour class against the round's penetration, and wear the jacket down. Heavier
    /// tiers slow the dog. Same tiers as the server mod's gear list (sizes, weights and prices live there).
    /// </summary>
    internal static class DogArmour
    {
        internal sealed class Tier
        {
            public int Class; public float Speed, Durability;
            public Tier(int armourClass, float speed, float durability) { Class = armourClass; Speed = speed; Durability = durability; }
        }

        private static readonly Dictionary<string, Tier> Tiers = new Dictionary<string, Tier>
        {
            ["6a5d0c0c4fb9a3c5e1d0d061"] = new Tier(0, 1.00f, 0),    // tactical chest rig (black)
            ["6a5d0c0c4fb9a3c5e1d0d073"] = new Tier(3, .95f, 40),    // pocket vest (black)
            ["6a5d0c0c4fb9a3c5e1d0d071"] = new Tier(4, .91f, 50),    // tactical vest (olive)
            ["6a5d0c0c4fb9a3c5e1d60020"] = new Tier(5, .93f, 75),    // elite assault vest (camo): heavy protection, light build
            ["6a5d0c0c4fb9a3c5e1d60021"] = new Tier(4, .92f, 55),    // camera vest (coyote): full length, own camera
            // Taken out in 0.20 (the server swaps them for the vests above at game start); as they were till then.
            ["6a5d0c0c4fb9a3c5e1d0d074"] = new Tier(2, .98f, 30),
            ["6a5d0c0c4fb9a3c5e1d0d072"] = new Tier(5, .86f, 60),
        };

        // Repairs at the kennel (Working Dog Station and up): durability one item of each material puts back. Fabric
        // jackets (class 2-3) take any of them; the armoured tiers (class 4-5) only aramid or Cordura. Every repair costs
        // the jacket a little of its maximum (a tenth of what was put back), as armour repairs do in the game.
        internal static readonly Dictionary<string, float> RepairMaterials = new Dictionary<string, float>
        {
            ["5e2af4d286f7746d4159f07a"] = 25,   // Aramid fiber fabric
            ["5e2af41e86f774755a234b67"] = 15,   // Cordura polyamide fabric
            ["5e2af4a786f7746d3f3c3400"] = 10,   // Ripstop fabric
            ["5e2af29386f7746d4159f077"] = 6,    // KEKTAPE duct tape
        };
        internal const float RepairWear = .1f;

        internal static bool Repairs(Tier tier, string material)
            => tier != null && tier.Class > 0 && RepairMaterials.ContainsKey(material)
               && (tier.Class <= 3 || material == "5e2af4d286f7746d4159f07a" || material == "5e2af41e86f774755a234b67");

        internal static Tier Of(Item jacket) => jacket != null && Tiers.TryGetValue(jacket.TemplateId.ToString(), out Tier t) ? t : null;

        /// <summary>
        /// The jacket's durability as the game shows it (inspect window, item tooltip), set to the dog's wear on it: the
        /// game's own copy of the item only knows its template's full value.
        /// </summary>
        internal static void ShowWear(Item jacket)
        {
            Tier tier = Of(jacket);
            RepairableComponent repairable = (jacket as Vest)?.Repairable;
            if (tier == null || tier.Class <= 0 || repairable == null) return;
            K9.Wear wear = K9.WearOf(jacket.Id.ToString(), tier.Durability);
            repairable.MaxDurability = wear.Max;
            repairable.Durability = wear.Now;
        }

        /// <summary>
        /// Chance a round gets through: certain well above the armour, unlikely well below it, about 60% when evenly
        /// matched; worn armour counts for less (down to half its class at zero durability).
        /// </summary>
        internal static float PenetrationChance(int armourClass, float durability, float maxDurability, float penetration)
        {
            if (armourClass <= 0 || maxDurability <= 0 || durability <= 0) return 1;
            float armour = armourClass * 10f * (.5f + .5f * durability / maxDurability);
            float margin = (penetration - armour) / 15f;
            return margin >= 0 ? .6f + .4f * Clamp01(margin) : .6f * (1 - Clamp01(-margin));
        }

        // Durability lost to one round: its penetration scaled by its armour-damage rating (as a percentage).
        internal static float Wear(float penetration, float armourDamage)
            => System.Math.Max(1f, penetration * (armourDamage > 0 ? armourDamage / 100f : .5f) * .5f);

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
