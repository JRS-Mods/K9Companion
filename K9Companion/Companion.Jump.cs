using System;
using UnityEngine;
using UnityEngine.AI;

namespace K9Companion
{
    /// <summary>
    /// Jumping (raids only): over a fence, a crate, a low wall or a gap, or down off a ledge, when that's the better way.
    /// The dog jumps when the way round is a long detour, when there's no way round at all, or when it's stuck against
    /// something low; teleporting back to you is still there for when it can't do even that. It's checked a few times a
    /// second and only then: a handful of rays.
    /// The three jumps are the dog's own animations (from a stand, a trot and a run). Their in-place versions keep the
    /// rise; how far each carries the dog, and when it leaves the ground and lands, was measured from the root-motion
    /// versions (tools/measure_jumps.py), so its feet leave and meet the ground where they should. A jump is stretched
    /// or shortened a little (0.6 to 1.6 times) to fit what's in the way.
    /// </summary>
    public sealed partial class Companion
    {
        private sealed class JumpClip
        {
            public string Name;
            // When it leaves the ground and lands (0-1 of the clip), and how far it has gone (m) at 17 even steps.
            public float TakeOff, Land;
            public float[] Travel;
            // How fast it's going as it comes out of the jump (a gait it goes straight on in).
            public Gait Exit;

            public float At(float u)
            {
                float x = Mathf.Clamp01(u) * (Travel.Length - 1);
                int i = Mathf.Min((int)x, Travel.Length - 2);
                return Mathf.Lerp(Travel[i], Travel[i + 1], x - i);
            }
        }

        private static readonly JumpClip StandJump = new JumpClip
        {
            Name = "Jump_Stand", TakeOff = .256f, Land = .581f, Exit = Gait.WalkSlow,
            Travel = new[] { 0f, -.06f, -.105f, .031f, .309f, .608f, .94f, 1.287f, 1.584f, 1.849f, 2.096f, 2.323f, 2.527f, 2.696f, 2.775f, 2.778f, 2.778f }
        };
        private static readonly JumpClip TrotJump = new JumpClip
        {
            Name = "Jump_Trot", TakeOff = .324f, Land = .824f, Exit = Gait.Trot,
            Travel = new[] { 0f, .229f, .459f, .711f, .976f, 1.242f, 1.507f, 1.772f, 2.037f, 2.302f, 2.567f, 2.833f, 3.098f, 3.363f, 3.628f, 3.893f, 4.034f }
        };
        private static readonly JumpClip RunJump = new JumpClip
        {
            Name = "Jump_Run", TakeOff = .292f, Land = .833f, Exit = Gait.Run,
            Travel = new[] { 0f, .457f, .874f, 1.239f, 1.572f, 1.867f, 2.177f, 2.508f, 2.85f, 3.199f, 3.549f, 3.893f, 4.229f, 4.583f, 4.976f, 5.38f, 5.511f }
        };

        // Clears this much by the animation alone (its paws at the top of the jump); anything higher up to MaxObstacle is
        // cleared by lifting the whole dog a little more. All in metres at the dog's normal size.
        private const float ClearsAlone = .45f, MaxObstacle = .9f, StepOver = .35f, MaxStepUp = .7f, MaxDrop = 1.8f, Step = .2f;
        private const float StretchMin = .6f, StretchMax = 1.6f;

        private JumpClip jumpClip;
        private Vector3 jumpFrom, jumpDir;
        private float jumpStretch, jumpY0, jumpYLand, jumpLift, jumpYaw, nextJumpCheck;
        // Going up to something it means to jump (too far off to jump from where it stood): straight there, then over.
        private bool runUp;

        private bool Jumping => stance == Stance.Jump;

        // ---- when ---------------------------------------------------------------------------------------------------

