using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace K9Companion
{
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
}
