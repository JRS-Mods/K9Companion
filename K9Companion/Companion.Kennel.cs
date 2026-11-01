using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Hideout;
using UnityEngine;

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
}