        // Called while it's on its way somewhere (following, a fight, a hunt). Worth a jump: stuck for a couple of seconds,
        // no way there, or a way round much longer than the straight line.
        // On a route of its own (a stalk, a hunt) the long way round is on purpose: only being stuck or having no way counts.
        private bool TryJumpOnTheWay(Vector3 at, Vector3 target, float stop, bool ownRoute)
        {
            if (IsHideout || Time.time < nextJumpCheck || stalkLow) return false;
            nextJumpCheck = Time.time + .4f;
            float straight = Flat(at, target);
            if (straight < stop + 1f || Mathf.Abs(target.y - at.y) > 3f) return false;
            bool stuck = Time.time - lastProgress > 2.2f;
            // At the end of a run-up: over it now.
            bool ready = runUp && corner >= corners.Length;
            if (runUp && !ready && Time.time - lastProgress < 2.2f) return false;
            runUp = false;
            bool noWay = corners.Length == 0 && (target - pathTarget).sqrMagnitude < 1 && Time.time < pathExpires;
            float pathLength = corners.Length > corner ? PathLeft(at) : float.MaxValue;
            bool detour = !ownRoute && corners.Length > corner && pathLength > straight * 1.6f + 4f;
            if (!stuck && !noWay && !detour && !ready) return false;
            Vector3 toTarget = target - at; toTarget.y = 0;
            if (TryJump(at, toTarget, target, pathLength, false, out float obstacle)) return true;
            // At the end of a run-up with no jump after all: its route was only the run-up, so plan the way on now (it stood
            // there for the five seconds the run-up's leg was kept, then got lost).
            if (ready) { corners = Array.Empty<Vector3>(); corner = 0; legsRoute = false; nextPath = pathExpires = 0; }
            // Worth jumping, but it's a way off yet: up to it first, at a run, and over it from there.
            if (!ready && RunUp(at, toTarget, obstacle, target)) return false;
            // Stuck on the path itself: over whatever's in the way along it.
            if (stuck && corner < corners.Length)
            {
                Vector3 toCorner = corners[corner] - at; toCorner.y = 0;
                if (Vector3.Angle(toCorner, toTarget) > 15 && TryJump(at, toCorner, target, pathLength, false)) return true;
            }
            nextJumpCheck = Time.time + (stuck ? .6f : 1.2f);
            return false;
        }

        // A straight leg up to just short of what's in the way, if the floor there is clear; the jump is tried at its end.
        private bool RunUp(Vector3 at, Vector3 direction, float obstacle, Vector3 target)
        {
            if (float.IsNaN(obstacle) || obstacle < 1f || obstacle > 15f) return false;
            Vector3 to = at + direction.normalized * (obstacle - .5f);
            if (!FloorNear(to, .4f, out Vector3 floor) || !Walkable(at, floor, true)) return false;
            corners = new[] { at, floor }; corner = 1; legsRoute = true;
            pathTarget = target; pathExpires = nextPath = Time.time + 5f;
            runUp = true; lastProgress = Time.time; nextJumpCheck = Time.time + .1f;
            Plugin.Log.LogInfo($"Jump: running up to it ({obstacle:0.0} m).");
            return true;
        }

        // ---- whether it can -----------------------------------------------------------------------------------------

        // A jump along 'direction' from 'at'. With a target it must leave the dog better off (a shorter way there, or a way
        // where there was none); 'anyway' (your Space key, controlling the dog) takes any jump that lands safely.
        private bool TryJump(Vector3 at, Vector3 direction, Vector3? target, float pathLength, bool anyway) => TryJump(at, direction, target, pathLength, anyway, out _);

