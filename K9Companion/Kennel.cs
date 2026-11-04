using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System;
using BepInEx.Bootstrap;
using EFT.CameraControl;
using EFT.Hideout;
using EFT;
using HarmonyLib;
using TMPro;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace K9Companion
{
    /// <summary>
    /// The hideout kennel: where the bed and bowls stand (saved with the dog's needs), sending the dog to bed, and
    /// bringing it back there after a meal. Resting, sleeping, eating and drinking reuse the dog's existing animations.
    /// The kennel only ever goes on clear, level floor: nothing of the hideout's is moved, hidden or clipped into.
    /// </summary>
    public sealed partial class Companion
    {
        private bool bedTrip;
        private static int solidMask;

        // What the kennel must not overlap and what the dog can't walk through: everything you bump into, plus the
        // detailed furniture colliders.
        private static int Solid
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

        // The kennel goes where it was last placed (if that's still clear), otherwise in the rest area, otherwise in the
        // nearest clear spot to you. The dog then settles on its bed.
        private void SetUpKennel()
        {
            Player owner = CurrentPlayer;
            bool room = KennelRoom.Prepare(out Vector3 corner, out _);   // the kennel's room is cleared wherever the kennel stands
            Vector3 at = default; Quaternion facing = Quaternion.identity;
            bool placed = false;
            // Once its room is open the kennel belongs there, whatever spot was saved before, unless it was put somewhere
            // by hand (moved from the overview with Hideout Extensions).
            if (needs != null && needs.HomeSet && !needs.HomeByHand && room && Flat(new Vector3(needs.HomeX, needs.HomeY, needs.HomeZ), corner) > 3f)
            {
                Plugin.Log.LogInfo("The saved kennel spot is outside the kennel's room; moving it there.");
                needs.HomeSet = false;
            }
            if (needs != null && needs.HomeSet)
            {
                at = new Vector3(needs.HomeX, needs.HomeY, needs.HomeZ); facing = Quaternion.Euler(0, needs.HomeYaw, 0);
                placed = KennelFits(at, facing, out string why);
                if (!placed) Plugin.Log.LogInfo($"The saved kennel spot isn't clear any more ({why}); finding a new one.");
            }
            if (!placed) placed = AreaSpot(out at, out facing);
            if (!placed && owner != null && ClearSpotNear(owner.Transform.position, owner.Transform.position.y, 5f, owner.Transform.position, out at, out facing))
            { placed = true; MoveKennel(at, facing); Plugin.Log.LogInfo($"No room in the rest area; kennel placed near you at {at}."); }
            if (!placed) { Plugin.Log.LogWarning("No clear floor for the kennel was found; the dog has no bed this visit."); return; }
            decor.SetKennelLevel(kennelLevel, Solid);
            decor.PlaceHome(at, facing);
            command = Command.Down;
            Teleport(at, at + facing * Vector3.forward);
        }

        // ---- finding a spot -----------------------------------------------------------------------------------------

        // The rooms the kennel may go in, roomiest first: the gym has open floor, the rest space and hall of fame less.
        private static readonly EAreaType[] KennelRooms = { EAreaType.Gym, EAreaType.RestSpace, EAreaType.PlaceOfFame };

        // The first of those rooms with clear floor for the whole kennel (at its level); placed and remembered there.
        private bool AreaSpot(out Vector3 at, out Quaternion facing)
        {
            at = default; facing = Quaternion.identity;
            // First choice: where the bunk bed stood in the room behind the emergency wall, cleared for it.
            if (KennelRoom.Prepare(out Vector3 corner, out Vector3 middle) && FloorNear(corner, .3f, out Vector3 floor, "no floor where the bunk bed was")
                && ClearSpotNear(floor, floor.y, 2.3f, middle, out at, out facing))
            {
                MoveKennel(at, facing);
                Plugin.Log.LogInfo($"Kennel (level {kennelLevel}) placed in the room behind the emergency wall at {at}.");
                return true;
            }
            foreach (EAreaType room in KennelRooms)
                if (AreaSpot(room, out at, out facing))
                {
                    MoveKennel(at, facing);
                    Plugin.Log.LogInfo($"Kennel (level {kennelLevel}) placed in the {room} at {at}.");
                    return true;
                }
            return false;
        }

        // Inside an area's own floor space (where you stand to use it), as close to its middle as there's room for.
        private bool AreaSpot(EAreaType type, out Vector3 at, out Quaternion facing)
        {
            at = default; facing = Quaternion.identity;
            HideoutArea rest = null;
            try { rest = FindObjectsOfType<HideoutArea>().FirstOrDefault(a => a.Data?.Template != null && a.Data.Template.Type == type); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't look up the {type}: " + e.Message); }
            if (rest == null) { Plugin.Log.LogInfo($"No {type} found."); return false; }

            Bounds area = default; bool any = false;
            if (rest._trigger != null)
                foreach (Collider c in rest._trigger.GetComponentsInChildren<Collider>(true))
                {
                    if (!any) { area = c.bounds; any = true; } else area.Encapsulate(c.bounds);
                }
            Vector3 icon = rest.AreaIconPoint != null ? rest.AreaIconPoint.position : rest.transform.position;
            if (!any) area = new Bounds(icon, new Vector3(4, 3, 4));
            // The area's floor: the walkable surface nearest the bottom of its space (the icon floats above it).
            float floorY = any ? area.min.y : icon.y - 1.5f;
            if (FloorNear(new Vector3(area.center.x, floorY, area.center.z), 1.2f, out Vector3 f)) floorY = f.y;
            Vector3 middle = new Vector3(area.center.x, floorY, area.center.z);
            float reach = Mathf.Clamp(Mathf.Max(area.extents.x, area.extents.z) + 1f, 2.5f, 5.5f);
            Plugin.Log.LogInfo($"{type} space {area.size} around {middle}; looking for clear floor within {reach:0.0} m.");
            return ClearSpotNear(middle, floorY, reach, middle, out at, out facing);
        }

        // The best clear spot within 'reach' of 'near', on the floor at 'floorY', with the bowls facing into the room.
        private bool ClearSpotNear(Vector3 near, float floorY, float reach, Vector3 lookAt, out Vector3 at, out Quaternion facing)
        {
            at = default; facing = Quaternion.identity;
            var rejected = new Dictionary<string, int>();
            float best = float.MaxValue;
            for (float dx = -reach; dx <= reach; dx += .25f)
                for (float dz = -reach; dz <= reach; dz += .25f)
                {
                    float off = Mathf.Sqrt(dx * dx + dz * dz);
                    if (off > reach) continue;
                    if (!FloorNear(new Vector3(near.x + dx, floorY, near.z + dz), .15f, out Vector3 floor)) { Count(rejected, "no floor"); continue; }
                    // Quick test first: the middle itself has to be free, whichever way the kennel faces.
                    if (Blocked(floor + Vector3.up * .55f, new Vector3(.3f, .47f, .3f), Quaternion.identity)) { Count(rejected, "furniture in the way"); continue; }
                    for (int turn = 0; turn < 8; turn++)
                    {
                        var face = Quaternion.Euler(0, turn * 45, 0);
                        if (!KennelFits(floor, face, out string why)) { Count(rejected, why); continue; }
                        // Closest to the middle, back to a wall, bowls towards the room.
                        Vector3 toRoom = lookAt - floor; toRoom.y = 0;
                        float score = off + (WallBehind(floor, face) ? 0 : 1.2f)
                                    + (toRoom.sqrMagnitude > .25f ? Vector3.Angle(face * Vector3.forward, toRoom) / 180f : 0);
                        if (score < best) { best = score; at = floor; facing = face; }
                    }
                }
            if (best < float.MaxValue) return true;
            Plugin.Log.LogInfo("No clear spot: " + string.Join(", ", rejected.OrderByDescending(r => r.Value).Select(r => $"{r.Key} x{r.Value}")));
            return false;
        }

        private static void Count(Dictionary<string, int> tally, string why) => tally[why] = tally.TryGetValue(why, out int n) ? n + 1 : 1;

        // Level floor under the bed and both bowls, nothing in the way up to a standing dog's height, and room in front
        // to reach the bowls.
        private bool KennelFits(Vector3 at, Quaternion facing, out string why)
        {
            // The bed and bowls, then whatever the kennel's level adds beside them (cabinet, barrel, vet table).
            bool first = true;
            foreach (var (localCentre, half) in CompanionDecor.Footprint(kennelLevel))
            {
                Vector3 centre = at + facing * localCentre;
                float level = first ? .035f : .06f;
                foreach (Vector3 corner in new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), Vector3.zero })
                {
                    Vector3 p = centre + facing * Vector3.Scale(corner, half);
                    if (!FloorNear(new Vector3(p.x, at.y, p.z), .1f, out Vector3 floor) || Mathf.Abs(floor.y - at.y) > level) { why = first ? "uneven floor" : "uneven floor for the kennel's furniture"; return false; }
                }
                if (Blocked(centre + Vector3.up * .55f, new Vector3(half.x + .04f, .47f, half.z + .04f), facing)) { why = first ? "furniture in the way" : "no room for the kennel's furniture"; return false; }
                first = false;
            }
            Vector3 centreFront = at + facing * CompanionDecor.FootprintCentre, halfFront = CompanionDecor.FootprintHalf;
            Vector3 front = centreFront + facing * new Vector3(0, 0, halfFront.z + .35f);
            if (Blocked(front + Vector3.up * .85f, new Vector3(.3f, .75f, .3f), facing)) { why = "no room in front"; return false; }
            why = null; return true;
        }

        private static bool WallBehind(Vector3 at, Quaternion facing)
        {
            Vector3 back = at + facing * new Vector3(0, .35f, CompanionDecor.FootprintCentre.z - CompanionDecor.FootprintHalf.z);
            return Physics.Raycast(back, facing * Vector3.back, .45f, Solid, QueryTriggerInteraction.Ignore);
        }

        // The walkable surface closest to 'near' in height (within 'within' metres of it).
        private static bool FloorNear(Vector3 near, float within, out Vector3 floor, string complaint)
        {
            if (FloorNear(near, within, out floor)) return true;
            Plugin.Log.LogInfo($"Kennel room not used: {complaint} ({near}).");
            return false;
        }

        private static bool FloorNear(Vector3 near, float within, out Vector3 floor)
        {
            floor = default; float bestGap = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(near + Vector3.up * (within + .6f), Vector3.down, within * 2 + 1.2f, Solid, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .9f || Ignored(hit.collider)) continue;
                float gap = Mathf.Abs(hit.point.y - near.y);
                if (gap <= within && gap < bestGap) { bestGap = gap; floor = hit.point; }
            }
            return bestGap < float.MaxValue;
        }

        private static bool Blocked(Vector3 centre, Vector3 half, Quaternion rotation)
        {
            foreach (Collider c in Physics.OverlapBox(centre, half, rotation, Solid, QueryTriggerInteraction.Ignore))
                if (!Ignored(c)) return true;
            return false;
        }

        // People, the dog and the kennel itself never count as obstacles.
        private static bool Ignored(Collider c)
            => c.GetComponentInParent<Player>() != null || c.GetComponentInParent<DogHitbox>() != null || c.transform.root.name == "K9 kennel"
               || c.GetComponentInParent<Companion>() != null || c.transform.root.name == "K9 Companion dog";

        // ---- moving it, and the dog's trips to bed ------------------------------------------------------------------

        // A new kennel level that no longer fits where the kennel stands moves it to a room with space.
        private void KennelLevelChanged()
        {
            if (decor?.Home == null) return;
            if (KennelFits(decor.Home.position, decor.Home.rotation, out string why)) { decor.SetKennelLevel(kennelLevel, Solid); return; }
            Plugin.Log.LogInfo($"The kennel's level {kennelLevel} doesn't fit where it stands ({why}); moving it.");
            if (AreaSpot(out Vector3 at, out Quaternion facing)) { SetCommand(Command.Down); Teleport(at, at + facing * Vector3.forward); }
            else decor.SetKennelLevel(kennelLevel, Solid);
        }

        // Moving it from the overview (Hideout Extensions): why it can't stand there, or null.
        internal string KennelCheck(Vector3 at, Quaternion facing) => KennelFits(at, facing, out string why) ? null : why;

        // Put down by hand on the overview: it stays there from now on, also outside its room.
        internal void KennelPlacedByHand(Vector3 at, Quaternion facing)
        {
            if (needs != null) needs.HomeByHand = true;
            MoveKennel(at, facing);
            // The dog follows its kennel: it walks over to the new bed (unless it's out for a wander; it goes there after).
            if (dog != null && !dead && !roaming) GoToBed(true);
        }

        private void MoveKennel(Vector3 at, Quaternion facing)
        {
            decor.PlaceHome(at, facing);
            decor.SetKennelLevel(kennelLevel, Solid);   // the upgrades look for room again at the new spot
            if (needs != null)
            {
                needs.HomeSet = true; needs.HomeX = at.x; needs.HomeY = at.y; needs.HomeZ = at.z; needs.HomeYaw = facing.eulerAngles.y;
                NeedsStore.Save();
            }
        }

        // DOG menu "Go to bed" / Shift+F7 in the hideout.
        private void GoToBed(bool quiet = false)
        {
            if (!IsHideout || decor?.Home == null) { if (!quiet) Say("The bed is in the hideout."); return; }
            SetCommand(Command.Down);
            Vector3 bed = decor.Home.position;
            if (Flat(dog.transform.position, bed) <= .3f) { if (!quiet) Say("Bed."); return; }
            // The dog walks there when it can find a way, round to the shelter's door and in (never through its walls);
            // otherwise it goes straight to its bed.
            if (!Reachable(bed)) Teleport(bed, bed + decor.Home.forward);
            else { bedRoute.Clear(); bedRoute.AddRange(decor.BedRoute(dog.transform.position)); bedTrip = true; }
            if (!quiet) Say("Bed.");
        }

        private readonly List<Vector3> bedRoute = new List<Vector3>();

        // True while the dog is walking to its bed; the follow loop takes it there, a stop at a time.
        private bool BedWalking(out Vector3 spot)
        {
            spot = default;
            if (!bedTrip || decor?.Home == null) { bedTrip = false; return false; }
            Vector3 bed = decor.Home.position;
            while (bedRoute.Count > 1 && Flat(dog.transform.position, bedRoute[0]) <= .35f) bedRoute.RemoveAt(0);
            spot = bedRoute.Count > 0 ? bedRoute[0] : bed;
            if (bedRoute.Count > 1 || Flat(dog.transform.position, spot) > .3f)
            {
                // Stuck on the way (a door closed, someone in the way): finish the trip directly.
                if (Time.time - lastProgress > 6) { bedTrip = false; Teleport(bed, bed + decor.Home.forward); return false; }
                return true;
            }
            // Arrived: it turns to face out of its door; Down makes it lie on the bed, and in time it sleeps.
            bedTrip = false;
            yaw = decor.Home.eulerAngles.y;
            return false;
        }
    }

    /// <summary>
    /// The kennel's own corner: the room behind the emergency wall, once the wall is broken through (its last stage).
    /// Its floor clutter (stepladder, bunk bed, tyre, box, the torch on the bed) is switched off while the kennel is in
    /// the hideout, and the kennel goes where the bunk bed stood. Nothing is changed otherwise, and the clutter comes
    /// back when the dog leaves the hideout.
    /// </summary>
    internal static class KennelRoom
    {
        private static readonly string[] Clutter = { "bed_metal_bunk", "Ladder", "jcb_Tire_reserve", "Box_closed_01", "flashlight_portable_P" };
        private static readonly List<GameObject> hidden = new List<GameObject>();
        private static bool reported;

        /// <summary>Clears the room; <paramref name="corner"/> is where the bunk bed stood, <paramref name="middle"/> the room's middle.</summary>
        public static bool Prepare(out Vector3 corner, out Vector3 middle)
        {
            corner = middle = default;
            Transform props = Room();
            if (props == null) return false;
            Transform bed = props.Cast<Transform>().First(t => t.name.StartsWith("bed_metal_bunk"));
            // Along the wall from where the bunk bed stood, a little towards the room's door.
            Transform door = props.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Outside_Door_Metal"));
            corner = door != null ? Vector3.Lerp(bed.position, new Vector3(door.position.x, bed.position.y, bed.position.z), .4f) : bed.position;
            foreach (Transform t in props)
            {
                if (!t.gameObject.activeSelf || !Clutter.Any(c => t.name.StartsWith(c))) continue;
                t.gameObject.SetActive(false);
                hidden.Add(t.gameObject);
            }
            var room = props.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Basement_Room"))?.GetComponentInChildren<Renderer>(true);
            middle = room != null ? room.bounds.center : corner;
            middle.y = corner.y;
            Physics.SyncTransforms();
            return true;
        }

        // The emergency wall's stage that's switched on and holds the room's furniture: the bunk bed and the stepladder.
        private static Transform Room()
        {
            HideoutArea wall = Object.FindObjectsOfType<HideoutArea>(true).FirstOrDefault(a => a.Data?.Template != null && a.Data.Template.Type == EAreaType.EmergencyWall);
            if (wall == null) { Report("the emergency wall area wasn't found"); return null; }
            foreach (Transform t in wall.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("bed_metal_bunk") || t.parent == null || !t.parent.gameObject.activeInHierarchy) continue;
                if (t.parent.Cast<Transform>().Any(s => s.name.StartsWith("Ladder"))) return t.parent;
            }
            Report("the room behind the emergency wall isn't open (wall not broken through)");
            return null;
        }

        private static void Report(string why)
        {
            if (reported) return;
            reported = true;
            Plugin.Log.LogInfo("Kennel room not used: " + why + ".");
        }

        public static void Restore()
        {
            foreach (var go in hidden) if (go != null) go.SetActive(true);
            hidden.Clear();
        }
    }

    /// <summary>
    /// The K9 Kennel in the hideout's own Rest Space screen. The server mod lists the kennel's three levels as Rest Space
    /// improvements, so the game shows their materials and timers and takes the materials itself; it shows improvements
    /// without a name, so each gets a heading with what it adds, and a level that isn't unlocked yet (quest or Dr. Yang
    /// loyalty) says why and can't be started. The server checks the same rules.
    /// The kennel's levels show only in the kennel's own screen (its paw icon on the overview opens the Rest Space's
    /// screen as "K9 KENNEL", with nothing but the three levels); the Rest Space's screen itself stays as the game has it.
    /// </summary>
    internal static class KennelUi
    {
        private static readonly FieldInfo ImprovementField = AccessTools.Field(typeof(ImprovementView), "_improvement");
        private static readonly FieldInfo SubstrateField = AccessTools.Field(typeof(HideoutScreenOverlay), "_areaSubstrate");

        // The area screen is showing the kennel (opened from its icon) rather than the Rest Space.
        internal static bool KennelView;
        private static bool opening;

        private static readonly string[] Features =
        {
            "Dog bed and bowls, food and water, the dog's health and condition, deployment",
            "Armour locker, jacket repairs, 2 equipment presets, better rest",
            "Veterinary station (+50% healing), 4 presets, faster recovery, cosmetics, premium storage",
        };

        internal static void Patch(Harmony harmony)
        {
            MethodInfo show = AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.Show))
                              ?? throw new MissingMethodException(nameof(ImprovementView), nameof(ImprovementView.Show));
            MethodInfo can = AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.CanImprove))
                             ?? throw new MissingMethodException(nameof(ImprovementView), nameof(ImprovementView.CanImprove));
            if (ImprovementField == null) throw new MissingFieldException(nameof(ImprovementView), "_improvement");
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(KennelUi), nameof(Shown)));
            harmony.Patch(can, postfix: new HarmonyMethod(typeof(KennelUi), nameof(Allowed)));
            harmony.Patch(AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.UpdateImprovementInfo)), postfix: new HarmonyMethod(typeof(KennelUi), nameof(Status)));
            harmony.Patch(AccessTools.Method(typeof(AreaScreenSubstrate), nameof(AreaScreenSubstrate.SelectArea)), prefix: new HarmonyMethod(typeof(KennelUi), nameof(Selecting)));
            if (SubstrateField == null) throw new MissingFieldException(nameof(HideoutScreenOverlay), "_areaSubstrate");
        }

        private static int Level(Improvement improvement) => improvement == null ? 0 : Array.IndexOf(K9.KennelIds, improvement.Id) + 1;

        /// <summary>Why a kennel level can't be started yet, or null.</summary>
        internal static string Locked(int level, Profile profile)
        {
            K9.EnsureLoaded();
            if (!K9.Owned) return "Needs a dog: Dr. Yang's quest Trust Is Earned";
            int built = K9.Kennel(profile);
            if (level == 1) return K9.QuestDone(profile, K9.TrustIsEarned) || built >= 1 ? null : "Complete Dr. Yang's quest Trust Is Earned";
            if (built < level - 1) return "Build the " + K9.KennelNames[level - 2] + " first";
            int loyalty = level == 2 ? 2 : 4;
            return K9.YangLoyalty(profile) < loyalty ? "Requires Dr. Yang loyalty level " + loyalty : null;
        }

        private static void Allowed(ImprovementView __instance, ref bool __result)
        {
            int level = Level(ImprovementField.GetValue(__instance) as Improvement);
            if (level > 0 && __result && Locked(level, K9.SessionProfile) != null) __result = false;
        }

        /// <summary>The kennel's screen: the Rest Space's area screen, opened in place (no camera move) as the kennel.</summary>
        internal static void OpenKennel(HideoutScreenOverlay overlay, AreaData rest)
        {
            var substrate = SubstrateField.GetValue(overlay) as AreaScreenSubstrate;
            opening = true;
            if (substrate != null) substrate.SelectArea(rest);
            else { opening = false; Plugin.Log.LogWarning("K9 Kennel screen: the area screen isn't ready."); }
        }

        private static readonly System.Collections.Generic.List<GameObject> hiddenParts = new System.Collections.Generic.List<GameObject>();

        private static bool reported;

        private static void Selecting()
        {
            KennelView = opening; opening = false; reported = false;
            // Whatever the kennel's screen hid comes back for the next area shown.
            foreach (var part in hiddenParts) if (part != null) part.SetActive(true);
            hiddenParts.Clear();
        }

        private static void Hide(GameObject part)
        {
            if (part == null || !part.activeSelf) return;
            part.SetActive(false);
            hiddenParts.Add(part);
        }

        // Each area screen shows either the kennel's levels or the rest of the Rest Space's, never both.
        private static bool Belongs(int level) => KennelView ? level > 0 : level == 0;

        private static void Status(ImprovementView __instance)
        {
            var improvement = ImprovementField.GetValue(__instance) as Improvement;
            if (Level(improvement) > 0 && improvement.Completed && __instance._status != null) __instance._status.text = "Built";
        }

        /// <summary>Every frame on the overview: while the kennel's screen is open, it shows nothing but the kennel.</summary>
        internal static void Enforce(HideoutScreenOverlay overlay, int level)
        {
            if (!KennelView || overlay == null) return;
            var substrate = SubstrateField.GetValue(overlay) as AreaScreenSubstrate;
            if (substrate == null || !substrate.isActiveAndEnabled) { KennelView = false; return; }
            KennelIcon.Dress(substrate._areaPanel, level);
            // Everything but the levels list (and whatever holds it): the Rest Space's own panels and its upgrade button.
            var lists = substrate.GetComponentsInChildren<ImprovementPanel>(true);
            foreach (var mb in substrate.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(mb is IAreaRelatedPanel) || mb is ImprovementPanel) continue;
                if (lists.Any(l => l.transform.IsChildOf(mb.transform) || mb.transform.IsChildOf(l.transform))) continue;
                Hide(mb.gameObject);
            }
            if (substrate._actionButton != null) Hide(substrate._actionButton.gameObject);
            if (!reported && lists.Length > 0)
            {
                reported = true;
                Plugin.Log.LogInfo($"K9 Kennel screen: {lists.Length} level list(s), {lists.Sum(l => l.GetComponentsInChildren<ImprovementView>(true).Count(v => v.gameObject.activeSelf))} level(s) showing; hid {string.Join(", ", hiddenParts.Select(p => p.name))}.");
            }
            foreach (var panel in substrate.GetComponentsInChildren<ImprovementPanel>(true))
                foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
                    if (text.GetComponentInParent<ImprovementView>() == null && text.text != "K9 KENNEL UPGRADES") text.text = "K9 KENNEL UPGRADES";
        }

        private static void Shown(ImprovementView __instance, Improvement improvement)
        {
            try
            {
                int level = Level(improvement);
                if (!Belongs(level)) { __instance.gameObject.SetActive(false); return; }
                if (!__instance.gameObject.activeSelf) __instance.gameObject.SetActive(true);
                if (level == 0) return;
                Transform old = __instance.transform.Find("K9 kennel heading");
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                var go = new GameObject("K9 kennel heading", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(__instance.transform, false);
                rect.SetAsFirstSibling();
                // In a layout the heading takes its own row; otherwise it sits just above the view.
                var element = go.AddComponent<LayoutElement>(); element.minHeight = element.preferredHeight = 46;
                if (__instance.GetComponent<LayoutGroup>() == null)
                {
                    rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(0, 0);
                    rect.anchoredPosition = new Vector2(8, 2); rect.sizeDelta = new Vector2(-16, 46);
                }
                var text = go.AddComponent<TextMeshProUGUI>();
                TMP_Text sample = __instance._status != null ? (TMP_Text)__instance._status : __instance.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                if (sample != null) text.font = sample.font;
                text.fontSize = 15; text.enableWordWrapping = true; text.raycastTarget = false; text.alignment = TextAlignmentOptions.BottomLeft;
                string locked = Locked(level, K9.SessionProfile);
                text.text = $"<b>K9 KENNEL · LEVEL {level}: {K9.KennelNames[level - 1].ToUpperInvariant()}</b>\n<size=85%><color=#a8a69a>{Features[level - 1]}</color>" +
                            (locked == null || improvement.Completed || improvement.Processing ? "" : $"  <color=#c9705f>· LOCKED: {locked}</color>") + "</size>";
                __instance.ButtonUpdate();
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 Kennel heading: " + e.Message); }
        }
    }

    /// <summary>
    /// The kennel's hex on the hideout overview, like the game's own areas: kennel_icon.png (the paw), over the kennel,
    /// with "K9 KENNEL" and its level. It is one of the game's area panels made for the Rest Space (where the kennel's
    /// levels are built); its picture, label, position, click (the kennel's own screen) and hover (the game's outline
    /// on the kennel, not the Rest Space) are the kennel's. The same goes for its entry in the area bar along the bottom,
    /// which sits next to the Rest Space's.
    /// </summary>
    internal static class KennelIcon
    {
        private static readonly FieldInfo Panels = AccessTools.Field(typeof(HideoutScreenRear), "_areaWorldPanels");
        private static HideoutScreenRear screen;
        private static AreaWorldPanel panel;
        private static AreasPanel bar;
        private static AreaPanel barEntry;
        private static Sprite sprite;
        private static bool firstPerson = true, failed;
        private static float nextLook;
        private static Transform kennelHome;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AreaPanel), nameof(AreaPanel.PointerEnterHandler)), prefix: new HarmonyMethod(typeof(KennelIcon), nameof(Entered)));
            harmony.Patch(AccessTools.Method(typeof(AreaPanel), nameof(AreaPanel.PointerExitHandler)), prefix: new HarmonyMethod(typeof(KennelIcon), nameof(Left)));
        }

        // Hovering the paw outlines the kennel (the way hovering an area outlines that area), never the Rest Space.
        private static bool Entered(AreaPanel __instance) { if (!Ours(__instance)) return true; Hover(true); return false; }
        private static bool Left(AreaPanel __instance) { if (!Ours(__instance)) return true; Hover(false); return false; }
        private static bool Ours(AreaPanel shown) => shown != null && (shown == panel || shown == barEntry);

        private static void Hover(bool on)
        {
            panel?.AreaIconCreated?.SetHover(on);
            HighLightMesh outline = CameraManager.Instance?.MeshHighlighter;
            if (outline == null) return;
            if (on && kennelHome != null)
            {
                outline.Color = (panel ?? barEntry).Data.HighlightColor;
                outline.Target = kennelHome;
                outline.enabled = true;
            }
            else if (outline.Target == kennelHome) { outline.Target = null; outline.enabled = false; }
        }

        public static void Tick(Transform kennel, int level, Companion companion)
        {
            if (failed) return;
            if (kennel == null) { Close(); return; }
            kennelHome = kennel;
            try
            {
                if (screen == null && Time.unscaledTime >= nextLook) { nextLook = Time.unscaledTime + 1; screen = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>(true); }
                if (screen == null || !screen.isActiveAndEnabled) return;
                if (panel == null && !Create()) return;
                Bar(level);
                // Right-clicking the paw moves the kennel, with Hideout Extensions.
                if (level >= 1) HideoutExtensionsLink.Kennel(panel.gameObject, kennel, companion);
                else HideoutExtensionsLink.Unmovable(panel.gameObject, "Build the K9 Kennel first: then it can be moved.");
                if (screen.FirstPerson != firstPerson) { firstPerson = screen.FirstPerson; panel.SetPointOfView(firstPerson); }
                Dress(level);
                Camera camera = CameraManager.Instance?.Camera;
                if (camera == null) return;
                // Placed the way the game places its own: just over the dog's bed, in screen space, with distance as depth.
                Vector3 point = level >= 1 ? kennel.TransformPoint(new Vector3(0, .8f, -.05f)) : kennel.position + Vector3.up * .9f;
                Vector3 view = camera.WorldToViewportPoint(point);
                float distance = (camera.transform.position - point).magnitude * (view.z < 0 ? -1 : 1);
                panel.SetPosition(new Vector3(view.x * Screen.width, view.y * Screen.height, distance));
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("Kennel icon disabled: " + e.Message);
                Close();
            }
        }

        /// <summary>The kennel's own screen (its levels), as clicking its icon opens it; false if the hideout's screen isn't up.</summary>
        internal static bool OpenScreen()
        {
            if (screen == null) screen = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>();
            var rest = screen == null ? null : (Panels.GetValue(screen) as List<AreaWorldPanel>)?.FirstOrDefault(p => p != null && p.Data?.Template?.Type == EAreaType.RestSpace);
            if (rest == null) return false;
            KennelUi.OpenKennel(screen._hideoutScreenOverlay, rest.Data);
            return true;
        }

        private static bool Create()
        {
            var rest = (Panels.GetValue(screen) as List<AreaWorldPanel>)?.FirstOrDefault(p => p != null && p.Data?.Template?.Type == EAreaType.RestSpace);
            if (rest == null) return false;
            panel = UnityEngine.Object.Instantiate(screen._areaPanelTemplate, screen._areaIconsContainer, false);
            panel.name = "K9 Kennel icon";
            var overlay = screen._hideoutScreenOverlay;
            panel.Show(rest.Data, p => KennelUi.OpenKennel(overlay, p.Data));
            firstPerson = screen.FirstPerson;
            panel.SetPointOfView(firstPerson);
            return true;
        }

        private static void Dress(int level)
        {
            Dress(panel, level);
            KennelUi.Enforce(screen._hideoutScreenOverlay, level);
        }

        // The game re-applies the Rest Space's picture and texts whenever that area updates, so they're set every frame.
        internal static void Dress(AreaPanel panel, int level)
        {
            if (panel == null) return;
            if (panel.AreaName != null && panel.AreaName.text != "K9 KENNEL") panel.AreaName.text = "K9 KENNEL";
            AreaIcon icon = panel.AreaIconCreated;
            if (icon == null) return;
            // The paw in the game's own hex, as every area's symbol is.
            Sprite paw = Paw();
            if (paw != null && icon._areaIconImage != null && icon._areaIconImage.sprite != paw)
            {
                icon._areaIconImage.sprite = paw;
                icon._areaIconImage.preserveAspect = true;
            }
            string number = level.ToString("00");
            if (icon._currentAreaLevel != null && icon._currentAreaLevel.text != number) icon._currentAreaLevel.text = number;
        }

        private static Sprite Paw()
        {
            if (sprite != null) return sprite;
            string path = Path.Combine(Plugin.AssetFolder, "kennel_icon.png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "K9 Kennel icon" };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) { UnityEngine.Object.Destroy(texture); return null; }
            texture.wrapMode = TextureWrapMode.Clamp;
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
            return sprite;
        }

        // The kennel's entry in the area bar along the bottom: a copy of the bar's own, right after the Rest Space's.
        private static void Bar(int level)
        {
            if (bar == null)
            {
                if (Time.unscaledTime < nextBar) return;
                nextBar = Time.unscaledTime + 1;
                bar = UnityEngine.Object.FindObjectOfType<AreasPanel>(true);
                if (bar == null) return;
            }
            if (bar._areaPanelsContainer == null) return;
            // The Rest Space's entry, looked for twice a second rather than every frame.
            if (barRest == null || Time.unscaledTime >= nextBarRest)
            {
                nextBarRest = Time.unscaledTime + .5f;
                barRest = bar._areaPanelsContainer.GetComponentsInChildren<AreaPanel>(true)
                    .FirstOrDefault(p => p != barEntry && p.Data?.Template?.Type == EAreaType.RestSpace);
            }
            AreaPanel rest = barRest;
            if (rest == null) return;
            if (barEntry == null)
            {
                barEntry = UnityEngine.Object.Instantiate(bar._areaPanelTemplate, bar._areaPanelsContainer, false);
                barEntry.name = "K9 Kennel area entry";
                var overlay = screen._hideoutScreenOverlay;
                barEntry.Show(rest.Data, p => KennelUi.OpenKennel(overlay, p.Data));
                HideoutExtensionsLink.Unmovable(barEntry.gameObject, "Move the kennel with the paw over it on the overview.");
            }
            if (!barEntry.gameObject.activeSelf) barEntry.gameObject.SetActive(true);
            // The game re-sorts the bar as areas change: keep the kennel beside the Rest Space.
            int after = rest.transform.GetSiblingIndex() + (barEntry.transform.GetSiblingIndex() < rest.transform.GetSiblingIndex() ? 0 : 1);
            if (barEntry.transform.GetSiblingIndex() != after) barEntry.transform.SetSiblingIndex(after);
            Dress(barEntry, level);
        }
        private static float nextBar, nextBarRest;
        private static AreaPanel barRest;

        public static void Close()
        {
            if (barEntry != null)
            {
                try { barEntry.Close(); } catch (Exception) { }
                if (barEntry != null) UnityEngine.Object.Destroy(barEntry.gameObject);
            }
            barEntry = null; bar = null; barRest = null;
            if (panel != null)
            {
                try { panel.Close(); } catch (Exception) { }
                if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            }
            panel = null; screen = null;
        }
    }

    /// <summary>
    /// The kennel's furniture, copied from props the hideout scene already has (the pet bed and bowls from the rest space
    /// and kitchen, a cabinet, a food barrel, the med station's metal table and medical case), so it looks like the rest
    /// of the hideout. They're found by their first-detail mesh, even where the area they belong to isn't built (its
    /// objects are only switched off). A copy keeps nothing but the model: its scripts, colliders and the rest are
    /// stripped before it is switched on. The originals are only read. A prop the hideout doesn't have returns null and
    /// the kennel uses its older models or simple shapes.
    /// </summary>
    internal static class HideoutProps
    {
        public const string Bed = "Pet_bed_LOD0", Bowl = "Pet_bowl_LOD0", FoodBag = "Pet_feed_LOD0", Barrel = "Food_barrel_closed_LOD0",
                            Cabinet = "Old_Electrical_Cabinet_C_LOD0", Table = "metal_table001", MedCase = "Medical_case_LOD0",
                            MedSupplies = "item_barter_medical_pile_LOD0";
        private static readonly HashSet<string> Wanted = new HashSet<string> { Bed, Bowl, FoodBag, Barrel, Cabinet, Table, MedCase, MedSupplies };

        // The pet bed's inside floor, in its own units (measured from the model), for the cushion the dog lies on.
        public const float BedFloor = .032f;

        // Hideout models are made Z-up with their front to -Y: this stands one up with its front to the kennel's front (+Z).
        private static readonly Quaternion Upright = Quaternion.Euler(-90, 0, 0);

        private static readonly Dictionary<string, MeshFilter> sources = new Dictionary<string, MeshFilter>();
        private static int scannedFrame = -1;

        private static MeshFilter Source(string mesh)
        {
            if (sources.TryGetValue(mesh, out var found) && found != null) return found;
            if (scannedFrame == Time.frameCount) return null;
            // A new hideout visit (or the first): look through the scene's meshes once.
            scannedFrame = Time.frameCount;
            sources.Clear();
            foreach (var filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                var shared = filter.sharedMesh;
                if (shared == null || !Wanted.Contains(shared.name) || sources.ContainsKey(shared.name)) continue;
                if (filter.name.StartsWith("K9 ", StringComparison.Ordinal) || filter.GetComponentInParent<HideoutCopy>(true) != null) continue;
                sources[shared.name] = filter;
            }
            if (sources.Count < Wanted.Count)
                foreach (string name in Wanted) if (!sources.ContainsKey(name)) Plugin.Log.LogWarning($"Hideout prop {name} not found; the kennel uses a stand-in.");
            return sources.TryGetValue(mesh, out found) ? found : null;
        }

        /// <summary>
        /// A copy of the prop, standing upright on <paramref name="parent"/>'s floor at <paramref name="position"/>,
        /// facing its front turned by <paramref name="turn"/> degrees, at <paramref name="scale"/> (along the model's own
        /// width, depth and height), its base at <paramref name="height"/>. <paramref name="bounds"/> is where the prop's
        /// main mesh ended up, in the parent's frame. Parts whose mesh name starts with one of <paramref name="leaveOut"/>
        /// are dropped.
        /// </summary>
        public static Transform Spawn(string mesh, Transform parent, Vector3 position, out Bounds bounds, float turn = 0,
                                      Vector3? scale = null, float height = 0, params string[] leaveOut)
        {
            bounds = default;
            var source = Source(mesh);
            if (source == null) return null;
            // The prop: the LOD group the mesh belongs to, or the mesh's own object when it has none.
            var group = source.GetComponentInParent<LODGroup>(true);
            Transform original = group != null ? group.transform : source.transform;
            try
            {
                // Copied into a switched-off holder so none of its scripts wake before they're gone.
                var holder = new GameObject("K9 " + mesh.Replace("_LOD0", "")).transform;
                holder.gameObject.SetActive(false);
                holder.SetParent(parent, false);
                holder.localPosition = position; holder.localRotation = Quaternion.Euler(0, turn, 0);
                holder.gameObject.AddComponent<HideoutCopy>();
                Transform copy = UnityEngine.Object.Instantiate(original.gameObject, holder, false).transform;
                copy.name = original.name;
                copy.gameObject.SetActive(true);
                Strip(copy, leaveOut);

                // Upright: the main mesh's rotation within the prop is undone, then the model stood up.
                Quaternion within = Quaternion.Inverse(original.rotation) * source.transform.rotation;
                copy.localPosition = Vector3.zero;
                copy.localRotation = Upright * Quaternion.Inverse(within);
                Vector3 own = Divide(original.lossyScale, source.transform.lossyScale);
                copy.localScale = Vector3.Scale(scale ?? Vector3.one, own);

                // Stood on the floor, centred on the spot.
                Transform main = Find(copy, mesh) ?? copy;
                bounds = Measure(main.GetComponent<MeshFilter>()?.sharedMesh, main.localToWorldMatrix, holder.worldToLocalMatrix);
                copy.localPosition = new Vector3(-bounds.center.x, height - bounds.min.y, -bounds.center.z);
                bounds = Measure(main.GetComponent<MeshFilter>()?.sharedMesh, main.localToWorldMatrix, parent.worldToLocalMatrix);

                holder.gameObject.SetActive(true);
                return holder;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't copy hideout prop {mesh}: {e.Message}");
                return null;
            }
        }

        // Keep the look only: meshes, renderers and LOD groups.
        private static void Strip(Transform copy, string[] leaveOut)
        {
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
            {
                string name = filter.sharedMesh != null ? filter.sharedMesh.name : "";
                foreach (string part in leaveOut)
                    if (name.StartsWith(part, StringComparison.Ordinal) && filter.transform != copy) { UnityEngine.Object.DestroyImmediate(filter.gameObject); break; }
            }
            // Scripts first: some insist on the colliders or bodies they use.
            for (int pass = 0; pass < 3; pass++)
                foreach (var component in copy.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform || component is MeshFilter || component is MeshRenderer || component is LODGroup) continue;
                    if (pass == 0 && !(component is MonoBehaviour)) continue;
                    try { UnityEngine.Object.DestroyImmediate(component); } catch (Exception) { }
                }
            // The game may have hidden the original's renderers (culling); the copy shows.
            foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true)) { renderer.enabled = true; renderer.forceRenderingOff = false; }
            foreach (var group in copy.GetComponentsInChildren<LODGroup>(true)) group.enabled = true;
        }

        private static Transform Find(Transform copy, string mesh)
        {
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && filter.sharedMesh.name == mesh) return filter.transform;
            return null;
        }

        private static Bounds Measure(Mesh mesh, Matrix4x4 meshToWorld, Matrix4x4 worldToFrame)
        {
            if (mesh == null) return new Bounds(Vector3.zero, Vector3.zero);
            Bounds local = mesh.bounds;
            Matrix4x4 m = worldToFrame * meshToWorld;
            var result = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                result.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return result;
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0 ? a.x / b.x : 1, b.y != 0 ? a.y / b.y : 1, b.z != 0 ? a.z / b.z : 1);

        /// <summary>Marks the kennel's copies, so they're never mistaken for the hideout's own props.</summary>
        private sealed class HideoutCopy : MonoBehaviour { }
    }

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

    /// <summary>
    /// With Hideout Extensions installed, the kennel moves from the overview like the hideout's areas: right-click the
    /// paw, and the kennel follows the mouse until it's put down where it fits. Without that mod nothing here runs (its
    /// types are only touched inside methods that are called once it's known to be loaded).
    /// </summary>
    internal static class HideoutExtensionsLink
    {
        private const string Id = "jrs.hideoutextensions";
        private static bool? present;
        private static GameObject offeredIcon;
        private static Transform offeredRoot;
        private static bool failed;

        private static bool Present => present ?? (present = Chainloader.PluginInfos.ContainsKey(Id)).Value;

        /// <summary>Offers the kennel (once per icon and kennel) for moving.</summary>
        internal static void Kennel(GameObject icon, Transform kennel, Companion companion)
        {
            if (failed || !Present || icon == null || kennel == null || companion == null) return;
            if (icon == offeredIcon && kennel == offeredRoot) return;
            try { Offer(icon, kennel, companion); offeredIcon = icon; offeredRoot = kennel; }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("The kennel can't be moved from the overview: " + e.Message); }
        }

        /// <summary>
        /// An icon that's a copy of the Rest Space's but moves nothing (the paw before the kennel is built, the kennel's
        /// entry in the area bar): without this, right-clicking it would pick up the Rest Space.
        /// </summary>
        internal static void Unmovable(GameObject icon, string reason)
        {
            if (failed || !Present || icon == null || refused.Contains(icon)) return;
            try { Refuse(icon, reason); refused.Add(icon); }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("The kennel's icon couldn't be kept from moving the Rest Space (update Hideout Extensions): " + e.Message); }
        }
        private static readonly System.Collections.Generic.HashSet<GameObject> refused = new System.Collections.Generic.HashSet<GameObject>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Refuse(GameObject icon, string reason) => HideoutExtensions.HideoutExtensionsApi.AddUnmovable(icon, reason);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Offer(GameObject icon, Transform kennel, Companion companion)
        {
            HideoutExtensions.HideoutExtensionsApi.AddMovable(new HideoutExtensions.MovableThing
            {
                Name = "K9 Kennel",
                Icon = icon,
                Root = kennel,
                Check = companion.KennelCheck,
                Placed = companion.KennelPlacedByHand,
            });
        }
    }
}
