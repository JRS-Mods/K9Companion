using System;

namespace K9Companion
{
    internal enum Aggression { Passive, Defensive, Aggressive }

    internal enum DogCamQuality { Low, Medium, High }

    internal enum DogCamPlace { NextToHealth, TopLeft, TopRight }

    internal enum DogOrder { OverThere, ComeHere, Sit, LieDown, OpenBag, Feed, Water, GoToBed, Stay, Follow, Search, FetchMeds, FetchFood, AttackMode, ApproachMode }

    // How it goes for an enemy. Auto: straight at one really close, stalking one further off, changing as the distance does.
    internal enum DogApproach { Auto, Stealth, Direct }

    /// <summary>
    /// What the dog is actually allowed to do, worked out in one place from the raw BepInEx values. Every subsystem
    /// (hitboxes, AI targeting, biting, the Y-menu commands) reads this instead of the individual settings, so they cannot
    /// disagree: immortality is simply "combat is off", and nothing combat-related survives CombatMode = false.
    /// </summary>
    internal struct DogRules
    {
        public bool Combat;
        public bool Immortal;
        public bool CanBeTargeted;
        public bool CanAttack;      // explicit "Over There" orders
        public bool AutoEngage;     // picks its own targets (Defensive / Aggressive)
        public Aggression Aggression;
        public float EngageDistance, BitesToKill, BossBitesToKill, Health, BiteCooldown;

        public const float DefaultHealth = 300f, DefaultBites = 2.5f, DefaultBossBites = 4f, DefaultEngage = 25f, DefaultCooldown = 1.5f;

        // How much of its health a downed dog gets back when it's revived; another treatment does the rest.
        public const float RevivePercent = 35f;

        // Speed with a load in its jacket: full speed up to the limit, then slower in proportion, never below half. No limit at 0.
        public static float CarrySpeed(float load, float limit) => limit <= 0 || load <= limit ? 1f : Math.Max(.5f, limit / load);

        public static DogRules Resolve(bool combatMode, Aggression aggression,
                                       float engageDistance, float bitesToKill, float bossBitesToKill, float health, bool inRaid,
                                       float biteCooldown = DefaultCooldown)
        {
            // The hideout never has combat, whatever the setting says.
            bool combat = combatMode && inRaid;
            var rules = new DogRules
            {
                Combat = combat,
                Immortal = !combat,
                CanBeTargeted = combat,
                Aggression = combat ? aggression : Aggression.Passive,
                EngageDistance = Sane(engageDistance, 5, 60, DefaultEngage),
                BitesToKill = Sane(bitesToKill, 1, 10, DefaultBites),
                BossBitesToKill = Sane(bossBitesToKill, 1, 20, DefaultBossBites),
                // Always finite and above zero, so a dog in combat mode can always be killed.
                Health = Sane(health, 1, 1000, DefaultHealth),
                BiteCooldown = Sane(biteCooldown, .8f, 5, DefaultCooldown)
            };
            // Every mode obeys an explicit order; Passive just never starts a fight on its own.
            rules.CanAttack = combat;
            rules.AutoEngage = combat && rules.Aggression != Aggression.Passive;
            return rules;
        }

        // How many bites one enemy takes, rolled once for it: a whole number as it is, and a fraction a mix of the two
        // either side (2.5: two bites for half of them, three for the rest).
        public static int Bites(float setting, float roll)
        {
            int whole = (int)Math.Floor(setting);
            return Math.Max(1, whole + (roll < setting - whole ? 1 : 0));
        }

        // Damage per bite to the head, so that exactly that many bites kill it (a little over, against rounding).
        public static float ThroatBite(float headMaximum, int bites) => Math.Max(1f, headMaximum) / Math.Max(1, bites) + .01f;

        // Why a wheel command is greyed out, or null when it can be given. Come Here and Sit are always available.
        public string Unavailable(DogOrder order, bool hideout)
        {
            switch (order)
            {
                case DogOrder.OverThere:
                    // Without combat mode it sends the dog to where you aim, to wait there.
                    return hideout ? "Raid only" : null;
                case DogOrder.GoToBed:
                    return hideout ? null : "Hideout only";
                case DogOrder.Search:
                case DogOrder.FetchMeds:
                case DogOrder.FetchFood:
                    return "No longer a command";   // the loot search and fetching were taken out in 0.30
                default:
                    return null;
            }
        }

        private static float Sane(float value, float min, float max, float fallback)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
    }

    /// <summary>What a K9 veterinary kit does for the dog: a full kit its whole health, a part-used one its share.</summary>
    internal static class VetMath
    {
        // At a kennel with a veterinary station the kit goes half as far again.
        public const float StationBonus = 1.5f;

        // left and max are the kit's resource, wanted the percent of its health the dog is missing. Only what the dog
        // needs is used; lastOfIt when nothing worth keeping would remain (the kit goes).
        public static void Portion(float left, float max, float wanted, float multiplier, out float gives, out float use, out bool lastOfIt)
        {
            gives = use = 0; lastOfIt = false;
            if (left <= 0 || max <= 0 || wanted <= 0 || multiplier <= 0) return;
            float holds = left / max * 100 * multiplier;
            gives = Math.Min(wanted, holds);
            use = Math.Min(left, (float)Math.Ceiling(gives / multiplier / 100 * max));
            lastOfIt = left - use < 1;
        }
    }

    /// <summary>When Auto goes straight in. It holds to what it's doing near the line, so it doesn't flick between the two.</summary>
    internal static class ApproachRule
    {
        // Rushing, it gives up only once the target is this much further off than it started rushing at.
        public const float GiveWay = 10f;

        public static bool GoDirect(float distance, float rushWithin, bool rushingNow) => distance <= (rushingNow ? rushWithin + GiveWay : rushWithin);
    }
}