        // 'tooFar': how far off the thing in the way is, when it's the only reason there's no jump (NaN otherwise).
        private bool TryJump(Vector3 at, Vector3 direction, Vector3? target, float pathLength, bool anyway, out float tooFar)
        {
            tooFar = float.NaN;
            direction.y = 0;
            if (direction.sqrMagnitude < .01f || dog == null) return false;
            direction.Normalize();
            float s = Mathf.Max(.3f, Plugin.Scale.Value);
            if (!GroundAt(at, out Vector3 ground)) return false;
            float y0 = ground.y;
            // Turning a long way: from a stand, turning first. Otherwise the jump that goes with its pace.
            Vector3 heading = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            JumpClip moving = Vector3.Angle(heading, direction) > 30 || shownSpeed < 1.6f ? StandJump : shownSpeed < 5f ? TrotJump : RunJump;
            // On the move it takes off well before what's in the way; too close for that, it jumps from where it is.
            bool any = false;
            foreach (JumpClip clip in moving == StandJump ? new[] { StandJump } : new[] { moving, StandJump })
            {
                if (!Survey(at, y0, direction, clip.At(1) * StretchMax * s, s, out float near, out float far, out float top, out float wall)) return false;
                if (float.IsInfinity(near) && !anyway) return false;   // nothing in the way: no reason to jump
                // How much to stretch it: leaving the ground before what's in the way, landing past it.
                float up = clip.At(clip.TakeOff) * s, down = clip.At(clip.Land) * s;
                float most = float.IsInfinity(near) ? StretchMax : (near + .05f) / Mathf.Max(.05f, up);
                float least = float.IsInfinity(near) ? StretchMin : (far + .35f) / Mathf.Max(.05f, down);
                if (least > most || least > StretchMax || most < StretchMin)
                {
                    // Clearing it would take a longer jump than there is from here: nearer, it'd be a jump.
                    if (!any && !float.IsInfinity(near) && far - near < down * StretchMax - .6f) tooFar = near;
                    any = true;
                    continue;
                }
                float k = Mathf.Clamp(1f, Mathf.Max(least, StretchMin), Mathf.Min(most, StretchMax));
                float landAt = down * k, endAt = clip.At(1) * s * k;
                // A wall further on: the jump must be over before it (a running jump indoors), or a shorter one tried.
                if (endAt + DogRadius + .15f > wall)
                {
                    k = Mathf.Max(least, StretchMin);
                    landAt = down * k; endAt = clip.At(1) * s * k;
                    if (endAt + DogRadius + .15f > wall) continue;
                }
                Vector3 flat = new Vector3(at.x, y0, at.z);
                if (!LandingAt(flat + direction * landAt, y0, top, out Vector3 landing) || !LandingAt(flat + direction * endAt, y0, top, out Vector3 end)
                    || Mathf.Abs(landing.y - end.y) > .5f) return false;
                if (!ClearOverhead(flat + direction * (up * k), direction, landAt - up * k, Mathf.Max(top, Mathf.Max(y0, landing.y)) + .35f * s, s)) return false;
                if (!BodyFits(end) || DoorInTheWay(flat, end, out _)) return false;
                if (!(anyway || target == null || Better(end, target.Value, endAt, pathLength, at))) return false;
                tooFar = float.NaN;
                StartJump(clip, flat, direction, k, y0, landing.y, Mathf.Max(0, top - y0 - ClearsAlone * s + .08f));
                return true;
            }
            return false;
        }

        // Along the way: where the first thing too high to step over starts (near), where clear ground starts again past
        // it (far) and the highest point between; or the edge of a drop, and the ground below it. False if it's too high
        // to jump, or there's nowhere to land within reach.
        // 'wall': where something too high to jump stands past it all, if it does (the jump has to end short of it).
        private bool Survey(Vector3 at, float y0, Vector3 direction, float reach, float s, out float near, out float far, out float top, out float wall)
        {
            near = far = wall = float.PositiveInfinity; top = y0;
            float probe = y0 + (MaxObstacle + .6f) * s, last = y0;
            bool inside = false;
            for (float d = Step; d <= reach; d += Step)
            {
                Vector3 p = at + direction * d;
                float h = Physics.Raycast(new Vector3(p.x, probe, p.z), Vector3.down, out RaycastHit hit, (MaxObstacle + .6f + MaxDrop) * s, Solid, QueryTriggerInteraction.Ignore)
                          && !Ignored(hit.collider) ? hit.point.y : float.NegativeInfinity;
                // Higher than the dog can reach: not over that. Past open ground, it's only where the jump has to end.
                if (h >= y0 + MaxObstacle * s)
                {
                    if (inside) return false;
                    wall = d - Step * .5f;
                    return true;
                }
                // Too high to step over, or a hole too deep to climb out of: over it.
                if (h > y0 + StepOver * s || h < y0 - MaxDrop * s)
                {
                    if (!inside) near = d - Step * .5f;
                    if (h > top) top = h;
                    inside = true;
                    continue;
                }
                if (inside) { far = d; return true; }
                // The edge of a drop: down onto the ground below.
                if (last - h > StepOver * 2 * s) { near = d - Step; far = d; top = y0; return true; }
                last = h;
            }
            // Never back down within reach (a long gap) is no jump; nothing in the way at all is open ground.
            return !inside;
        }

