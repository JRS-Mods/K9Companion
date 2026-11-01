using System;
namespace K9Companion
{
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
}
