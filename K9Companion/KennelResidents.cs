using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Hideout;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Your other dogs in the hideout: each at its own kennel (at that dog's own kennel level), lying on its bed, dozing off
    /// when nobody's about and lifting its head to watch you when you come close. Only the active dog follows you; the DOG
    /// tab changes which one that is. The kennels stand together, in a group round the active dog's kennel (the paw on
    /// the overview): along its row either side, then in a row facing it (KennelSpot.GroupSlot). Wherever the active
    /// kennel goes (dragged by its paw, into the K9 kennels room, to a new level's spot), the others follow it. Each
    /// spot is remembered with its dog; the kennels keep clear of each other.
    /// </summary>
    internal static class KennelResidents
    {
        private static readonly Dictionary<string, Resident> residents = new Dictionary<string, Resident>();
        private static int seenVersion = -1;
        private static float nextLook, hideoutSince = -1;

        internal static void Refresh() { seenVersion = -1; nextLook = 0; }

        // The active kennel has moved (by hand, into the K9 kennels room, for a new level): the kennels no longer in its
        // group are taken away and put down again round it.
        private static bool rehome, regroup;
        internal static void Rehome() { rehome = true; Refresh(); }
        /// <summary>The active kennel was moved: every other kennel is taken up and the group laid out again round it.</summary>
        internal static void Regroup() { regroup = true; Rehome(); }

        internal static IEnumerable<Resident> All => residents.Values.Where(r => r != null);

        /// <summary>Every frame (Plugin.Update): the hideout's dogs that aren't out, made or taken away as the roster says.</summary>
        internal static void Tick()
        {
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1;
            bool hideout = Comfort.Common.Singleton<GameWorld>.Instantiated && Comfort.Common.Singleton<GameWorld>.Instance is HideoutGameWorld;
            if (!hideout || !Plugin.Active.Value || !Plugin.Hideout.Value || !K9.Owned)
            {
                if (residents.Count > 0) Clear();
                hideoutSince = -1;
                return;
            }
            if (hideoutSince < 0) hideoutSince = Time.unscaledTime;
            // The active dog's kennel goes down first, so the others keep clear of it (or after a while, without one).
            Companion main = Plugin.HideoutDog;
            if ((main == null || main.KennelHome == null) && Time.unscaledTime - hideoutSince < 8) return;
            if (placing) return;
            if (rehome)
            {
                rehome = false;
                Transform anchor = main?.KennelHome;
                if (anchor != null)
                    foreach (string id in residents.Keys.ToList())
                        if (regroup || residents[id] == null || residents[id].Home == null || !KennelSpot.InGroup(anchor, residents[id].Home.position)) Remove(id);
                layingOut = regroup; regroup = false;
            }
            if (seenVersion == Pack.Version) return;
            seenVersion = Pack.Version;
            var wanted = Pack.Dogs.Where(d => !d.Active).ToDictionary(d => d.Id);
            foreach (string id in residents.Keys.ToList())
                if (!wanted.TryGetValue(id, out PackDog dog) || residents[id] == null || !residents[id].Matches(dog)) Remove(id);
            var pending = wanted.Values.Where(d => !residents.ContainsKey(d.Id)).ToList();
            if (pending.Count > 0) { placing = true; Plugin.Runner.StartCoroutine(PlaceAll(pending)); }
        }

        // The kennels go down one after another (each keeps clear of those before it), the search for room spread over
        // frames so the hideout never stalls. A dog there's no room for stays away this visit (null: not tried again).
        private static bool placing;
        private static System.Collections.IEnumerator PlaceAll(List<PackDog> dogs)
        {
            foreach (PackDog dog in dogs)
            {
                Resident r = null;
                try { r = Resident.Make(dog); }
                catch (Exception e) { Plugin.Log.LogError($"{dog.Shown} couldn't be shown at its kennel: {e}"); }
                residents[dog.Id] = r;
                if (r == null) continue;
                bool done = false, ok = false;
                Plugin.Runner.StartCoroutine(r.Settle(result => { ok = result; done = true; }));
                while (!done) { if (r == null) break; yield return null; }
                if (r != null && !ok) { UnityEngine.Object.Destroy(r.gameObject); residents[dog.Id] = null; }
                if (!InHideout) break;
            }
            placing = false; layingOut = false;
        }
        // Laying the group out afresh (the active kennel moved): the spots the dogs had are passed over for the group's.
        private static bool layingOut;

        private static bool InHideout => Comfort.Common.Singleton<GameWorld>.Instantiated && Comfort.Common.Singleton<GameWorld>.Instance is HideoutGameWorld;

        private static void Remove(string id)
        {
            if (residents.TryGetValue(id, out Resident r) && r != null) UnityEngine.Object.Destroy(r.gameObject);
            residents.Remove(id);
        }

        private static void Clear() { foreach (string id in residents.Keys.ToList()) Remove(id); seenVersion = -1; }

        internal sealed class Resident : MonoBehaviour
        {
            private PackDog dog;
            private DogModel model;
            private CompanionDecor decor;
            private string jacket, collar;
            private int kennel;
            // Lying on its bed; dozing off and waking; getting up to sit and watch you, and lying down again.
            private enum Pose { Lying, ToSleep, Sleeping, Waking, StandUp, SitDown, Sitting, StandFromSit, LieDown }
            private Pose pose = Pose.Lying;
            private float since, nearSince = -1, farSince;

            internal Transform Home => decor?.Home;
            internal bool Matches(PackDog d) => d.BreedId == dog.BreedId && d.Coat == dog.Coat && d.Kennel == kennel && d.Jacket == jacket && d.Collar == collar
                                                && Mathf.Abs(d.BodyScale - dog.BodyScale) < .02f;

            internal static Resident Make(PackDog dog)
            {
                var root = new GameObject("K9 resident " + dog.Id);
                var world = Comfort.Common.Singleton<GameWorld>.Instance;
                if (world != null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, world.gameObject.scene);
                var r = root.AddComponent<Resident>();
                r.dog = dog; r.kennel = dog.Kennel; r.jacket = dog.Jacket; r.collar = dog.Collar;
                r.model = DogModel.Load(dog.Breed, dog.Coat, root.transform, Plugin.Scale.Value, dog.BodyScale);
                r.model.SkipUnseen = true;
                r.decor = new CompanionDecor(root.transform, r.model) { HomeName = "K9 kennel " + dog.Id, Resident = true, BodySize = dog.Breed.Size * dog.BodyScale * Plugin.Scale.Value,
                                                                        AreaDogId = dog.Id, AreaDogName = dog.Shown };
                r.decor.SetKennelLevel(dog.Kennel, KennelSpot.Solid);
                if (DogGear.Jackets.TryGetValue(dog.Jacket ?? "", out string look)) r.decor.SetVest(look);
                if (DogGear.Collars.TryGetValue(dog.Collar ?? "", out string style)) r.decor.SetCosmetics(style);
                root.SetActive(false);   // out of sight until its kennel has a spot
                return r;
            }

            // Its kennel where it was left (if still clear), else a new spot near the active dog's kennel, remembered.
            internal System.Collections.IEnumerator Settle(Action<bool> done)
            {
                JObject n = dog.Needs;
                bool saved = n != null && ((bool?)n["homeSet"] ?? false);
                Vector3 at = default; Quaternion facing = Quaternion.identity;
                bool placed = false;
                // With the active dog's kennel down, every kennel is in its group: a spot saved elsewhere is given up.
                Transform anchor = Plugin.HideoutDog?.KennelHome;
                if (saved && !(layingOut && anchor != null))
                {
                    at = new Vector3((float?)n["homeX"] ?? 0, (float?)n["homeY"] ?? 0, (float?)n["homeZ"] ?? 0);
                    facing = Quaternion.Euler(0, (float?)n["homeYaw"] ?? 0, 0);
                    placed = (anchor != null ? KennelSpot.InGroup(anchor, at) : Companion.InsideHideout(at)) && KennelSpot.Fits(at, facing, dog.Kennel, decor.HomeName);
                }
                if (!placed && anchor != null && KennelSpot.GroupSlot(anchor, dog.Kennel, decor.HomeName, out at, out facing)) { placed = true; Remember(at, facing); }
                if (!placed)
                {
                    var search = new KennelSpot.Search(dog.Kennel, decor.HomeName);
                    yield return search.Run();
                    if (this == null) { done(false); yield break; }
                    placed = search.Found;
                    at = search.At; facing = search.Facing;
                    if (placed) Remember(at, facing);
                }
                if (!placed) { Plugin.Log.LogWarning($"No clear floor for {dog.Shown}'s kennel: it stays away this visit."); done(false); yield break; }
                gameObject.SetActive(true);
                decor.PlaceHome(at, facing);
                LieOnBed();
                since = Time.time;
                Plugin.Log.LogInfo($"{dog.Shown} ({dog.Breed.Name}) is at its kennel (level {dog.Kennel}).");
                done(true);
            }

            // On its bed, wherever that is (moved with Hideout Interiors, it follows).
            private Vector3 bedWas;
            private Quaternion bedTurnWas;
            private void LieOnBed()
            {
                Transform spot = decor.BedSpot;
                if (spot == null) return;
                bedWas = spot.position; bedTurnWas = spot.rotation;
                Vector3 bed = spot.TransformPoint(new Vector3(0, 0, -.05f));
                float y = spot.position.y;
                if (decor.OnBed(bed, out float top)) y = top;
                transform.SetPositionAndRotation(new Vector3(bed.x, y, bed.z), Quaternion.Euler(0, spot.eulerAngles.y, 0));
            }

            private void Remember(Vector3 at, Quaternion facing)
            {
                var body = new JObject { ["dog"] = dog.Id, ["x"] = Math.Round(at.x, 3), ["y"] = Math.Round(at.y, 3), ["z"] = Math.Round(at.z, 3), ["yaw"] = Math.Round(facing.eulerAngles.y, 1) };
                try { RequestHandler.PostJsonAsync("/k9companion/pack/home", body.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't remember a kennel's spot: " + e.Message); }
                if (dog.Needs == null) dog.Needs = new JObject();
                dog.Needs["homeSet"] = true; dog.Needs["homeX"] = at.x; dog.Needs["homeY"] = at.y; dog.Needs["homeZ"] = at.z; dog.Needs["homeYaw"] = facing.eulerAngles.y;
            }

            private void Update()
            {
                if (model == null) return;
                float dt = Time.deltaTime;
                Transform spot = decor.BedSpot;
                if (spot != null && ((spot.position - bedWas).sqrMagnitude > .0025f || Quaternion.Angle(spot.rotation, bedTurnWas) > 1)) LieOnBed();
                // You: close by and in front of it, it watches you; nobody about for a while, it dozes.
                Player you = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
                Vector3 eye = Camera.main != null ? Camera.main.transform.position : you != null ? you.Transform.position + Vector3.up * 1.6f : transform.position + transform.forward * 50;
                Vector3 head = transform.position + Vector3.up * .5f * dog.Breed.Size * dog.BodyScale;
                Vector3 to = eye - head;
                bool near = you != null && you.gameObject.activeInHierarchy && to.magnitude < 4.5f;
                if (near) { if (nearSince < 0) nearSince = Time.time; farSince = Time.time; } else nearSince = -1;
                bool awake = pose != Pose.Sleeping && pose != Pose.ToSleep;
                if (awake && near)
                {
                    Vector3 local = transform.InverseTransformDirection(to);
                    model.LookAt(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg);
                }
                switch (pose)
                {
                    case Pose.Lying:
                        model.Tick("Lying_00", 1, dt);
                        if (near && Time.time - nearSince > 4 && UnityEngine.Random.value < dt * .3f) Go(Pose.StandUp);
                        else if (!near && Time.time - farSince > 25 && Time.time - since > 20) Go(Pose.ToSleep);
                        break;
                    case Pose.ToSleep: if (model.Tick("Trans_Lying_to_Sleeping", 1, dt, false)) Go(Pose.Sleeping); break;
                    case Pose.Sleeping:
                        model.Tick("Sleeping_01", 1, dt);
                        if (near && Time.time - nearSince > 1.5f) Go(Pose.Waking);
                        break;
                    case Pose.Waking: if (model.Tick("Trans_Sleeping_to_Lying", 1.2f, dt, false)) Go(Pose.Lying); break;
                    case Pose.StandUp: if (model.Tick("Trans_Lying_To_Stand", 1.2f, dt, false)) Go(Pose.SitDown); break;
                    case Pose.SitDown: if (model.Tick("Trans_Stand_To_Sitting", 1, dt, false)) Go(Pose.Sitting); break;
                    case Pose.Sitting:
                        model.Tick("Sitting_00", 1, dt);
                        if (!near && Time.time - farSince > 10) Go(Pose.StandFromSit);
                        break;
                    case Pose.StandFromSit: if (model.Tick("Trans_Sitting_To_Stand", 1.3f, dt, false)) Go(Pose.LieDown); break;
                    case Pose.LieDown: if (model.Tick("Trans_Stand_To_Lying", 1, dt, false)) Go(Pose.Lying); break;
                }
                decor.Update();
            }

            private void Go(Pose next) { pose = next; since = Time.time; }

            private void OnDestroy()
            {
                if (decor?.Home != null) Destroy(decor.Home.gameObject);
                decor?.Dispose(); model?.Dispose();
            }
        }
    }

    /// <summary>Clear, level floor for one more kennel: the active dog's kennel's rules (Companion.KennelFits), with every other kennel in the way.</summary>
    internal static class KennelSpot
    {
        private static int solidMask;
        internal static int Solid
        {
            get
            {
                if (solidMask == 0)
                {
                    solidMask = (int)LayersMaskController.PlayerStaticCollisionsMask | (int)LayersMaskController.HighPolyCollider | (int)LayersMaskController.TerrainLowPoly;
                    if (solidMask == 0) solidMask = Physics.DefaultRaycastLayers;
                }
                return solidMask;
            }
        }

        /// <summary>
        /// Room for one more kennel: near the active dog's kennel, else the Rest Space's pet bed, else the gym or the rest
        /// space; nearest first, a spot with a wall behind it taken at once. Run as a coroutine: it works a few milliseconds
        /// a frame, so the hideout keeps running while it looks.
        /// </summary>
        internal sealed class Search
        {
            private readonly int level; private readonly string own;
            public bool Found; public Vector3 At; public Quaternion Facing = Quaternion.identity;
            private const float Reach = 6f, Step = .4f, Budget = .004f;

            public Search(int level, string own) { this.level = level; this.own = own; }

            public System.Collections.IEnumerator Run()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Bounds inside = HideoutBox(out bool known);
                var starts = new List<Vector3>();
                Transform home = Plugin.HideoutDog?.KennelHome;
                if (home != null) starts.Add(home.position);
                if (HideoutProps.Where(HideoutProps.Bed, out Vector3 bed) && FloorNear(bed, .5f, own, out Vector3 bedFloor)) starts.Add(bedFloor);
                foreach (EAreaType type in new[] { EAreaType.Gym, EAreaType.RestSpace, EAreaType.PlaceOfFame })
                {
                    HideoutArea area = null;
                    try { area = UnityEngine.Object.FindObjectsOfType<HideoutArea>().FirstOrDefault(a => a.Data?.Template != null && a.Data.Template.Type == type); }
                    catch (Exception) { }
                    if (area?.AreaIconPoint != null && FloorNear(area.AreaIconPoint.position + Vector3.down * 1.5f, 1.5f, own, out Vector3 f)) starts.Add(f);
                }
                // The cells round a start, nearest first (not on top of the kennel it starts from: beside it).
                var cells = new List<Vector2>();
                for (float dx = -Reach; dx <= Reach; dx += Step)
                    for (float dz = -Reach; dz <= Reach; dz += Step)
                    {
                        float off = Mathf.Sqrt(dx * dx + dz * dz);
                        if (off <= Reach && off >= 1.2f) cells.Add(new Vector2(dx, dz));
                    }
                cells.Sort((a, b) => a.sqrMagnitude.CompareTo(b.sqrMagnitude));
                foreach (Vector3 near in starts)
                {
                    float best = float.MaxValue, bestOff = 0;
                    foreach (Vector2 c in cells)
                    {
                        if (clock.Elapsed.TotalSeconds > Budget) { yield return null; clock.Restart(); }
                        float off = c.magnitude;
                        // Nothing nearer is coming: the best so far will do.
                        if (best < float.MaxValue && off > bestOff + .8f) break;
                        if (!FloorNear(new Vector3(near.x + c.x, near.y, near.z + c.y), .15f, own, out Vector3 floor)) continue;
                        if (known && !(floor.x > inside.min.x - 8 && floor.x < inside.max.x + 8 && floor.z > inside.min.z - 8 && floor.z < inside.max.z + 8
                                       && floor.y > inside.min.y - 5 && floor.y < inside.max.y + 4) && !KennelsRoom.InAnyRoom(floor)) continue;
                        if (TooClose(floor, own) || Blocked(floor + Vector3.up * .55f, new Vector3(.3f, .47f, .3f), Quaternion.identity, own)) continue;
                        for (int turn = 0; turn < 8; turn++)
                        {
                            var face = Quaternion.Euler(0, turn * 45, 0);
                            if (!Fits(floor, face, level, own)) continue;
                            Vector3 back = floor + face * new Vector3(0, .35f, CompanionDecor.FootprintCentre.z - CompanionDecor.FootprintHalf.z);
                            bool wall = Physics.Raycast(back, face * Vector3.back, .45f, Solid, QueryTriggerInteraction.Ignore);
                            float score = off + (wall ? 0 : 1.2f);
                            if (score < best) { best = score; bestOff = off; At = floor; Facing = face; Found = true; }
                            if (wall) break;
                        }
                        if (Found && best == off) break;   // against a wall, as near as it gets
                    }
                    if (Found) yield break;
                }
            }
        }

        // The box round the hideout's area icons (Companion.InsideHideout's), worked out once for a search.
        private static Bounds HideoutBox(out bool known)
        {
            Bounds box = default; int n = 0;
            try
            {
                foreach (HideoutArea area in UnityEngine.Object.FindObjectsOfType<HideoutArea>(true))
                {
                    if (area == null || area.AreaIconPoint == null) continue;
                    Vector3 icon = area.AreaIconPoint.position;
                    if (n++ == 0) box = new Bounds(icon, Vector3.zero); else box.Encapsulate(icon);
                }
            }
            catch (Exception) { n = 0; }
            known = n >= 4;
            return box;
        }

        // The other kennels (the active dog's and the other dogs'): each takes the room its furniture can spread to.
        private const float Apart = 2.7f;
        private static bool TooClose(Vector3 at, string own)
        {
            var homes = new List<Transform>();
            if (Plugin.HideoutDog?.KennelHome != null) homes.Add(Plugin.HideoutDog.KennelHome);
            foreach (KennelResidents.Resident r in KennelResidents.All) if (r.Home != null && r.Home.name != own) homes.Add(r.Home);
            foreach (Transform h in homes)
            {
                Vector3 d = h.position - at; d.y = 0;
                if (d.magnitude < Apart && Mathf.Abs(h.position.y - at.y) < 2) return true;
            }
            return false;
        }

        internal static bool Fits(Vector3 at, Quaternion facing, int level, string own)
        {
            if (TooClose(at, own) || KennelsRoom.NearDoorway(at)) return false;
            bool first = true;
            foreach (var (localCentre, half) in CompanionDecor.Footprint(level))
            {
                Vector3 centre = at + facing * localCentre;
                float flat = first ? .035f : .06f;
                foreach (Vector3 corner in new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), Vector3.zero })
                {
                    Vector3 p = centre + facing * Vector3.Scale(corner, half);
                    if (!FloorNear(new Vector3(p.x, at.y, p.z), .1f, own, out Vector3 floor) || Mathf.Abs(floor.y - at.y) > flat) return false;
                }
                if (Blocked(centre + Vector3.up * .55f, new Vector3(half.x + .04f, .47f, half.z + .04f), facing, own)) return false;
                first = false;
            }
            Vector3 front = at + facing * CompanionDecor.FootprintCentre + facing * new Vector3(0, 0, CompanionDecor.FootprintHalf.z + .35f);
            return !Blocked(front + Vector3.up * .85f, new Vector3(.3f, .75f, .3f), facing, own);
        }

        internal static bool Floor(Vector3 near, string own, out Vector3 floor) => FloorNear(near, .15f, own, out floor);

        // The group round the active kennel, in its own frame (x along its row, z the way its bowls face; metres, turn in
        // degrees): either side along the row first, then a row facing it across an aisle. A kennel at level 3 spreads
        // about 3.1 m (vet table to cabinet), so they're 3.2 m apart.
        private static readonly (float X, float Z, float Turn)[] Slots =
        {
            (3.2f, 0, 0), (-3.2f, 0, 0), (6.4f, 0, 0), (-6.4f, 0, 0),
            (0, 4.4f, 180), (3.2f, 4.4f, 180), (-3.2f, 4.4f, 180), (6.4f, 4.4f, 180), (-6.4f, 4.4f, 180),
            (9.6f, 0, 0), (-9.6f, 0, 0),
        };
        // A slot with something in the way is tried a little off where it should be before it's given up.
        private static readonly Vector2[] Nudges = { Vector2.zero, new Vector2(0, .4f), new Vector2(.4f, 0), new Vector2(-.4f, 0), new Vector2(0, -.3f), new Vector2(0, .8f) };
        private const float GroupReach = 11f;

        /// <summary>Close enough to the active kennel to count as one of its group, and in the same room as it.</summary>
        internal static bool InGroup(Transform anchor, Vector3 at)
        {
            Vector3 d = at - anchor.position; d.y = 0;
            return d.magnitude <= GroupReach && Mathf.Abs(at.y - anchor.position.y) < 1.5f && SameRoom(anchor.position, at);
        }

        // Nothing solid between the two at head height (walls, not the furniture under it): the hideout's floor carries on
        // behind its rooms' walls, so a spot 3 m on could be clear, level floor inside the wall, out of sight. Kennels,
        // dogs and people on the way don't count.
        private const float WallCheckHeight = 1.7f;
        internal static bool SameRoom(Vector3 a, Vector3 b)
        {
            Vector3 from = a + Vector3.up * WallCheckHeight, to = b + Vector3.up * WallCheckHeight, way = to - from;
            float length = way.magnitude;
            if (length < .05f) return true;
            foreach (RaycastHit hit in Physics.RaycastAll(from, way / length, length, Solid, QueryTriggerInteraction.Ignore))
            {
                Collider c = hit.collider;
                if (c.GetComponentInParent<Player>() != null || c.GetComponentInParent<DogHitbox>() != null || c.GetComponentInParent<Companion>() != null) continue;
                string root = c.transform.root.name;
                if (root.StartsWith("K9 kennel", StringComparison.Ordinal) || root.StartsWith("K9 resident ", StringComparison.Ordinal) || root == "K9 Companion dog") continue;
                return false;
            }
            return true;
        }

        /// <summary>The first free spot in the active kennel's group for a kennel at this level.</summary>
        internal static bool GroupSlot(Transform anchor, int level, string own, out Vector3 at, out Quaternion facing)
        {
            at = default; facing = Quaternion.identity;
            Quaternion frame = Quaternion.Euler(0, anchor.eulerAngles.y, 0);
            foreach (var (x, z, turn) in Slots)
                foreach (Vector2 nudge in Nudges)
                {
                    Vector3 p = anchor.position + frame * new Vector3(x + nudge.x, 0, z + nudge.y);
                    if (!Floor(p, own, out Vector3 floor) || !SameRoom(anchor.position, floor)) continue;
                    Quaternion face = frame * Quaternion.Euler(0, turn, 0);
                    if (!Fits(floor, face, level, own)) continue;
                    at = floor; facing = face;
                    return true;
                }
            return false;
        }

        private static bool FloorNear(Vector3 near, float within, string own, out Vector3 floor)
        {
            floor = default; float bestGap = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(near + Vector3.up * (within + .6f), Vector3.down, within * 2 + 1.2f, Solid, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .9f || Ignored(hit.collider, own)) continue;
                float gap = Mathf.Abs(hit.point.y - near.y);
                if (gap <= within && gap < bestGap) { bestGap = gap; floor = hit.point; }
            }
            return bestGap < float.MaxValue;
        }

        private static bool Blocked(Vector3 centre, Vector3 half, Quaternion rotation, string own)
        {
            foreach (Collider c in Physics.OverlapBox(centre, half, rotation, Solid, QueryTriggerInteraction.Ignore))
                if (!Ignored(c, own)) return true;
            return false;
        }

        // People and dogs never count, nor the kennel being placed; every other kennel (the active dog's too) does.
        private static bool Ignored(Collider c, string own)
            => c.GetComponentInParent<Player>() != null || c.GetComponentInParent<DogHitbox>() != null || c.transform.root.name == own
               || c.GetComponentInParent<Companion>() != null || c.transform.root.name == "K9 Companion dog" || c.transform.root.name.StartsWith("K9 resident ", StringComparison.Ordinal);
    }
}
