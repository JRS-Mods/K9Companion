using EFT;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Getting about the hideout, which has no navigation mesh: the dog walks straight across clear, continuous floor,
    /// taking one turn when something is in the way. And when it's bored of its bed it gets up, wanders round the
    /// hideout (now and then coming over to you), and goes back to bed.
    /// </summary>
    public sealed partial class Companion
    {
        private const float DogRadius = .2f;
        private bool roaming;
        private int roamStops;
        private Vector3 roamSpot;
        private float nextRoam, roamPause, legStarted, offBedSince;
        private const float OffBedFor = 60f;

        private bool WalkingWithoutNavmesh => IsHideout && !navigationAvailable;

        // A route of one or two straight legs. The first point is 'from', the last is on the floor at 'to'.
        private bool HideoutRoute(Vector3 from, Vector3 to, out Vector3[] route)
        {
            route = null;
            if (!FloorNear(to, .6f, out Vector3 end)) end = to;
            if (Walkable(from, end)) { route = new[] { from, end }; return true; }
            float best = float.MaxValue; Vector3 turn = default;
            foreach (Vector3 around in new[] { from, end })
                for (float radius = 1.2f; radius <= 3.6f; radius += 1.2f)
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * 22.5f * Mathf.Deg2Rad;
                        if (!FloorNear(around + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius, .5f, out Vector3 corner)) continue;
                        float length = Flat(from, corner) + Flat(corner, end);
                        if (length < best && Walkable(from, corner) && Walkable(corner, end)) { best = length; turn = corner; }
                    }
            if (best == float.MaxValue) return false;
            route = new[] { from, turn, end };
            return true;
        }

        // Nothing solid at the dog's body height along the way, and floor under every step with no drop or wall of steps.
        private static bool Walkable(Vector3 a, Vector3 b)
        {
            Vector3 flat = b - a; flat.y = 0;
            float length = flat.magnitude;
            if (length < .05f) return true;
            Vector3 direction = flat / length;
            Vector3 low = a + Vector3.up * (.25f + DogRadius), high = a + Vector3.up * (.9f - DogRadius);
            foreach (RaycastHit hit in Physics.CapsuleCastAll(low, high, DogRadius, direction, length, Solid, QueryTriggerInteraction.Ignore))
                if (hit.distance > 0 && !Ignored(hit.collider)) return false;
            float y = a.y;
            for (float d = .35f; d < length + .35f; d += .35f)
            {
                Vector3 p = a + direction * Mathf.Min(d, length);
                if (!FloorNear(new Vector3(p.x, y, p.z), .3f, out Vector3 floor)) return false;
                y = floor.y;
            }
            return Mathf.Abs(y - b.y) < .5f;
        }

        // ---- wandering ----------------------------------------------------------------------------------------------

        // Called every frame in the hideout: a dog that has rested on its bed a while gets up for a wander. One lying
        // down anywhere else (told to, or left behind when its kennel was moved) goes back to its bed after a while.
        private void TickRoam()
        {
            if (!IsHideout || roaming || !Plugin.Roam.Value || decor?.Home == null || dead) return;
            bool lying = command == Command.Down && !bedTrip && meal == MealSource.None && actionClip == null
                         && (stance == Stance.Lying || stance == Stance.Sleeping);
            bool onBed = Flat(dog.transform.position, decor.Home.position) < .8f;
            if (lying && !onBed)
            {
                if (offBedSince == 0) offBedSince = Time.time;
                else if (Time.time - offBedSince > OffBedFor) { offBedSince = 0; GoToBed(true); }
            }
            else offBedSince = 0;
            bool resting = lying && onBed;
            if (!resting) { nextRoam = Mathf.Max(nextRoam, Time.time + 20); return; }
            if (nextRoam == 0) nextRoam = Time.time + Random.Range(45f, 90f);
            if (Time.time < nextRoam) return;
            SetCommand(Command.Stay); WakeUp();
            roaming = true; roamStops = Random.Range(2, 5);
            // Out of the shelter by its door first (never through its walls).
            if (decor.DoorStep is Vector3 door && FloorNear(door, .5f, out Vector3 step))
            { roamSpot = step; roamPause = Time.time; legStarted = lastProgress = Time.time; roamStops++; return; }
            if (!NextRoamSpot()) EndRoam();
        }

        // True while the dog is walking to its next stop; between stops it stands, sniffs about and sits.
        private bool RoamWalking(out Vector3 spot)
        {
            spot = roamSpot;
            if (!roaming) return false;
            if (Flat(dog.transform.position, roamSpot) > .45f)
            {
                if (Time.time - lastProgress < 5 && Time.time - legStarted < 25) return true;
                // Stuck or taking too long: try somewhere else.
                if (NextRoamSpot()) { spot = roamSpot; return true; }
                EndRoam(); return false;
            }
            if (roamPause == 0) roamPause = Time.time + Random.Range(5f, 12f);
            if (Time.time < roamPause) return false;
            if (--roamStops <= 0 || !NextRoamSpot()) { EndRoam(); return false; }
            spot = roamSpot; WakeUp();
            return true;
        }

        private bool NextRoamSpot()
        {
            roamPause = 0; legStarted = Time.time; lastProgress = Time.time;
            Vector3 from = dog.transform.position;
            Vector3 home = decor?.Home != null ? decor.Home.position : from;
            Player owner = CurrentPlayer;
            // Sometimes over to you, to say hello.
            if (owner != null && Random.value < .35f && Flat(owner.Transform.position, from) < 15)
            {
                Vector3 toDog = from - owner.Transform.position; toDog.y = 0;
                Vector3 hello = owner.Transform.position + (toDog.sqrMagnitude > .01f ? toDog.normalized : owner.Transform.forward) * 1.2f;
                if (FloorNear(hello, .5f, out Vector3 near) && HideoutRoute(from, near, out _)) { roamSpot = near; return true; }
            }
            for (int i = 0; i < 12; i++)
            {
                Vector2 r = Random.insideUnitCircle.normalized * Random.Range(2f, 7f);
                if (!FloorNear(home + new Vector3(r.x, 0, r.y), .5f, out Vector3 spot)) continue;
                if (Blocked(spot + Vector3.up * .5f, new Vector3(.3f, .4f, .45f), Quaternion.identity)) continue;
                if (HideoutRoute(from, spot, out _)) { roamSpot = spot; return true; }
            }
            return false;
        }

        private void EndRoam()
        {
            roaming = false;
            nextRoam = Time.time + Random.Range(120f, 300f);
            GoToBed(true);
        }
    }
}
