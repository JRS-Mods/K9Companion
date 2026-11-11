using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using EFT.Hideout;
using EFT;
using UnityEngine.AI;
using UnityEngine;

namespace K9Companion
{
    public sealed partial class Companion : MonoBehaviour, IDogTarget
    {
        private enum Stance { Stand, SitDown, Sitting, StandUp, Turn, LieDown, Lying, LieUp, SleepDown, Sleeping, Wake }

        private const float Accel = 8f, SitAfter = 6f, LookAhead = 1.2f, TurnPlayback = 1.3f, HurriedTurn = 2.2f;
        // How hard it brakes for the spot it's going to (m/s2): usually, and at the end of a rush.
        private const float Braking = 6f, HardBraking = 16f;

        private static readonly float[] TurnL =
            { 0, .061f, .193f, .319f, .409f, .491f, .569f, .646f, .730f, .820f, .900f, .954f, .979f, .991f, .998f, 1, 1, 1, 1, 1, 1 };
        private static readonly float[] TurnR =
            { 0, .093f, .278f, .416f, .508f, .588f, .662f, .734f, .810f, .900f, .973f, .992f, .998f, 1, 1, 1, 1, 1, 1, 1, 1 };

        private GameWorld world;
        private HideoutPlayer hideoutOwner;
        private Player CurrentPlayer => hideoutOwner != null ? hideoutOwner : world != null ? world.MainPlayer : null;
        private bool IsHideout => hideoutOwner != null;
        internal void BindHideout(HideoutPlayer owner) { hideoutOwner = owner; boundHideout = true; nextTry = Time.time + 1; }
        internal HideoutPlayer HideoutOwner => hideoutOwner;
        private bool boundHideout, wasShown;
        private static HideoutScreenRear hideoutScreen;
        private static float nextScreenLook;

        // The hideout is on screen: walking round it, or its overview (where the player is switched off).
        private bool HideoutShown()
        {
            if (hideoutOwner.gameObject.activeInHierarchy) return true;
            // In a raid the hideout can't be on screen: no looking (a search of the whole raid scene, over and over, is a
            // stall the hideout dog would cause from the background).
            if (Comfort.Common.Singleton<GameWorld>.Instantiated && !(Comfort.Common.Singleton<GameWorld>.Instance is HideoutGameWorld)) return false;
            // Found once, switched off or not, and kept: looking only for an active one searched everything loaded every
            // couple of seconds for as long as you were in the menus.
            if (hideoutScreen == null && Time.unscaledTime >= nextScreenLook)
            { nextScreenLook = Time.unscaledTime + 2f; hideoutScreen = FindObjectOfType<HideoutScreenRear>(true); }
            return hideoutScreen != null && hideoutScreen.isActiveAndEnabled;
        }

        private static Vector3 HideoutStart(Vector3 owner)
        {
            var saved = NeedsStore.Current;
            if (saved != null && saved.HomeSet)
            {
                var home = new Vector3(saved.HomeX, saved.HomeY, saved.HomeZ);
                // Not a spot out in the void (where 1.3.1 could put the kennel); SetUpKennel finds it a new one.
                if (saved.HomeByHand || InsideHideout(home)) return home;
            }
            if (KennelRoom.Prepare(out Vector3 corner, out _)) return corner;
            // No home and no room yet: the Rest Space's pet bed, which is always on the hideout's floor. Your character
            // isn't: on the overview it's switched off and parked where there may be no floor, so the dog never appeared
            // (and with it the kennel's paw icon, which stands over the dog until the kennel is built) until you'd walked
            // around in the hideout.
            return HideoutProps.Where(HideoutProps.Bed, out Vector3 bed) ? bed : owner;
        }

        // In the hideout the dog and kennel show with the hideout (overview included) and hide while you're elsewhere.
        private bool HideoutVisible()
        {
            if (!boundHideout) return true;
            if (hideoutOwner == null) { Cleanup(); Destroy(gameObject); return false; }
            bool shown = HideoutShown();
            if (shown != wasShown) { wasShown = shown; Plugin.Log.LogInfo(shown ? $"Hideout on screen ({(hideoutOwner.gameObject.activeInHierarchy ? "walking" : "overview")}): dog shown." : "Hideout off screen: dog hidden."); }
            if (dog != null && dog.activeSelf != shown) dog.SetActive(shown);
            if (decor?.Home != null && decor.Home.gameObject.activeSelf != shown) decor.Home.gameObject.SetActive(shown);
            if (!shown) KennelIcon.Close();
            return shown;
        }

        private GameObject dog;
        private DogModel model;
        private PreparedDog prepared;
        private readonly NavMeshPath path = new NavMeshPath();
        private Vector3[] corners = Array.Empty<Vector3>();
        private int corner, groundMask, band;
        private float nextTry, deadline, nextPath, pathExpires, lastProgress, speed, shownSpeed, idle;
        private float yaw, yawRate, lean, headingError, turnFrom, turnAngle;
        private string turnClip;
        private Gait gait = Gait.Walk;
        // How winded it is (0-1, from running), and when it may shake itself again.
        private float exertion, nextShake;
        private Vector3 progressPoint, pathTarget;
        private bool failed, following;
        private Stance stance;