        // Firm, fairly level ground where a paw comes down, not too far up or down from where it jumped.
        private bool LandingAt(Vector3 p, float y0, float top, out Vector3 landing)
        {
            landing = p;
            float from = Mathf.Max(y0, top) + 1.2f;
            if (!Physics.Raycast(new Vector3(p.x, from, p.z), Vector3.down, out RaycastHit hit, from - (y0 - MaxDrop) + .2f, Solid, QueryTriggerInteraction.Ignore)
                || Ignored(hit.collider) || hit.normal.y < .7f) return false;
            float rise = hit.point.y - y0;
            if (rise > MaxStepUp || rise < -MaxDrop) return false;
            landing = hit.point;
            return true;
        }

        // Nothing at body height over the top of the jump (a window frame, a pipe, a low ceiling).
        private static bool ClearOverhead(Vector3 from, Vector3 direction, float length, float height, float s)
        {
            Vector3 start = new Vector3(from.x, height + .25f * s, from.z);
            if (Physics.SphereCast(start, .22f * s, direction, out RaycastHit hit, Mathf.Max(.1f, length), Solid, QueryTriggerInteraction.Ignore) && !Ignored(hit.collider)) return false;
            return !Physics.Raycast(start, Vector3.up, .5f * s, Solid, QueryTriggerInteraction.Ignore);
        }

        // Room for the dog where it ends up.
        private static bool BodyFits(Vector3 floor)
        {
            foreach (Collider c in Physics.OverlapCapsule(floor + Vector3.up * (.25f + DogRadius), floor + Vector3.up * (.75f - DogRadius), DogRadius, Solid, QueryTriggerInteraction.Ignore))
                if (!Ignored(c)) return false;
            return true;
        }

        // It gets the dog somewhere: onto ground it can go on from, and a good deal shorter, or a way where there was none.
        private bool Better(Vector3 end, Vector3 target, float jumpLength, float pathLength, Vector3 at)
        {
            bool onMesh = NavMesh.SamplePosition(end, out NavMeshHit from, .8f, NavMesh.AllAreas);
            if (onMesh && NavMesh.SamplePosition(target, out NavMeshHit to, 2f, NavMesh.AllAreas))
            {
                var test = new NavMeshPath();
                if (NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, test) && test.status == NavMeshPathStatus.PathComplete)
                {
                    float after = 0;
                    for (int i = 1; i < test.corners.Length; i++) after += Flat(test.corners[i - 1], test.corners[i]);
                    return after + jumpLength < pathLength - 3f;
                }
            }
            // Off the navmesh (or the target is): just closer, and somewhere it can walk on from over the floor.
            return Flat(end, target) < Flat(at, target) - 1f && (onMesh || Flat(end, target) < OffMeshReach);
        }

