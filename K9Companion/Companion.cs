using System;
using EFT;
using EFT.Hideout;
using UnityEngine;
using UnityEngine.AI;

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
            if (saved != null && saved.HomeSet) return new Vector3(saved.HomeX, saved.HomeY, saved.HomeZ);
            return KennelRoom.Prepare(out Vector3 corner, out _) ? corner : owner;
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
            if (!NavMesh.SamplePosition(dog.transform.position, out NavMeshHit from, 1.5f, NavMesh.AllAreas)) return;
            if (!NavMesh.SamplePosition(target, out NavMeshHit to, 2f, NavMesh.AllAreas)) return;
            if (NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete && path.corners.Length > 1)
            { corners = path.corners; corner = 1; }
        }

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
            if (WalkingWithoutNavmesh)
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
            point = Vector3.zero;
            return false;
        }

        private bool FindHideoutPoint(Vector3 around, out Vector3 point)
        {
            // Hideout floor colliders are not necessarily on the raid terrain layers.
            groundMask = Physics.DefaultRaycastLayers;
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
                    navigationAvailable = NavMesh.SamplePosition(point, out NavMeshHit nav, .5f, NavMesh.AllAreas);
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
}