        private void Awake()
        {
            world = GetComponent<GameWorld>();
            // Out with your character: it's been made ready during the loading screen (DogPreload).
            nextTry = Time.time + .1f;
            deadline = Time.time + 90;
            groundMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
            if (groundMask == 0) groundMask = Physics.DefaultRaycastLayers;
        }

        private void Update()
        {
            if (failed || !HideoutVisible()) return;
            try { Tick(Time.deltaTime); }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError("Companion stopped safely: " + e);
                Cleanup();
            }
        }

        private void Tick(float dt)
        {
            Player player = CurrentPlayer;
            if (!Plugin.Active.Value || (player != null && player.HealthController != null && !player.HealthController.IsAlive))
            { Cleanup(); failed = true; return; }
            if (player == null)
            {
                if (dog != null) { Cleanup(); failed = true; }
                else if (Time.time > deadline) failed = true;
                return;
            }

            bool hideout = IsHideout;
            Vector3 ownerPosition = player.Transform.position;
            Vector3 target = ownerPosition;
            crouching = player.IsInPronePose || player.PoseLevel < 0.6f;
            Vector3 velocity = player.Velocity; velocity.y = 0;
            ownerSpeed = velocity.magnitude;
            Vector3 point;
            if (dog == null)
            {
                if (Time.time < nextTry || (!hideout && DogPreload.Busy && Time.time < deadline)) return;
                nextTry = Time.time + 2;
                // In the hideout the dog starts at its kennel: on the overview the player is switched off and parked
                // where there may be no floor.
                if (hideout) target = HideoutStart(ownerPosition);
                if (FindPoint(target, out point)) { Spawn(point, target); InitFeatures(player); }
                else if (Time.time > deadline && !hideout)
                { failed = true; Plugin.Log.LogWarning("No nearby navigation surface: dog cannot spawn in this raid."); }
                return;
            }

            if (TickCombat(dt, player)) return;
            if (!RenameDialog.IsOpen && !player.IsInventoryOpened && Cursor.lockState == CursorLockMode.Locked) Commands(player, hideout);
            TickFeatures(dt, player, hideout);
            if (TickTreatment(dt, player)) return;
            if (TickAction(dt, player)) return;
            if (TickBite(dt)) return;
            if (TickMeal(dt)) return;
            TickSniff(player);
            TickRoam();
            Vector3 start = dog.transform.position;
            Tuning tuning = Plugin.Tuning;
            // A hungry or thirsty dog in combat mode is slower; NeedsFactor is 1 otherwise.
            float fitness = NeedsFactor * jacketSpeed;
            tuning.Walk *= fitness; tuning.Run *= fitness; tuning.Sprint *= fitness;
            bool toDish = MealWalking(out Vector3 dish);
            Vector3 find = default;
            // A hunt: to the spot, at a run.
            bool toFind = !toDish && HuntWalking(player, out find);
            Vector3 bed = default;
            bool toBed = !toDish && !toFind && BedWalking(out bed);
            Vector3 roam = default;
            bool toRoam = !toDish && !toFind && !toBed && RoamWalking(out roam);
            // An attack sends the dog to the enemy at a run, then back to its usual spot beside you.
            bool errand = HasTask || toFind;
            // Its own trip somewhere (not keeping up with you): it brakes for the end of it, and owner's pace doesn't come into it.
            bool trip = errand || toDish || toBed || toRoam;
            if (attackTarget != null) { target = attackTarget.Transform.position; tuning.Follow = ChaseStop; }
            else if (toDish) { target = dish; tuning.Follow = BowlReach * .7f; }
            else if (toFind) { target = find; tuning.Follow = .8f; }
            else if (toBed) { target = bed; tuning.Follow = .2f; }
            else if (toRoam) { target = roam; tuning.Follow = .3f; }
            float distance = Vector3.Distance(start, target);
            float ownerDistance = Vector3.Distance(start, ownerPosition);
            float stop = tuning.Follow;
            band = Locomotion.Band(distance, band, tuning);
            if (errand) band = Mathf.Max(band, 1);
            else if (hideout && (toRoam || toBed || toDish)) band = 0;   // pottering about at home: a walk
            if (rushing) band = 2;
            if (distance > stop + (ownerSpeed > 0.15f ? 0.3f : 0.8f)) following = true;
            // While the owner keeps moving the dog stays in step instead of stopping and restarting at the stop radius.
            else if (distance <= stop + .05f && (ownerSpeed < .15f || trip)) following = false;

            if (!toDish && !toFind && !toBed && !toRoam && (command == Command.Stay || command == Command.Sit || command == Command.Down)) following = false;
            // Not while it's off on your order (a fight or a hunt, maybe far away): the fight's own rules bring it back.
            // Nor while it runs back from one (up to a minute): it comes home on its own legs.
            if (ownerDistance < 6) homeUntil = 0;
            bool away = (attackTarget != null && brain.Ordered) || hunt != null || Time.time < homeUntil;
            bool lost = command == Command.Follow && !away && (ownerDistance > Mathf.Max(45, rules.EngageDistance * 2 + 15) || (following && Time.time - lastProgress > 8));
            if (lost && Time.time >= nextTry)
            {
                nextTry = Time.time + 3;
                // Reappear behind the owner's view rather than in front of it.
                if (FindPoint(target, out point, -player.LookDirection)) Teleport(point, target);
                return;
            }

            if (crouching && command == Command.Follow) WakeUp();
            if (following) { idle = 0; WakeUp(); }
            else
            {
                idle += dt;
                if ((command == Command.Down || (hideout && idle > 18 && !crouching && command != Command.Sit)) && stance == Stance.Sitting) stance = Stance.StandUp;
                if (stance == Stance.Stand && shownSpeed < .15f)
                {
                    if (command == Command.Down || (hideout && idle > 18 && command != Command.Sit)) stance = Stance.LieDown;
                    else if (command == Command.Sit || (idle > SitAfter && !crouching)) stance = Stance.SitDown;
                }
                if (hideout && !crouching && idle > 40 && stance == Stance.Lying) stance = Stance.SleepDown;
            }

            // A stalk or the run out to a hunt follows its own route at its own pace.
            float pace = attackTarget != null ? TickStalk(start, tuning) : toFind ? TickHuntRoute(start, find, tuning) : -1;
            float desired = 0;
            if (following && stance == Stance.Stand)
            {
                if (pace >= 0) desired = pace;
                else
                {
                    // Only re-plan when the owner has moved off the planned end, the plan ran out, or it has gone stale.
                    if (Time.time >= nextPath && (corner >= corners.Length || (target - pathTarget).sqrMagnitude > 1 || Time.time >= pathExpires))
                        RefreshPath(target);
                    desired = Locomotion.DesiredSpeed(distance, errand ? 0 : ownerSpeed, crouching && !errand, band, tuning);
                    // Searching round a hunted spot it trots, nose down, and doesn't tear about.
                    if (toFind) desired = Mathf.Min(desired, huntArrived > 0 ? Locomotion.Speed(Gait.Trot, tuning) : Locomotion.Speed(Gait.Run, tuning) * .8f);
                }
                if (trip && desired > 0)
                {
                    // Runs up to where it's going and stops: never a standing start at full speed or a dead stop from it.
                    float left = Mathf.Max(0, (corner < corners.Length ? PathLeft(start) : distance) - stop);
                    float brake = Mathf.Sqrt(2 * (rushing ? HardBraking : Braking) * left);
                    desired = left <= 0 ? 0 : Mathf.Min(desired, Mathf.Max(brake, Locomotion.Speed(Gait.WalkSlow, tuning) * .9f));
                }
                // (Not in a rush: it swings round on the move.)
                if (desired > 0 && !rushing && speed < 0.3f && corners.Length > 0 && TryTurn(Ahead(start) - start, errand ? 65 : 100)) desired = 0;
            }
            else if (stance == Stance.Stand && !following && speed < 0.05f && idle > 0.5f)
                TryTurn(target - start, 70);

            if (stance == Stance.Stand) Steer(start, dt);
            // Slower through a turn (it runs the way it faces, so a tight turn at speed would swing it wide).
            desired *= Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(20, 100, Mathf.Abs(headingError)));
            speed = Mathf.MoveTowards(speed, desired, (desired < speed && (!following || trip) ? 3 : 1) * Accel * dt);
            if (stance != Stance.Stand) speed = 0;
            if (speed > 0.01f) Move(Mathf.Min(speed * dt, Mathf.Max(0, distance - stop)));