        // Your jump key, playing the dog: always a jump, straight on, the one that goes with its pace (from a stand, a trot
        // or a run). No asking whether it's worth it, as the dog does on its own: it goes over what's low enough (a fence, a
        // crate) and comes down wherever the ground is. Only something too high to clear, or a shut door, cuts it short (it
        // would go through it), down to a jump on the spot.
        private void JumpNow(Vector3 direction)
        {
            direction.y = 0;
            if (direction.sqrMagnitude < .01f) direction = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            direction.Normalize();
            float s = Mathf.Max(.3f, Plugin.Scale.Value);
            Vector3 at = dog.transform.position;
            float y0 = GroundAt(at, out Vector3 ground) ? ground.y : at.y;
            JumpClip clip = shownSpeed < 1.6f ? StandJump : shownSpeed < 5f ? TrotJump : RunJump;
            float length = clip.At(1) * s;
            // Room before anything too high to go over.
            float room = length;
            Vector3 chest = new Vector3(at.x, y0 + (MaxObstacle + .2f) * s, at.z);
            foreach (RaycastHit hit in Physics.SphereCastAll(chest, DogRadius, direction, length + DogRadius, Solid, QueryTriggerInteraction.Ignore))
                if (hit.distance > 0 && !Ignored(hit.collider)) room = Mathf.Min(room, hit.distance - DogRadius);
            while (room > 0 && DoorInTheWay(at, at + direction * room, out _)) room -= .25f;
            room = Mathf.Max(0, room);
            float k = Mathf.Clamp01(room / Mathf.Max(.01f, length));
            // The highest thing it passes over, to lift it clear.
            float top = y0;
            for (float d = Step; d <= room; d += Step)
            {
                Vector3 p = at + direction * d;
                if (Physics.Raycast(new Vector3(p.x, y0 + (MaxObstacle + .1f) * s, p.z), Vector3.down, out RaycastHit hit, (MaxObstacle + .1f + MaxDrop) * s, Solid, QueryTriggerInteraction.Ignore)
                    && !Ignored(hit.collider) && hit.point.y > top) top = hit.point.y;
            }
            // Where it comes down: the ground at the end, however far below.
            Vector3 end = at + direction * room;
            float yLand = y0;
            if (Physics.Raycast(new Vector3(end.x, top + 1.2f, end.z), Vector3.down, out RaycastHit floor, top + 1.2f - (y0 - 6f), Solid, QueryTriggerInteraction.Ignore))
                yLand = floor.point.y;
            StartJump(clip, new Vector3(at.x, y0, at.z), direction, k, y0, yLand, Mathf.Max(0, top - y0 - ClearsAlone * s + .08f));
        }

        // ---- the jump -----------------------------------------------------------------------------------------------

        private void StartJump(JumpClip clip, Vector3 from, Vector3 direction, float stretch, float y0, float yLand, float lift)
        {
            jumpClip = clip; jumpFrom = from; jumpDir = direction; jumpStretch = stretch;
            jumpY0 = y0; jumpYLand = yLand; jumpLift = lift;
            jumpYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            stance = Stance.Jump; yawRate = 0; lean = 0;
            model.NextBlend(clip == StandJump ? .2f : .12f);
            model.Restart();
            nextJumpCheck = Time.time + 1.5f;
            Plugin.Log.LogInfo($"Jump: {clip.Name} x{stretch:0.00}, {Mathf.Max(0, jumpLift + ClearsAlone):0.00} m up, lands {yLand - y0:+0.00;-0.00} m.");
        }

        // The jump owns the frame: the clip plays, the dog travels as the animation does, over the arc to where it lands.
        private void TickJump(float dt)
        {
            float s = Mathf.Max(.3f, Plugin.Scale.Value);
            bool done = model.Tick(jumpClip.Name, 1, dt, false);
            float u = done ? 1 : model.Progress;
            // From a stand it turns to face the jump while it gathers itself.
            yaw = Mathf.MoveTowardsAngle(yaw, jumpYaw, (u < jumpClip.TakeOff ? 720 : 240) * dt);
            Vector3 p = jumpFrom + jumpDir * (jumpClip.At(u) * s * jumpStretch);
            if (u < jumpClip.TakeOff) p.y = jumpY0;
            else if (u < jumpClip.Land)
            {
                float a = (u - jumpClip.TakeOff) / (jumpClip.Land - jumpClip.TakeOff);
                // Up eased (it springs), down falling faster the further it drops.
                float along = jumpYLand >= jumpY0 ? a * a * (3 - 2 * a) : a * a;
                p.y = Mathf.Lerp(jumpY0, jumpYLand, along) + jumpLift * Mathf.Sin(a * Mathf.PI);
            }
            else
            {
                p.y = jumpYLand;
                if (GroundAt(p, out Vector3 under) && Mathf.Abs(under.y - jumpYLand) < .6f) p.y = under.y;
            }
            dog.transform.position = p;
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            lastProgress = Time.time; progressPoint = p;
            if (!done) return;
            stance = Stance.Stand;
            // On it goes at the pace it landed at.
            speed = shownSpeed = jumpClip == StandJump ? 0 : Locomotion.Speed(jumpClip.Exit, Plugin.Tuning);
            gait = jumpClip.Exit;
            corners = Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            jumpClip = null;
        }

        private void CancelJump()
        {
            if (stance == Stance.Jump) stance = Stance.Stand;
            jumpClip = null;
        }
    }
}
