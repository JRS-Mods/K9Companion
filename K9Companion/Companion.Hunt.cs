using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace K9Companion
{
    /// <summary>
    /// A hunt (Over There with nobody to aim at): the dog runs to where you aimed, up to the ordered attack range, and
    /// searches the area by sight and scent. It picks the places worth checking within 20 m (inside buildings, behind
    /// cover, other floors), spread apart, and works through them nearest first, stopping at each to sniff, then comes
    /// back to you. The first enemy it picks up on the way, at the spot or during the search, it goes for (as if you'd
    /// ordered it). Without combat mode it just goes there and waits.
    /// Where you aim is often not ground the dog can walk (a wall, a roof, a fence): the nearest walkable spot it can
    /// reach is found back along your line of aim.
    /// </summary>
    public sealed partial class Companion
    {
        private const float HuntReach = 2f, HuntSense = 25f, SearchArea = 20f, SearchSpacing = 5f, CheckReach = 1.2f, SweepLimit = 75f;
        private const int SearchPlaces = 7;
        // Nearer the spot than this the run out to it is on the plain path.
        private const float HuntPlainWithin = 14f;
        private Vector3? hunt;
        private bool huntWaits;
        private float huntArrived, nextHuntScan, sniffUntil;
        // The places it checks round the hunted spot, in the order it goes, and how far through them it is.
        private readonly List<Vector3> sweep = new List<Vector3>();
        private int sweepAt;
        // Until then it's running back to you from a hunt or an ordered fight, and isn't teleported home.
        private float homeUntil;
        // Stopped at a place, nose down.
        private bool Sniffing => hunt != null && sniffUntil > 0;

        private bool StartHunt(Player owner, Vector3 point, out string problem)
        {
            problem = "The dog can't get there.";
            Vector3 from = owner.Transform.position;
            if (Vector3.Distance(from, point) > Plugin.OrderReach)
            { problem = $"Too far away (limit {Mathf.RoundToInt(Plugin.OrderReach)} m)."; return false; }
            if (!Walkable(from, point, out Vector3 spot)) { Plugin.Log.LogInfo($"Over there: no walkable ground the dog can reach near {point}."); return false; }
            bool waits = !rules.CanAttack;
            SetCommand(Command.Follow);
            StopFight();
            hunt = spot; huntWaits = waits; huntArrived = 0; nextHuntScan = 0; sniffUntil = 0; lastProgress = Time.time;
            sweep.Clear(); sweepAt = 0;
            corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            ResetStalk();
            audio?.Play("bark");
            int metres = Mathf.RoundToInt(Vector3.Distance(from, spot));
            Say(waits ? $"Over there! ({metres} m)" : $"Over there! {Plugin.DogName.Value} goes hunting ({metres} m).");
            return true;
        }

        // The ground nearest the aimed point that the dog can walk to: the point itself, else stepping back towards you.
        private bool Walkable(Vector3 from, Vector3 point, out Vector3 spot)
        {
            spot = default;
            Vector3 back = from - point;
            float length = back.magnitude;
            for (float d = 0; d <= length; d += 2.5f)
            {
                Vector3 p = point + (length > .01f ? back / length * d : Vector3.zero);
                if (!NavMesh.SamplePosition(p, out NavMeshHit near, 4f, NavMesh.AllAreas) || !Reachable(near.position)) continue;
                spot = near.position;
                return true;
            }
            return false;
        }

        // True while the dog is on its way to (or working) the hunt area; the follow loop takes it there.
        private bool HuntWalking(Player owner, out Vector3 spot)
        {
            spot = default;
            if (hunt == null) return false;
            if (dead || attackTarget != null || command != Command.Follow) { hunt = null; return false; }
            spot = hunt.Value;
            if (!huntWaits && Time.time >= nextHuntScan)
            {
                nextHuntScan = Time.time + .4f;
                Player found = EnemyNear(owner, dog.transform.position, HuntSense);
                if (found != null)
                {
                    hunt = null;
                    Engage(found, true);
                    if (!stalking) audio?.Play("bark");
                    Say(Plugin.DogName.Value + " has found " + found.Profile.Nickname + "!");
                    return false;
                }
            }
            if (huntArrived == 0)
            {
                // On the way to the spot.
                if (Flat(dog.transform.position, spot) > HuntReach)
                {
                    if (Time.time - lastProgress > 8) { hunt = null; Say(Plugin.DogName.Value + " can't get there."); return false; }
                    return true;
                }
                if (huntWaits) { hunt = null; SetCommand(Command.Stay); Say(Plugin.DogName.Value + " waits there."); return false; }
                huntArrived = Time.time;
                ResetStalk();
                corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
                PlanSearch(spot);
            }
            if (sweepAt >= sweep.Count || Time.time - huntArrived > SweepLimit)
            {
                hunt = null; sweep.Clear(); homeUntil = Time.time + 60;
                Say("Nothing there. " + Plugin.DogName.Value + " is coming back.");
                return false;
            }
            // At a place: it stands and sniffs a moment (still sensing), then moves on. One it can't get to is skipped.
            if (sniffUntil > 0)
            {
                lastProgress = Time.time;
                if (Time.time < sniffUntil) { spot = dog.transform.position; return true; }
                sniffUntil = 0; sweepAt++;
                return HuntWalking(owner, out spot);
            }
            if (Flat(dog.transform.position, sweep[sweepAt]) <= CheckReach) { sniffUntil = Time.time + Random.Range(1.5f, 2.8f); spot = dog.transform.position; return true; }
            if (Time.time - lastProgress > 6) { sweepAt++; lastProgress = Time.time; return HuntWalking(owner, out spot); }
            spot = sweep[sweepAt];
            return true;
        }

        // The places worth checking round the spot: walkable ground within the area (on its floor and the floors above
        // and below), scored up for being under a roof (inside a building), out of sight of the spot (behind cover) and
        // on another floor. The best are taken that are well apart and the dog can reach, and visited nearest first.
        // Cheap tests first; only the best few candidates get the (dearer) path check.
        private void PlanSearch(Vector3 centre)
        {
            sweep.Clear(); sweepAt = 0; sniffUntil = 0;
            var candidates = new List<(Vector3 Point, float Score, bool Inside)>();
            Vector3 eye = centre + Vector3.up;
            for (float x = -SearchArea; x <= SearchArea; x += 3f)
                for (float z = -SearchArea; z <= SearchArea; z += 3f)
                {
                    if (x * x + z * z > SearchArea * SearchArea) continue;
                    foreach (float y in new[] { 0f, 3.5f, -3.5f, 7f })
                    {
                        if (!NavMesh.SamplePosition(centre + new Vector3(x, y, z), out NavMeshHit near, 1.5f, NavMesh.AllAreas)) continue;
                        Vector3 p = near.position;
                        if (candidates.Any(c => (c.Point - p).sqrMagnitude < 2.25f)) continue;
                        bool inside = Physics.Raycast(p + Vector3.up * .5f, Vector3.up, 8f, groundMask, QueryTriggerInteraction.Ignore);
                        bool hidden = Physics.Linecast(eye, p + Vector3.up * .6f, groundMask, QueryTriggerInteraction.Ignore);
                        bool floor = Mathf.Abs(p.y - centre.y) > 2.2f;
                        float score = (inside ? 2f : 0) + (hidden ? 1.5f : 0) + (floor ? 1f : 0) + Mathf.Min(Flat(p, centre), 12f) / 12f + Random.value * .3f;
                        candidates.Add((p, score, inside));
                    }
                }
            var chosen = new List<(Vector3 Point, bool Inside)>();
            int pathChecks = 0;
            foreach (var c in candidates.OrderByDescending(c => c.Score))
            {
                if (chosen.Count >= SearchPlaces || pathChecks >= 40) break;
                if (chosen.Any(k => (k.Point - c.Point).sqrMagnitude < SearchSpacing * SearchSpacing)) continue;
                pathChecks++;
                if (Reachable(c.Point)) chosen.Add((c.Point, c.Inside));
            }
            // Nearest first, each from the last.
            Vector3 at = dog.transform.position;
            while (chosen.Count > 0)
            {
                var next = chosen.OrderBy(k => (k.Point - at).sqrMagnitude).First();
                chosen.Remove(next); sweep.Add(next.Point); at = next.Point;
            }
            int indoors = sweep.Count(p => Physics.Raycast(p + Vector3.up * .5f, Vector3.up, 8f, groundMask, QueryTriggerInteraction.Ignore));
            Plugin.Log.LogInfo($"Hunt: searching {sweep.Count} place(s) round {centre} ({indoors} under a roof; {candidates.Count} candidates, {pathChecks} path checks).");
            if (sweep.Count > 0) Say($"{Plugin.DogName.Value} searches the area ({sweep.Count} places" + (indoors > 0 ? $", {indoors} inside" : "") + ").");
        }

        // The nearest enemy around a point that the dog can see, or smell close by, and get to.
        private Player EnemyNear(Player owner, Vector3 around, float radius)
        {
            if (world == null) return null;
            Player best = null; float bestDistance = float.MaxValue;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                float d = Vector3.Distance(p.Transform.position, around);
                if (d > radius || d >= bestDistance) continue;
                if (d > ScentRange && !ClearLine(HeadPoint, Body(p))) continue;
                best = p; bestDistance = d;
            }
            return best != null && Reachable(best.Transform.position) ? best : null;
        }
    }
}