            Vector3 moved = dog.transform.position - start; moved.y = 0;
            shownSpeed = Mathf.Lerp(shownSpeed, dt > 0 ? moved.magnitude / dt : 0, Mathf.Min(1, 10 * dt));
            if (!following || stance != Stance.Stand || Vector3.Distance(progressPoint, dog.transform.position) > 0.5f)
            { progressPoint = dog.transform.position; lastProgress = Time.time; }

            TickBody(dog.transform.position, dt);
            TickLook(dog.transform.position, player);
            Animate(dt, tuning);
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
        }

        private void Animate(float dt) => Animate(dt, Plugin.Tuning);

        private void Animate(float dt, Tuning tuning)
        {
            switch (stance)
            {
                case Stance.SitDown:
                    if (model.Tick("Trans_Stand_To_Sitting", 1, dt, false)) stance = Stance.Sitting;
                    return;
                case Stance.Sitting:
                    model.Tick(IdleClip("Sitting", idle), 1, dt);
                    return;
                case Stance.StandUp:
                    if (model.Tick("Trans_Sitting_To_Stand", 1.5f, dt, false)) stance = Stance.Stand;
                    return;
                case Stance.LieDown:
                    if (model.Tick("Trans_Stand_To_Lying", 1, dt, false)) stance = Stance.Lying;
                    return;
                case Stance.Lying: model.Tick(IdleClip("Lying", idle), 1, dt); return;
                case Stance.LieUp:
                    if (model.Tick("Trans_Lying_To_Stand", 1.3f, dt, false))
                    {
                        stance = Stance.Stand;
                        // Up from lying, a dog often shakes itself off.
                        if (Time.time > nextShake && UnityEngine.Random.value < .6f) { audio?.Play("shake"); nextShake = Time.time + 30; }
                    }
                    return;
                case Stance.SleepDown:
                    if (model.Tick("Trans_Lying_to_Sleeping", 1, dt, false)) stance = Stance.Sleeping;
                    return;
                case Stance.Sleeping: model.Tick("Sleeping_01", 1, dt); return;
                case Stance.Wake:
                    if (model.Tick("Trans_Sleeping_to_Lying", 1.3f, dt, false)) stance = Stance.LieUp;
                    return;
                case Stance.Turn:
                    bool done = model.Tick(turnClip, HasTask || hunt != null ? HurriedTurn : TurnPlayback, dt, false);
                    yaw = turnFrom + turnAngle * Curve(turnClip == "Trans_TurnL90" ? TurnL : TurnR, done ? 1 : model.Progress);
                    if (done) { stance = Stance.Stand; yawRate = 0; }
                    return;
            }
            float s = shownSpeed;
            lean = Mathf.Lerp(lean, Mathf.Clamp(yawRate / 90f, -1, 1), Mathf.Min(1, 6 * dt));
            if (s < 0.15f) { model.Tick(crouching || stalkLow || Sniffing ? "Stand_Sneak_00" : IdleClip("Stand", idle), 1, dt); return; }
            // Gait and playback come from the distance actually covered, so feet match the ground.
            gait = Locomotion.Pick(s, gait, stalkLow || (crouching && !HasTask), tuning);
            string clip = Locomotion.Clip(gait);
            model.TickGait(clip, clip + (lean < 0 ? "_L" : "_R"), Mathf.Abs(lean), Locomotion.Playback(gait, s), dt);
        }

        private bool TryTurn(Vector3 dir, float threshold)
        {
            dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return false;
            float delta = Mathf.DeltaAngle(yaw, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
            if (Mathf.Abs(delta) < threshold) return false;
            turnClip = delta < 0 ? "Trans_TurnL90" : "Trans_TurnR90";
            turnFrom = yaw; turnAngle = Mathf.Clamp(delta, -150, 150);
            stance = Stance.Turn; speed = 0; yawRate = 0; lean = 0;
            return true;
        }

        private static float Curve(float[] c, float t)
        {
            float f = Mathf.Clamp01(t) * (c.Length - 1);
            int i = Mathf.Min((int)f, c.Length - 2);
            return Mathf.Lerp(c[i], c[i + 1], f - i);
        }

        private void Steer(Vector3 pos, float dt)
        {
            Vector3 dir = Ahead(pos) - pos; dir.y = 0;
            float targetRate = 0;
            headingError = 0;
            if (speed > 0.05f && dir.sqrMagnitude > 0.0004f)
            {
                headingError = Mathf.DeltaAngle(yaw, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
                float maxRate = Mathf.Lerp(140, 240, speed / 8f);
                targetRate = Mathf.Clamp(headingError * 3.5f, -maxRate, maxRate);
            }
            yawRate = Mathf.MoveTowards(yawRate, targetRate, 480 * dt);
            yaw += yawRate * dt;
        }

        private Vector3 Ahead(Vector3 pos)
        {
            Vector3 p = pos;
            float left = Mathf.Clamp(speed * .3f, LookAhead, 3.5f);
            for (int i = corner; i < corners.Length; i++)
            {
                Vector3 seg = corners[i] - p; seg.y = 0;
                float len = seg.magnitude;
                if (len >= left) return p + seg / len * left;
                left -= len;
                p = new Vector3(corners[i].x, pos.y, corners[i].z);
            }
            return p;
        }

        private void RefreshPath(Vector3 target)
        {
            nextPath = Time.time + 0.3f; pathExpires = Time.time + 1.5f; pathTarget = target;
            corners = Array.Empty<Vector3>(); corner = 0;
            if (WalkingWithoutNavmesh)
            {
                // Hideout: straight legs over clear floor, planned less often (it's more physics work than a navmesh).
                nextPath = Time.time + 1f; pathExpires = Time.time + 3f;
                if (HideoutRoute(dog.transform.position, target, out Vector3[] route)) { corners = route; corner = 1; }
                return;
            }
            legsRoute = false;
            if (NavMesh.SamplePosition(dog.transform.position, out NavMeshHit from, 1.5f, NavMesh.AllAreas)
                && NavMesh.SamplePosition(target, out NavMeshHit to, 2f, NavMesh.AllAreas)
                && NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete && path.corners.Length > 1)
            { corners = path.corners; corner = 1; return; }
            // The bots' navmesh doesn't reach everywhere you can walk (upstairs in some buildings, small rooms): close by,
            // the dog goes over the floor itself, in straight legs as at home.
            if (Flat(dog.transform.position, target) < OffMeshReach && Mathf.Abs(dog.transform.position.y - target.y) < 6
                && HideoutRoute(dog.transform.position, target, out Vector3[] legs))
            { corners = legs; corner = 1; legsRoute = true; nextPath = Time.time + .8f; pathExpires = Time.time + 2f; }
        }

        private const float OffMeshReach = 20f;
        private bool legsRoute;   // following a floor route off the navmesh (raid)

        // A dog runs the way it faces. Steer turns it towards a point a little way along the path, so it starts round a
        // corner before reaching it; it moves along that heading, cutting the corner as an animal does. (Moving to each
        // corner point exactly while facing the next leg made it slide sideways round every corner: at a sprint, round
        // a hunt's loop, it glided.) Where that heading would leave the walkable ground, it keeps to the path itself; so
        // it does for the last stretch to where it stops, and in the hideout, whose straight legs are cleared of
        // furniture.
        private void Move(float step)
        {
            Vector3 pos = dog.transform.position;
            while (corner < corners.Length - 1 && Flat(pos, corners[corner]) < Mathf.Max(.35f, speed * .12f)) corner++;
            if (WalkingWithoutNavmesh || legsRoute)
            {
                MoveAlongPath(step);
                return;
            }
            if (corner >= corners.Length) return;
            Vector3 toCorner = corners[corner] - pos; toCorner.y = 0;
            float left = toCorner.magnitude;
            bool last = corner == corners.Length - 1;
            if (left < .05f) { if (last) corner++; return; }
            if (last && left < 1.5f) { MoveAlongPath(step); return; }
            Vector3 heading = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            if (Vector3.Angle(heading, toCorner) > 70) { MoveAlongPath(step); return; }   // facing well off (a tight turn): keep to the path
            Vector3 next = pos + heading * step;
            if (NavMesh.Raycast(pos, next, out _, NavMesh.AllAreas)) { MoveAlongPath(step); return; }
            Place(next, true);
        }

        // Straight along the path, corner to corner.
        private void MoveAlongPath(float step)
        {
            while (step > 0.0001f && corner < corners.Length)
            {
                Vector3 pos = dog.transform.position;
                Vector3 flat = corners[corner] - pos; flat.y = 0;
                float len = flat.magnitude;
                if (len < 0.05f) { corner++; continue; }
                float d = Mathf.Min(step, len);
                Vector3 next = pos + flat / len * d;
                next.y = Mathf.Lerp(pos.y, corners[corner].y, d / len);
                Place(next, true);
                step -= d;
            }
        }

        // Walking, the body rides over steps and rubble rather than jumping to each new height (it went up stairs in jerks).
        private void Place(Vector3 p, bool walking = false)
        {
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 1.5f, groundMask, QueryTriggerInteraction.Ignore))
                p.y = hit.point.y;
            // On its kennel bed (raised on the dog house's or the military kennel's floor): on top of it.
            if (decor != null && decor.OnBed(p, out float bed)) p.y = Mathf.Max(p.y, bed);
            if (walking && !IsHideout)
            {
                float rise = p.y - dog.transform.position.y;
                if (Mathf.Abs(rise) < .7f) p.y = dog.transform.position.y + rise * (1 - Mathf.Exp(-(14 + 3 * speed) * Time.deltaTime));
            }
            dog.transform.position = p;
        }

        private bool FindPoint(Vector3 around, out Vector3 point, Vector3 prefer = default)
        {
            if (IsHideout) return FindHideoutPoint(around, out point);
            float first = prefer.x * prefer.x + prefer.z * prefer.z > .01f ? Mathf.Atan2(prefer.x, prefer.z) * Mathf.Rad2Deg : 0;
            for (int i = 0; i < 12; i++)
            {
                // Preferred side first, then fanning out either side of it.
                float angle = (first + (i + 1) / 2 * 30 * (i % 2 == 0 ? 1 : -1)) * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * 2;
                if (NavMesh.SamplePosition(around + offset, out NavMeshHit hit, 1.8f, NavMesh.AllAreas)
                    && Mathf.Abs(hit.position.y - around.y) < 1.5f)
                { point = hit.position; return true; }
            }
            // No navmesh near you (it doesn't cover every floor you can walk on): clear floor beside you will do.
            return FloorPoint(around, groundMask, out point);
        }

        private bool FindHideoutPoint(Vector3 around, out Vector3 point)
        {
            // Hideout floor colliders are not necessarily on the raid terrain layers.
            groundMask = Physics.DefaultRaycastLayers;
            if (!FloorPoint(around, groundMask, out point)) return false;
            navigationAvailable = NavMesh.SamplePosition(point, out NavMeshHit nav, .5f, NavMesh.AllAreas);
            return true;
        }

        // Level floor beside 'around' (or under it), in clear sight of it at a dog's height.
        private static bool FloorPoint(Vector3 around, int groundMask, out Vector3 point)
        {
            for (int i = 0; i < 13; i++)
            {
                float angle = i * 30 * Mathf.Deg2Rad;
                Vector3 offset = i == 12 ? Vector3.zero : new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * 1.4f;
                Vector3 origin = around + offset + Vector3.up * 1.2f;
                var hits = Physics.RaycastAll(origin, Vector3.down, 4, groundMask, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a,b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    if (hit.normal.y < .65f || hit.collider.GetComponentInParent<Player>() != null || hit.point.y > around.y + .5f) continue;
                    if (offset.sqrMagnitude > .01f && Physics.Linecast(around + Vector3.up * .35f, hit.point + Vector3.up * .35f, groundMask, QueryTriggerInteraction.Ignore)) continue;
                    point = hit.point;
                    return true;
                }
            }
            point = Vector3.zero;
            return false;
        }

        private void Spawn(Vector3 point, Vector3 target)
        {
            // A hideout dog is its own: one made ready for a raid that didn't happen isn't needed.
            if (IsHideout) DogPreload.Release();
            prepared = IsHideout ? null : DogPreload.Take();
            dog = prepared?.Dog ?? new GameObject("K9 Companion dog");
            if (!IsHideout) dog.transform.SetParent(world.transform, true);
            else UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(dog, gameObject.scene);
            dog.transform.position = point;
            model = prepared?.Model ?? DogModel.Load(Plugin.AssetFolder, dog.transform, Plugin.Scale.Value);
            Teleport(point, target);
            dog.SetActive(true);
            Plugin.Log.LogInfo("K9 dog spawned" + (prepared != null ? " (ready since the loading screen)." : "."));
        }

        private void Teleport(Vector3 point, Vector3 target)
        {
            Place(point);
            Vector3 look = target - point;
            if (look.x * look.x + look.z * look.z > 0.01f) yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            if (stance == Stance.Turn) stance = Stance.Stand;
            corners = Array.Empty<Vector3>(); corner = 0; speed = 0; shownSpeed = 0; yawRate = 0; lean = 0;
            progressPoint = point; lastProgress = Time.time; nextPath = 0;
        }

        private void Cleanup()
        {
            CleanupFeatures();
            KennelIcon.Close(); KennelRoom.Restore();
            if (dog != null) Destroy(dog);
            model?.Dispose(); model = null; dog = null;
        }

        private void OnDisable() { if (RenameDialog.Owner == this) RenameDialog.Close(); if (dog != null) dog.SetActive(false); if (decor?.Home != null) decor.Home.gameObject.SetActive(false); }
        private void OnEnable() { if (dog != null) dog.SetActive(true); if (decor?.Home != null) decor.Home.gameObject.SetActive(true); }
        private void OnDestroy() { Cleanup(); }
    }

    internal enum Gait { Sneak, WalkSlow, Walk, Trot, Run, Sprint }

    internal struct Tuning { public float Walk, Run, Sprint, Follow, RunFrom, SprintFrom; }

    internal static class Locomotion
    {
        // Ground speed (m/s) each in-place clip was authored at, measured from the root-motion clips by tools/measure_gaits.py.
        private static readonly float[] Authored = { .264f, .408f, .753f, 2.884f, 8.01f, 11.348f };
        private static readonly string[] Clips = { "Loco_Sneak", "Loco_WalkSlow", "Loco_Walk", "Loco_Trot", "Loco_Run", "Loco_Sprint" };
        // The sneak's stride is long and its clip very slow: quickened it's a crouched stalk at a dog's normal cadence, and
        // the feet still match the ground, since playback follows the speed.
        private const float SneakStretch = 5.5f;

        private static float Clamp(float v, float min, float max) => Math.Max(min, Math.Min(max, v));
        public static string Clip(Gait g) => Clips[(int)g];
        public static float Speed(Gait g, Tuning t) => Authored[(int)g] * (g <= Gait.Walk ? t.Walk : g == Gait.Sprint ? t.Sprint : t.Run);
        // Feet stay planted when playback follows the real ground speed, multipliers included.
        public static float Playback(Gait g, float speed) => Clamp(speed / Authored[(int)g], .3f, g == Gait.Sneak ? SneakStretch + .3f : 2.5f);
        // The fastest it can go keeping low.
        public static float StalkSpeed(Tuning t) => Speed(Gait.Sneak, t) * SneakStretch * .9f;

        // 0 = near (walk/match pace), 1 = run, 2 = sprint. Hysteresis keeps a dog on a band edge from flickering.
        public static int Band(float distance, int current, Tuning t)
        {
            if (distance >= t.SprintFrom || (current == 2 && distance > t.SprintFrom - 3)) return 2;
            if (distance >= t.RunFrom || (current >= 1 && distance > t.RunFrom - 1.5f)) return 1;
            return 0;
        }

        public static float DesiredSpeed(float distance, float ownerSpeed, bool crouch, int band, Tuning t)
        {
            float gap = Math.Max(0, distance - t.Follow);
            float v = ownerSpeed + gap * .8f;
            if (band == 0)
            {
                // Close by: amble over, or hold the owner's pace without overshooting.
                float amble = crouch ? Speed(Gait.Sneak, t) * 2.5f : Speed(Gait.Walk, t) * 1.5f;
                v = Math.Min(v, Math.Max(ownerSpeed * 1.15f + .2f, amble));
                // Walk the last stretch in and stop, rather than creeping slower and slower.
                if (gap > .05f) v = Math.Max(v, Speed(Gait.WalkSlow, t) * .9f);
            }
            else v = Math.Max(v, band == 1 ? Speed(Gait.Run, t) * .75f : Speed(Gait.Sprint, t));
            return Math.Min(v, Speed(Gait.Sprint, t) * 1.1f);
        }

        public static Gait Pick(float speed, Gait current, bool crouch, Tuning t)
        {
            if (crouch && speed <= Speed(Gait.Sneak, t) * SneakStretch) return Gait.Sneak;
            Gait best = Gait.Walk;
            float bestScore = float.MaxValue;
            for (Gait g = Gait.WalkSlow; g <= Gait.Sprint; g++)
            {
                // Closest authored speed wins; the current gait gets a bonus so small speed changes don't swap clips.
                float score = Math.Abs((float)Math.Log(Math.Max(speed, .05f) / Speed(g, t))) - (g == current ? .1f : 0);
                if (score < bestScore) { best = g; bestScore = score; }
            }
            return best;
        }
    }

    internal sealed class CompanionAudio : IDisposable
    {
        // Barks (and your whistle) carry a long way; whining, shaking and so on only close by. Breathing has its own.
        private readonly AudioSource source, near, breath;
        // Each sound's takes (sounds/<name>.wav, or <name>_1.wav, <name>_2.wav ...: one is picked at random each time).
        private readonly Dictionary<string,List<AudioClip>> clips = new Dictionary<string,List<AudioClip>>();
        // Made up when there's no recording; the rest only play from their files.
        private static readonly string[] Synthesised={"whistle","bark","whine","happy"}, Recorded={"shake"};
        public CompanionAudio(GameObject dog)
        {
            source=dog.AddComponent<AudioSource>();source.playOnAwake=false;source.spatialBlend=1;
            source.rolloffMode=AudioRolloffMode.Linear;source.minDistance=1;source.maxDistance=Carries;
            near=dog.AddComponent<AudioSource>();near.playOnAwake=false;near.spatialBlend=1;
            near.rolloffMode=AudioRolloffMode.Linear;near.minDistance=1;near.maxDistance=18;
            // The breathing: one loop, always playing, heard only close by (Breathe sets how hard).
            string breathing=Path.Combine(Plugin.AssetFolder,"sounds","breathing.wav");
            if(File.Exists(breathing))
            {
                try
                {
                    AudioClip clip=LoadWav(breathing,"breathing");Level(clip);
                    breath=dog.AddComponent<AudioSource>();breath.playOnAwake=false;breath.loop=true;breath.clip=clip;breath.spatialBlend=1;
                    breath.rolloffMode=AudioRolloffMode.Linear;breath.minDistance=.6f;breath.maxDistance=7;breath.volume=0;
                    resources.Add(clip);
                }
                catch(Exception e) { Plugin.Log.LogWarning("Could not load "+breathing+": "+e.Message); }
            }
            foreach(string name in Synthesised.Concat(Recorded))
            {
                var takes=new List<AudioClip>();
                string folder=Path.Combine(Plugin.AssetFolder,"sounds");
                var paths=new List<string>{Path.Combine(folder,name+".wav")};
                for(int i=1;i<=9;i++) paths.Add(Path.Combine(folder,name+"_"+i+".wav"));
                foreach(string path in paths.Where(File.Exists))
                {
                    try { takes.Add(LoadWav(path,Path.GetFileNameWithoutExtension(path))); }
                    catch(Exception e) { Plugin.Log.LogWarning("Could not load "+path+": "+e.Message); }
                }
                if(takes.Count==0 && Synthesised.Contains(name)) takes.Add(Synth(name));
                // Made-up and recorded sounds all at one loudness (convert_sounds.py levels the recordings the same way).
                foreach(AudioClip take in takes) Level(take);
                if(takes.Count>0) clips[name]=takes;
            }
        }
        private readonly List<AudioClip> resources=new List<AudioClip>();
        private bool reported;

        // How hard the dog breathes now: 0 = not at all (dead, or sounds off), 1 = as loud as it gets; pitch also speeds it up.
        public void Breathe(float effort,float pitch)
        {
            if(breath==null) return;
            Route(breath);
            float volume=Plugin.Sounds.Value?effort*Plugin.Volume.Value*Plugin.BreathingVolume.Value:0;
            // Eased, so it rises and settles like breathing does rather than jumping.
            breath.volume=Mathf.MoveTowards(breath.volume,volume,Time.deltaTime*.25f);
            breath.pitch=Mathf.MoveTowards(breath.pitch,pitch,Time.deltaTime*.2f);
            if(breath.volume>0.0005f && !breath.isPlaying){breath.time=UnityEngine.Random.Range(0f,breath.clip.length*.9f);breath.Play();}
            else if(breath.volume<=0.0005f && breath.isPlaying) breath.Stop();
        }

        // How far off a bark can still be heard, in metres.
        private const float Carries=60f;
        // Heard by whoever else is about (Fika); not the whistle, which is yours.
        internal Action<string> Played;

        public void Play(string name,bool atListener=false)
        {
            if(!atListener) Played?.Invoke(name);
            if(!Plugin.Sounds.Value || !clips.TryGetValue(name,out List<AudioClip> takes)) return;
            AudioSource from=name=="bark"||atListener?source:near;
            Route(from);from.spatialBlend=atListener?0:1;from.PlayOneShot(takes[UnityEngine.Random.Range(0,takes.Count)],Plugin.Volume.Value);
            if(!reported){reported=true;Plugin.Log.LogInfo($"Dog sounds: {clips.Count} kinds loaded, first played '{name}' through {(from.outputAudioMixerGroup!=null?from.outputAudioMixerGroup.name:"no mixer group")}.");}
        }
        private const float Loudness=.12f, Ceiling=.95f;

        // Brings a clip to the shared loudness: measured over the part that sounds, its peaks kept under the ceiling.
        private static void Level(AudioClip clip)
        {
            var data=new float[clip.samples*clip.channels];
            if(data.Length==0 || !clip.GetData(data,0)) return;
            int step=Math.Max(1,clip.frequency/100*clip.channels);
            var blocks=new List<float>();float peak=0,loudest=0;
            for(int i=0;i<data.Length;i+=step)
            {
                float sum=0;int n=Math.Min(step,data.Length-i);
                for(int k=0;k<n;k++){float x=data[i+k];sum+=x*x;peak=Math.Max(peak,Math.Abs(x));}
                float rms=(float)Math.Sqrt(sum/n);blocks.Add(rms);loudest=Math.Max(loudest,rms);
            }
            var heard=blocks.Where(b=>b>loudest*.15f).ToList();
            if(heard.Count==0 || peak<=0) return;
            float level=(float)Math.Sqrt(heard.Sum(b=>b*b)/heard.Count);
            float gain=Math.Min(Loudness/Math.Max(level,1e-6f),Ceiling/peak);
            if(Math.Abs(gain-1)<.02f) return;
            for(int i=0;i<data.Length;i++) data[i]*=gain;
            clip.SetData(data,0);
        }

        private static AudioClip Synth(string name)
        {
            const int rate=22050;float duration=name=="whistle"?.8f:name=="whine"?1.3f:.4f;
            var samples=new float[(int)(rate*duration)];var noise=new System.Random(79);double phase=0;
            for(int i=0;i<samples.Length;i++)
            {
                float t=(float)i/rate,u=t/duration;
                float hz=name=="whistle"?1800+500*Mathf.Sin(u*Mathf.PI):name=="whine"?550+260*Mathf.Sin(u*Mathf.PI):name=="happy"?330+120*u:180-70*u;
                phase+=2*Math.PI*hz/rate;
                float envelope=Mathf.Sin(Mathf.PI*u)*(name=="whistle" && u>.42f && u<.55f?0:1);
                float tone=(float)Math.Sin(phase);
                if(name=="bark") tone=tone*.5f+(float)(noise.NextDouble()*2-1)*.5f;
                samples[i]=tone*envelope*(name=="bark"?.3f:.18f);
            }
            var clip=AudioClip.Create(name,samples.Length,1,rate,false);clip.SetData(samples,0);return clip;
        }
        private static AudioClip LoadWav(string path,string name)
        {
            using(var r=new BinaryReader(File.OpenRead(path)))
            {
                if(new string(r.ReadChars(4))!="RIFF") throw new InvalidDataException("Expected RIFF PCM WAV");
                r.ReadInt32();if(new string(r.ReadChars(4))!="WAVE") throw new InvalidDataException("Expected WAVE");
                int channels=0,rate=0,bits=0,format=0;byte[] data=null;
                while(r.BaseStream.Position+8<=r.BaseStream.Length)
                {
                    string id=new string(r.ReadChars(4));int size=r.ReadInt32();
                    if(size<0 || size>r.BaseStream.Length-r.BaseStream.Position) throw new InvalidDataException("Invalid WAV chunk");
                    long end=r.BaseStream.Position+size;
                    if(id=="fmt " && size>=16) { format=r.ReadUInt16();channels=r.ReadUInt16();rate=r.ReadInt32();r.ReadInt32();r.ReadUInt16();bits=r.ReadUInt16(); }
                    else if(id=="data") { if(size>10000000) throw new InvalidDataException("Sound too large"); data=r.ReadBytes(size); }
                    r.BaseStream.Position=end+(size%2);
                }
                if(format!=1 || bits!=16 || channels<1 || channels>2 || rate<8000 || rate>96000 || data==null || data.Length%(2*channels)!=0)
                    throw new InvalidDataException("Use 16-bit PCM mono/stereo WAV, 8-96 kHz");
                var samples=new float[data.Length/2];for(int i=0;i<samples.Length;i++) samples[i]=BitConverter.ToInt16(data,i*2)/32768f;
                var clip=AudioClip.Create(name,samples.Length/channels,channels,rate,false);clip.SetData(samples,0);return clip;
            }
        }
        // Through the game's own sound mixer, as its sounds are (its volume settings, its mixing); played straight to the
        // speakers, the dog was far louder than the world around it.
        private static void Route(AudioSource s)
        {
            if(s==null||s.outputAudioMixerGroup!=null||!Comfort.Common.Singleton<BetterAudio>.Instantiated) return;
            s.outputAudioMixerGroup=Comfort.Common.Singleton<BetterAudio>.Instance.VeryStandartMixerGroup;
        }

        public void Dispose() { if(breath!=null) UnityEngine.Object.Destroy(breath);foreach(var clip in resources) UnityEngine.Object.Destroy(clip);if(source!=null) UnityEngine.Object.Destroy(source);if(near!=null) UnityEngine.Object.Destroy(near);foreach(var takes in clips.Values) foreach(var clip in takes) UnityEngine.Object.Destroy(clip);clips.Clear(); }
    }
}
