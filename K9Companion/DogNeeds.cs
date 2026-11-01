using System;
using System.Collections.Generic;
using System.Globalization;

namespace K9Companion
{
    /// <summary>
    /// The dog's hunger and thirst (100 = full, 0 = starving/parched), its health between raids and the two hideout dishes (0-100 each).
    /// Pure data and rules, so they can be validated and tested away from the game.
    /// Needs never hurt the dog: low needs only slow it down in combat mode.
    /// </summary>
    internal sealed class DogNeeds
    {
        public float Hunger = 100, Thirst = 100, FoodDish, WaterDish;
        // Health between raids, 0-100 % of the dog's full health (added in 0.16: wounds from a combat raid carry over).
        public float Health = 100;
        // Where the kennel stands in the hideout (scene coordinates), once you've placed it.
        public bool HomeSet;
        public float HomeX, HomeY, HomeZ, HomeYaw;
        // Put there by hand (moved from the overview with Hideout Extensions): it stays, even outside the kennel's room.
        public bool HomeByHand;
        // Which bag the dog wore before 0.14 (read once by the server mod to put that bag on the dog): "" = the first, "none" = none.
        public string VestId = "";

        public const float Hungry = 30f;           // below this the dog asks for food and (in combat) slows down
        public const float EatsFromDishBelow = 60f; // hideout: the dog goes to its dishes on its own
        public const float EatsFromBagBelow = 20f;  // raid: the dog eats from its own jacket on its own
        public const float HideoutPace = .5f;       // resting in the hideout uses less than a raid

        // Dog food, then meat as the alternative. Values are hunger points for a whole, unopened item.
        public const string DogFood = "6a5d0c0c4fb9a3c5e1d0d062", DogMeat = "6a5d0c0c4fb9a3c5e1d0d064";
        public static readonly Dictionary<string, float> Food = new Dictionary<string, float>
        {
            [DogFood] = 55,
            [DogMeat] = 40,
            ["6a5d0c0c4fb9a3c5e1d60011"] = 15,   // K9 training treats
            ["6a5d0c0c4fb9a3c5e1d60012"] = 100,  // K9 premium dog food
            ["635a758bfefc88a93f021b8a"] = 35,   // Salty Dog beef sausage
            ["57347da92459774491567cf5"] = 35,   // Can of beef stew (Large)
            ["57347d7224597744596b4e72"] = 30,   // Can of beef stew (Small)
            ["65815f0e647e3d7246384e14"] = 25,   // Pack of Tarker dried meat
        };

        // Plain water only. Values are thirst points for a full item.
        public static readonly Dictionary<string, float> Water = new Dictionary<string, float>
        {
            ["5448fee04bdc2dbc018b4567"] = 60,   // Bottle of water (0.6L)
            ["5c0fa877d174af02a012e1cf"] = 50,   // Aquamari water bottle with filter
            ["60098b1705871270cd5352a1"] = 40,   // Emergency Water Ration
            ["5d1b33a686f7742523398398"] = 100,  // Canister with purified water
        };

        // What an item is worth to the dog, given how much of it is left (0-1).
        public static float Value(string template, bool food, float remaining)
        {
            var table = food ? Food : Water;
            return table.TryGetValue(template, out float full) ? full * Clamp01(remaining) : 0;
        }

        public void Decay(float hours, float hungerPerHour, float thirstPerHour)
        {
            if (!(hours > 0)) return;
            Hunger = Clamp(Hunger - hours * Math.Max(0, hungerPerHour));
            Thirst = Clamp(Thirst - hours * Math.Max(0, thirstPerHour));
        }

        // Combat-mode speed (and bite pace) factor: 1 when fed and watered, down to 0.75 when either runs out.
        public float Fitness => 0.75f + 0.25f * Clamp01(Math.Min(Hunger, Thirst) / Hungry);

        // Eating or drinking from a dish takes what fits and leaves the rest in the dish.
        public void EatFromDish() { float take = Math.Min(FoodDish, 100 - Hunger); Hunger += take; FoodDish -= take; }
        public void DrinkFromDish() { float take = Math.Min(WaterDish, 100 - Thirst); Thirst += take; WaterDish -= take; }

