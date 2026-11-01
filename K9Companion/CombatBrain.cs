namespace K9Companion
{
    /// <summary>Whether a shot was at someone: its line passes this close, on its way out rather than behind the muzzle.</summary>
    internal static class Gunfire
    {
        public static bool PassesNear(UnityEngine.Vector3 from, UnityEngine.Vector3 direction, UnityEngine.Vector3 point, float within)
        {
            UnityEngine.Vector3 to = point - from;
            float along = UnityEngine.Vector3.Dot(to, direction);
            return along > 0 && (to - direction * along).sqrMagnitude <= within * within;
        }
    }

    internal enum CombatState { Idle, Chase, Strike, Recover, Return }

    /// <summary>What the dog can tell about its fight this frame. Filled in by the companion, judged by CombatBrain.</summary>
    internal struct CombatSense
    {
        public float Now;
        public bool TargetValid;   // alive, AI, still hostile to the owner
        public bool Reachable;     // a complete navmesh path exists (re-checked every second)
        public bool LineOfSight;   // nothing solid between the dog's head and the target's body
        public bool InReach;       // a biteable body part is within mouth reach, with no wall in between
        public bool CanFight;      // alive, standing, not flinching, not badly wounded
        public bool Progressing;   // has moved or been in contact recently (not stuck)
        public bool StrikeDone;    // the bite animation has finished
        public bool Stalking;      // working its way in out of sight: it knows where the target is by scent
        public float TargetFromOwner, DogFromOwner, Follow;
    }

    /// <summary>
    /// The dog's fight in five states: Idle, Chase the target, Strike (one bite animation), Recover (cooldown, facing the
    /// target) and Return to the owner. It holds no Unity state, so the rules can be tested on their own.
    /// </summary>
    internal sealed class CombatBrain
    {
        public const float LoseSightAfter = 5f, ChaseLimit = 30f, FightLimit = 60f;
        // A stalk is slower than a straight chase.
        public const float StalkChase = 70f, StalkFight = 100f;
        // Sent in on your order (perhaps far off, by scent): it keeps tracking longer before it gives up.
        public const float OrderedLoseSight = 15f, OrderedChase = 90f, OrderedFight = 120f;

        private float engagedAt, lastSeen, nextStrike;

        public CombatState State { get; private set; }
        public bool Ordered { get; private set; }
        // Why the last fight ended; null while fighting or when nothing needs saying.
        public string Reason { get; private set; }
        // True when the dog gave up on a target that is still alive, so it shouldn't pick it straight back up.
        public bool Abandoned { get; private set; }

        public void Engage(float now, bool ordered)
        {
            State = CombatState.Chase; Ordered = ordered; Reason = null; Abandoned = false;
            engagedAt = lastSeen = now;
        }

        public void Reset() { State = CombatState.Idle; Ordered = false; Reason = null; Abandoned = false; }

        public CombatState Update(in CombatSense s, float engageDistance, float cooldown)
        {
            if (State == CombatState.Idle) return State;
            if (State == CombatState.Return)
            {
                if (s.DogFromOwner <= s.Follow + 1.5f) State = CombatState.Idle;
                return State;
            }
            if (!s.TargetValid) return Leave("Target down.", false);
            if (s.LineOfSight || s.Stalking) lastSeen = s.Now;
            bool fighting = State == CombatState.Strike || State == CombatState.Recover;
            // engageDistance is the ordered range for an ordered fight (the companion passes whichever applies).
            float range = Ordered ? engageDistance : engageDistance * 1.5f;
            if (s.TargetFromOwner > range || s.DogFromOwner > range * 1.2f + 10) return Leave("Too far from you.", true);
            if (s.Now - engagedAt > (Ordered ? OrderedFight : s.Stalking ? StalkFight : FightLimit)) return Leave("Breaking off.", true);
            if (!fighting)
            {
                if (!s.Reachable) return Leave("Can't reach the target.", true);
                if (s.Now - lastSeen > (Ordered ? OrderedLoseSight : LoseSightAfter)) return Leave("Lost the target.", true);
                if (!s.Progressing) return Leave("Can't get to the target.", true);
                if (s.Now - engagedAt > (Ordered ? OrderedChase : s.Stalking ? StalkChase : ChaseLimit)) return Leave("Gave up the chase.", true);
            }

            bool canBite = s.CanFight && s.InReach && s.LineOfSight;
            switch (State)
            {
                case CombatState.Chase:
                    if (canBite) State = s.Now >= nextStrike ? CombatState.Strike : CombatState.Recover;
                    break;
                case CombatState.Strike:
                    // A flinch or a fall cancels the bite; either way the cooldown starts.
                    if (!s.CanFight || s.StrikeDone) { State = CombatState.Recover; nextStrike = s.Now + cooldown; }
                    break;
                case CombatState.Recover:
                    if (!s.InReach || !s.LineOfSight) State = CombatState.Chase;
                    else if (canBite && s.Now >= nextStrike) State = CombatState.Strike;
                    break;
            }
            return State;
        }

        private CombatState Leave(string reason, bool abandoned)
        {
            State = CombatState.Return; Reason = reason; Abandoned = abandoned;
            return State;
        }
    }
}
