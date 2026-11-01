using System.Collections.Generic;
using EFT.InventoryLogic;

namespace K9Companion
{
    /// <summary>
    /// What each jacket tier does for the dog in a raid. The jackets are containers, not the game's armour items, so the
    /// protection is applied by the dog's own hit handling (Companion.TakeHit): rounds that hit the chest or back are
    /// stopped or let through EFT-style, by armour class against the round's penetration, and wear the jacket down. Heavier
    /// tiers slow the dog. Same tiers as the server mod's DogGear.cs (sizes, weights and prices live there).
    /// </summary>
    internal static class DogArmour
    {
        internal sealed class Tier
        {
            public int Class; public float Speed, Durability;
            public Tier(int armourClass, float speed, float durability) { Class = armourClass; Speed = speed; Durability = durability; }
        }

        private static readonly Dictionary<string, Tier> Tiers = new Dictionary<string, Tier>
        {
            ["6a5d0c0c4fb9a3c5e1d0d061"] = new Tier(0, 1.00f, 0),    // tactical chest rig (black)
            ["6a5d0c0c4fb9a3c5e1d0d073"] = new Tier(3, .95f, 40),    // pocket vest (black)
            ["6a5d0c0c4fb9a3c5e1d0d071"] = new Tier(4, .91f, 50),    // tactical vest (olive)
            ["6a5d0c0c4fb9a3c5e1d60020"] = new Tier(5, .93f, 75),    // elite assault vest (camo): heavy protection, light build
            ["6a5d0c0c4fb9a3c5e1d60021"] = new Tier(4, .92f, 55),    // camera vest (coyote): full length, own camera
            // Taken out in 0.20 (the server swaps them for the vests above at game start); as they were till then.
            ["6a5d0c0c4fb9a3c5e1d0d074"] = new Tier(2, .98f, 30),
            ["6a5d0c0c4fb9a3c5e1d0d072"] = new Tier(5, .86f, 60),
        };

        // Repairs at the kennel (Working Dog Station and up): durability one item of each material puts back. Fabric
        // jackets (class 2-3) take any of them; the armoured tiers (class 4-5) only aramid or Cordura. Every repair costs
        // the jacket a little of its maximum (a tenth of what was put back), as armour repairs do in the game.
        internal static readonly Dictionary<string, float> RepairMaterials = new Dictionary<string, float>
        {
            ["5e2af4d286f7746d4159f07a"] = 25,   // Aramid fiber fabric
            ["5e2af41e86f774755a234b67"] = 15,   // Cordura polyamide fabric
            ["5e2af4a786f7746d3f3c3400"] = 10,   // Ripstop fabric
            ["5e2af29386f7746d4159f077"] = 6,    // KEKTAPE duct tape
        };
        internal const float RepairWear = .1f;

        internal static bool Repairs(Tier tier, string material)
            => tier != null && tier.Class > 0 && RepairMaterials.ContainsKey(material)
               && (tier.Class <= 3 || material == "5e2af4d286f7746d4159f07a" || material == "5e2af41e86f774755a234b67");

        internal static Tier Of(Item jacket) => jacket != null && Tiers.TryGetValue(jacket.TemplateId.ToString(), out Tier t) ? t : null;

        /// <summary>
        /// The jacket's durability as the game shows it (inspect window, item tooltip), set to the dog's wear on it: the
        /// game's own copy of the item only knows its template's full value.
        /// </summary>
        internal static void ShowWear(Item jacket)
        {
            Tier tier = Of(jacket);
            RepairableComponent repairable = (jacket as Vest)?.Repairable;
            if (tier == null || tier.Class <= 0 || repairable == null) return;
            K9.Wear wear = K9.WearOf(jacket.Id.ToString(), tier.Durability);
            repairable.MaxDurability = wear.Max;
            repairable.Durability = wear.Now;
        }

        /// <summary>
        /// Chance a round gets through: certain well above the armour, unlikely well below it, about 60% when evenly
        /// matched; worn armour counts for less (down to half its class at zero durability).
        /// </summary>
        internal static float PenetrationChance(int armourClass, float durability, float maxDurability, float penetration)
        {
            if (armourClass <= 0 || maxDurability <= 0 || durability <= 0) return 1;
            float armour = armourClass * 10f * (.5f + .5f * durability / maxDurability);
            float margin = (penetration - armour) / 15f;
            return margin >= 0 ? .6f + .4f * Clamp01(margin) : .6f * (1 - Clamp01(-margin));
        }

        // Durability lost to one round: its penetration scaled by its armour-damage rating (as a percentage).
        internal static float Wear(float penetration, float armourDamage)
            => System.Math.Max(1f, penetration * (armourDamage > 0 ? armourDamage / 100f : .5f) * .5f);

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
