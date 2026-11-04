using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.AI;

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
        private Player lastShooter;
        private float lastShotAt;
        private bool defending;

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
                if (under != null)
                {
                    if (rules.AutoEngage) { GoForShooter(under); return; }
                    Alert(under, owner);
                }
                Player found = FindEnemy(owner);
                if (found == null) return;
                // Passive: it only alerts you, and goes in when ordered (Over There).
                if (!rules.AutoEngage) { Alert(found, owner); return; }
                Engage(found, false);
                if (!stalking) audio?.Play("bark");
                Say(Plugin.DogName.Value + (stalking ? " is stalking " : " goes for ") + found.Profile.Nickname + ".");
                return;
            }
            if (!rules.CanAttack || command != Command.Follow) { StopFight(); return; }
            // Someone else opens up on you while it's on its way back, or chasing something it picked itself: them first. Never
            // over an order of yours, nor when it's about to get its teeth into what it's after.
            if (rules.AutoEngage && !Wounded && Time.time >= nextScan
                && (brain.State == CombatState.Return || (brain.State == CombatState.Chase && (!brain.Ordered || defending))))
            {
                nextScan = Time.time + .5f;
                Player under = Shooter(owner);
                if (under != null && under != attackTarget && DistanceTo(attackTarget) > SwitchFrom) { GoForShooter(under); return; }
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

        private Player FindEnemy(Player owner)
        {
            if (world == null) return null;
            Vector3 me = dog.transform.position, mine = owner.Transform.position;
            Player best = null; float bestDistance = float.MaxValue;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                if (ignored.TryGetValue(p.ProfileId, out float until) && Time.time < until) continue;
                Vector3 at = p.Transform.position;
                if (Vector3.Distance(at, mine) > rules.EngageDistance) continue;
                float d = Vector3.Distance(at, me);
                if (d >= bestDistance) continue;
                if (rules.Aggression == Aggression.Defensive && !Threatens(p, owner)) continue;
                // The dog notices what it can see, or smell and hear close by. Walls hide anything further away.
                if (d > ScentRange && !ClearLine(HeadPoint, Body(p))) continue;
                best = p; bestDistance = d;
            }
            return best != null && Reachable(best.Transform.position) ? best : null;
        }

        private bool Reachable(Vector3 point)
        {
            if (WalkingWithoutNavmesh) return HideoutRoute(dog.transform.position, point, out _);
            if (!NavMesh.SamplePosition(dog.transform.position, out NavMeshHit from, 1.5f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(point, out NavMeshHit to, 2f, NavMesh.AllAreas)) return false;
            var test = new NavMeshPath();
            return NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, test) && test.status == NavMeshPathStatus.PathComplete;
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
}