        // Rejects anything that isn't a sane number; missing or broken values fall back to a fed dog with empty dishes.
        public DogNeeds Sanitized()
        {
            var copy = new DogNeeds { Hunger = Hunger, Thirst = Thirst, Health = Health, FoodDish = FoodDish, WaterDish = WaterDish, HomeSet = HomeSet, HomeX = HomeX, HomeY = HomeY, HomeZ = HomeZ, HomeYaw = HomeYaw, HomeByHand = HomeByHand, VestId = VestId };
            copy.Sanitize();
            return copy;
        }

        public void Sanitize()
        {
            Hunger = Sane(Hunger, 100); Thirst = Sane(Thirst, 100); Health = Sane(Health, 100); FoodDish = Sane(FoodDish, 0); WaterDish = Sane(WaterDish, 0);
            // A kennel position must be a real place; anything odd just means "not placed yet".
            if (!(Place(HomeX) && Place(HomeY) && Place(HomeZ) && !float.IsNaN(HomeYaw) && !float.IsInfinity(HomeYaw))) HomeSet = false;
            if (!HomeSet) { HomeX = HomeY = HomeZ = HomeYaw = 0; HomeByHand = false; }
            else HomeYaw = ((HomeYaw % 360) + 360) % 360;
            if (!ValidVest(VestId)) VestId = "";
        }

        public string ToJson()
            => string.Format(CultureInfo.InvariantCulture,
                "{{\"hunger\":{0:0.##},\"thirst\":{1:0.##},\"health\":{10:0.##},\"foodDish\":{2:0.##},\"waterDish\":{3:0.##},\"homeSet\":{4},\"homeX\":{5:0.###},\"homeY\":{6:0.###},\"homeZ\":{7:0.###},\"homeYaw\":{8:0.#},\"homeByHand\":{11},\"vestId\":\"{9}\"}}",
                Hunger, Thirst, FoodDish, WaterDish, HomeSet ? "true" : "false", HomeX, HomeY, HomeZ, HomeYaw, ValidVest(VestId) ? VestId : "", Health, HomeByHand ? "true" : "false");

        // Small, strict reader for the four numbers above; anything unexpected is ignored and then sanitised.
        public static DogNeeds FromJson(string json)
        {
            var needs = new DogNeeds { Hunger = float.NaN, Thirst = float.NaN, Health = float.NaN, FoodDish = float.NaN, WaterDish = float.NaN };
            if (string.IsNullOrEmpty(json)) return needs.Sanitized();
            needs.Hunger = Read(json, "hunger"); needs.Thirst = Read(json, "thirst"); needs.Health = Read(json, "health");
            needs.FoodDish = Read(json, "foodDish"); needs.WaterDish = Read(json, "waterDish");
            needs.HomeSet = json.Replace(" ", "").Contains("\"homeSet\":true");
            needs.HomeByHand = json.Replace(" ", "").Contains("\"homeByHand\":true");
            needs.VestId = ReadText(json, "vestId");
            needs.HomeX = Read(json, "homeX"); needs.HomeY = Read(json, "homeY"); needs.HomeZ = Read(json, "homeZ"); needs.HomeYaw = Read(json, "homeYaw");
            return needs.Sanitized();
        }

        private static float Read(string json, string key)
        {
            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return float.NaN;
            at = json.IndexOf(':', at);
            if (at < 0) return float.NaN;
            int end = at + 1;
            while (end < json.Length && "0123456789.-+eE ".IndexOf(json[end]) >= 0) end++;
            return float.TryParse(json.Substring(at + 1, end - at - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
        }

        public static bool ValidVest(string v)
        {
            if (v == null) return false;
            if (v.Length == 0 || v == "none") return true;
            if (v.Length != 24) return false;
            foreach (char c in v) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static string ReadText(string json, string key)
        {
            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return "";
            int open = json.IndexOf('"', json.IndexOf(':', at) + 1);
            int close = open < 0 ? -1 : json.IndexOf('"', open + 1);
            return open < 0 || close < 0 ? "" : json.Substring(open + 1, close - open - 1);
        }

        private static bool Place(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 20000;
        private static float Sane(float v, float fallback) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : Clamp(v);
        private static float Clamp(float v) => Math.Max(0, Math.Min(100, v));
        private static float Clamp01(float v) => Math.Max(0, Math.Min(1, v));
    }
}
