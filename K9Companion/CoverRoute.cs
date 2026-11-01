using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace K9Companion
{
    /// <summary>Someone the dog would rather not be seen by.</summary>
    internal struct Watcher
    {
        public Vector3 Eye, Look;
        public float Weight, ViewCos, Range;
        public bool Prey;
    }

    /// <summary>What the route planner asks of the world (the raid's navigation mesh and colliders, see NavCoverWorld).</summary>
    internal interface ICoverWorld
    {
        bool Snap(Vector3 point, float radius, out Vector3 on);
        bool CanWalk(Vector3 from, Vector3 to);
        // Something solid is in the way.
        bool Blocked(Vector3 from, Vector3 to);
        // Bushes or the like are in the way: not cover from a bullet, but harder to be seen through.
        bool Screened(Vector3 from, Vector3 to);
        bool Path(Vector3 from, Vector3 to, List<Vector3> corners);
        float EdgeDistance(Vector3 point);
    }

    /// <summary>
    /// A way to somewhere that keeps out of sight. The navigation mesh's shortest path is widened into a band of walkable
    /// points either side of it; each point costs more the more the watchers can see of a dog standing there (out of their
    /// view it's nearly free, in the open in front of them it's dear) and a search finds the cheapest way through. So the
    /// dog goes round the far side of a wall, along a hedge or behind a building when that's worth the extra distance, and
    /// straight when there's nothing to hide behind.
    /// It works a little each frame, so a long route never costs a stall. Holds no Unity objects.
    /// </summary>
    internal sealed class CoverRoute
    {
        private const int Sides = 5;
        private const float MinSpacing = 3f, MaxSpacing = 6f, StationsWanted = 36f;
        private const float ExposedCost = 8f, CloseToOtherEnemy = 8f, OtherEnemyCost = 5f, EdgeHug = 1.2f, EdgeBonus = .15f, NotSeen = .12f;
        // Inside this distance of the end it's going to rush anyway, so being seen matters less.
        private const float RushZone = 10f;
        private const float DogLow = .45f, DogHigh = .7f, CheapestMetre = .8f;

        private enum Stage { Sampling, Searching, Smoothing, Done }

        private struct Node { public Vector3 P; public float Cost, Exposure; public int Row, Column; }

        private readonly List<Vector3> spine = new List<Vector3>();
        private readonly List<Vector3> stations = new List<Vector3>();
        private readonly List<Vector3> sideways = new List<Vector3>();
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<Watcher> eyes = new List<Watcher>();
        private readonly List<int> found = new List<int>();
        private readonly List<Vector3> smoothed = new List<Vector3>();
        private readonly Stopwatch clock = new Stopwatch();
        private ICoverWorld world;
        private Stage stage = Stage.Done;
        private int[] grid;
        private float spacing;
        private int rows, sampleRow, sampleColumn, start, goal, smoothAt;
        // The search.
        private float[] cost;
        private int[] came;
        private bool[] closed;
        private readonly List<KeyValuePair<float, int>> open = new List<KeyValuePair<float, int>>();

        // The shortest way, usable while the better one is being worked out.
        public Vector3[] Plain { get; private set; }
        // The route, first point where it starts; null until Step says it's done.
        public Vector3[] Points { get; private set; }
        public bool Busy => stage != Stage.Done;

        public bool Begin(ICoverWorld surroundings, Vector3 from, Vector3 to, IList<Watcher> watchers)
        {
            world = surroundings; Points = null; Plain = null; stage = Stage.Done;
            eyes.Clear(); eyes.AddRange(watchers);
            if (!world.Snap(from, 1.5f, out Vector3 a) || !world.Snap(to, 2.5f, out Vector3 b)) return false;
            spine.Clear();
            if (!world.Path(a, b, spine) || spine.Count < 2) return false;
            Plain = spine.ToArray();
            MakeStations();
            nodes.Clear(); open.Clear();
            grid = new int[rows * (Sides * 2 + 1)];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
            sampleRow = sampleColumn = 0;
            stage = Stage.Sampling;
            return true;
        }

        // Works for about this long; true once it's finished (Points has the route).
        public bool Step(float milliseconds)
        {
            if (stage == Stage.Done) return true;
            clock.Restart();
            long budget = (long)(milliseconds * Stopwatch.Frequency / 1000f);
            while (stage != Stage.Done && clock.ElapsedTicks < budget)
            {
                switch (stage)
                {
                    case Stage.Sampling: SampleOne(); break;
                    case Stage.Searching: SearchOne(); break;
                    case Stage.Smoothing: SmoothOne(); break;
                }
            }
            return stage == Stage.Done;
        }

        public void Cancel() { stage = Stage.Done; }

        // How much of a dog standing there the watchers can see, 0 (nothing) to 1 (right in front of them, close).
        internal static float Exposure(ICoverWorld world, Vector3 ground, IList<Watcher> eyes)
        {
            float worst = 0;
            Vector3 low = ground + Vector3.up * DogLow, high = ground + Vector3.up * DogHigh;
            for (int i = 0; i < eyes.Count; i++)
            {
                Watcher e = eyes[i];
                Vector3 to = low - e.Eye;
                float distance = to.magnitude;
                if (distance > e.Range || distance < .01f) continue;
                float facing = Vector3.Dot(e.Look, to / distance);
                float view = facing >= .77f ? 1f : facing >= e.ViewCos ? .6f : NotSeen;
                float amount = e.Weight * view * Mathf.Lerp(1f, .45f, distance / e.Range);
                if (amount <= worst) continue;
                // A dog is low: a crate or a low wall that wouldn't hide a man hides it. Both must be blocked, as bots look for either.
                if (world.Blocked(e.Eye, low) && world.Blocked(e.Eye, high)) continue;
                if (world.Screened(e.Eye, low)) amount *= .5f;
                if (amount > worst) worst = amount;
            }
            return worst;
        }

        private void MakeStations()
        {
            float length = 0;
            for (int i = 1; i < spine.Count; i++) length += Flat(spine[i - 1], spine[i]);
            spacing = Mathf.Clamp(length / StationsWanted, MinSpacing, MaxSpacing);
            stations.Clear(); sideways.Clear();
            stations.Add(spine[0]);
            float next = spacing;
            float walked = 0;
            for (int i = 1; i < spine.Count; i++)
            {
                float leg = Flat(spine[i - 1], spine[i]);
                while (leg > .001f && next <= walked + leg)
                {
                    if (length - next > spacing * .5f) stations.Add(Vector3.Lerp(spine[i - 1], spine[i], (next - walked) / leg));
                    next += spacing;
                }
                walked += leg;
            }
            stations.Add(spine[spine.Count - 1]);
            rows = stations.Count;
            for (int r = 0; r < rows; r++)
            {
                Vector3 along = stations[Mathf.Min(r + 1, rows - 1)] - stations[Mathf.Max(r - 1, 0)];
                along.y = 0;
                along = along.sqrMagnitude > .0001f ? along.normalized : Vector3.forward;
                sideways.Add(new Vector3(along.z, 0, -along.x));
            }
        }

        // One point of the band. It starts narrow at the dog and widens, and stays wide right up to the far end: coming in
        // from the side or from behind means reaching the target from anywhere around it.
        private void SampleOne()
        {
            if (sampleRow >= rows)
            {
                start = grid[Sides]; goal = grid[(rows - 1) * (Sides * 2 + 1) + Sides];
                BeginSearch();
                return;
            }
            int column = sampleColumn - Sides;
            bool wanted = Mathf.Abs(column) * spacing <= spacing * 2 + sampleRow * spacing * .8f;
            if (wanted)
            {
                Vector3 centre = stations[sampleRow];
                Vector3 p = centre;
                bool ok = column == 0 || (world.Snap(centre + sideways[sampleRow] * (column * spacing), spacing * .45f, out p) && Mathf.Abs(p.y - centre.y) < 2f);
                if (ok)
                {
                    float seen = Exposure(world, p, eyes);
                    float price = 1 + ExposedCost * seen * (Flat(p, stations[rows - 1]) < RushZone ? .35f : 1f);
                    for (int i = 0; i < eyes.Count; i++)
                        if (!eyes[i].Prey && (eyes[i].Eye - p).sqrMagnitude < CloseToOtherEnemy * CloseToOtherEnemy) { price += OtherEnemyCost; break; }
                    if (world.EdgeDistance(p) < EdgeHug) price -= EdgeBonus;
                    grid[sampleRow * (Sides * 2 + 1) + sampleColumn] = nodes.Count;
                    nodes.Add(new Node { P = p, Cost = price, Exposure = seen, Row = sampleRow, Column = sampleColumn });
                }
            }
            if (++sampleColumn > Sides * 2) { sampleColumn = 0; sampleRow++; }
        }

        private void BeginSearch()
        {
            int n = nodes.Count;
            cost = new float[n]; came = new int[n]; closed = new bool[n];
            for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; came[i] = -1; }
            cost[start] = 0;
            Push(Remaining(start), start);
            stage = Stage.Searching;
        }

        private float Remaining(int node) => Flat(nodes[node].P, nodes[goal].P) * CheapestMetre;

        private void Push(float priority, int node)
        {
            open.Add(new KeyValuePair<float, int>(priority, node));
            int i = open.Count - 1;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (open[up].Key <= open[i].Key) break;
                KeyValuePair<float, int> swap = open[up]; open[up] = open[i]; open[i] = swap;
                i = up;
            }
        }

        private int Pop()
        {
            int top = open[0].Value, end = open.Count - 1;
            open[0] = open[end]; open.RemoveAt(end);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, least = i;
                if (l < end && open[l].Key < open[least].Key) least = l;
                if (r < end && open[r].Key < open[least].Key) least = r;
                if (least == i) break;
                KeyValuePair<float, int> swap = open[least]; open[least] = open[i]; open[i] = swap;
                i = least;
            }
            return top;
        }

        private void SearchOne()
        {
            if (open.Count == 0) { Finish(Plain); return; }   // no way through the band: the plain path it is
            int at = Pop();
            if (closed[at]) return;
            closed[at] = true;
            if (at == goal) { Collect(); return; }
            Node here = nodes[at];
            for (int dr = -2; dr <= 2; dr++)
            {
                int row = here.Row + dr;
                if (row < 0 || row >= rows) continue;
                for (int dc = -2; dc <= 2; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    // Next rows for the most part; the second row out only straight on, to step over a gap.
                    if (Mathf.Abs(dr) == 2 && dc != 0) continue;
                    int column = here.Column + dc;
                    if (column < 0 || column > Sides * 2) continue;
                    int next = grid[row * (Sides * 2 + 1) + column];
                    if (next < 0 || closed[next]) continue;
                    Node there = nodes[next];
                    float length = Vector3.Distance(here.P, there.P);
                    float through = cost[at] + length * (here.Cost + there.Cost) * .5f;
                    if (through >= cost[next] || !world.CanWalk(here.P, there.P)) continue;
                    cost[next] = through; came[next] = at;
                    Push(through + Remaining(next), next);
                }
            }
        }

        private void Collect()
        {
            found.Clear();
            for (int at = goal; at >= 0; at = came[at]) found.Add(at);
            found.Reverse();
            smoothed.Clear(); smoothed.Add(nodes[found[0]].P);
            smoothAt = 0;
            stage = Stage.Smoothing;
        }

        // Straightens the route where that doesn't mean walking somewhere it can be seen and it wasn't before.
        private void SmoothOne()
        {
            int last = found.Count - 1;
            if (smoothAt >= last) { Finish(smoothed.ToArray()); return; }
            int reach = Mathf.Min(smoothAt + 5, last);
            Node from = nodes[found[smoothAt]];
            int best = smoothAt + 1;
            for (int k = reach; k > smoothAt + 1; k--)
            {
                Node to = nodes[found[k]];
                float worst = 0;
                for (int i = smoothAt; i <= k; i++) worst = Mathf.Max(worst, nodes[found[i]].Exposure);
                if (world.CanWalk(from.P, to.P) && Exposure(world, Vector3.Lerp(from.P, to.P, .5f), eyes) <= worst + .05f) { best = k; break; }
            }
            smoothed.Add(nodes[found[best]].P);
            smoothAt = best;
        }

        private void Finish(Vector3[] route)
        {
            Points = route;
            stage = Stage.Done;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0;
            return Vector3.Distance(a, b);
        }
    }
}
