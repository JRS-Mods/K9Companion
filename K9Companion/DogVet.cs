using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The K9 veterinary kit is the one thing that heals the dog, and it's never used on you: USE, HEAL ALL, its quick slot
    /// key, a double-click or dragging it onto your body all treat the dog. In a raid that's done kneeling beside the
    /// dog (Companion.Treat); at home at once, from anywhere the kit is.
    /// </summary>
    internal static class DogVet
    {
        internal const string Kit = "6a5d0c0c4fb9a3c5e1d60010";
        private const string UseRoute = "/k9companion/vet/use";
        private static readonly FieldInfo UiController = AccessTools.Field(typeof(ItemUiContext), "_inventoryController");

        internal static bool IsKit(Item item) => item != null && item.TemplateId.ToString() == Kit;

        internal static bool InRaid => Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);

        internal static float Multiplier => K9.Kennel() >= 3 ? VetMath.StationBonus : 1f;

        // How much of the dog's health the kit can still restore, in percent.
        internal static float Holds(Item kit, float multiplier)
        {
            MedKitComponent medkit = kit?.GetItemComponent<MedKitComponent>();
            return medkit == null || medkit.MaxHpResource <= 0 ? 0 : medkit.HpResource / medkit.MaxHpResource * 100 * multiplier;
        }

        // Why the kit can't be used right now; null when it can.
        internal static string Problem(Item kit)
        {
            if (InRaid)
            {
                Companion dog = Companion.Raid;
                return dog == null ? "Your dog isn't with you." : dog.TreatProblem(kit);
            }
            if (!K9.Owned) return "The K9 veterinary kit is for a dog: you have none yet.";
            DogNeeds needs = NeedsStore.Current;
            if (needs == null) return "No dog.";
            if (needs.Health >= 99.5f) return Plugin.DogName.Value + " is in good health.";
            return Holds(kit, 1) <= 0 ? "This kit is empty." : null;
        }

        // Uses the kit on the dog however it was asked for. Tells you if it can't.
        internal static void Use(Item kit)
        {
            try
            {
                string problem = Problem(kit);
                if (problem == null && InRaid) problem = Companion.Raid.Treat(kit);
                else if (problem == null) UseAtHome(kit, ControllerFor(kit), said => NotificationManager.DisplayMessageNotification(said));
                if (problem != null) NotificationManager.DisplayWarningNotification(problem);
            }
            catch (Exception e) { Plugin.Log.LogError("K9 veterinary kit: " + e); }
        }

        private static InventoryController ControllerFor(Item kit)
            => kit.Owner as InventoryController ?? UiController?.GetValue(ItemUiContext.Instance) as InventoryController;

        /// <summary>
        /// At home it's done at once. The last of a kit goes with the game's own discard; otherwise the server takes what was
        /// used off the kit, so it's still there, with less in it, next time you log in.
        /// </summary>
        internal static void UseAtHome(Item kit, InventoryController controller, Action<string> said)
        {
            DogNeeds needs = NeedsStore.Current;
            MedKitComponent medkit = kit.GetItemComponent<MedKitComponent>();
            if (needs == null || medkit == null) { said("No dog."); return; }
            VetMath.Portion(medkit.HpResource, medkit.MaxHpResource, 100 - needs.Health, Multiplier, out float gives, out float use, out bool lastOfIt);
            if (gives <= 0) { said(Plugin.DogName.Value + " is in good health."); return; }
            void Healed()
            {
                needs.Health = Mathf.Min(100, needs.Health + gives);
                NeedsStore.Save();
                Companion.Current?.TreatedAtHome();
                said($"{Plugin.DogName.Value} was treated (+{Mathf.RoundToInt(gives)}%): now at {Mathf.RoundToInt(needs.Health)}%.");
            }
            if (lastOfIt)
            {
                DogPantry.Use(controller, kit, ok => { if (ok) Healed(); else said("Couldn't use the K9 veterinary kit."); });
                return;
            }
            string reply = null;
            try { reply = RequestHandler.PostJson(UseRoute, new JObject { ["id"] = kit.Id.ToString(), ["amount"] = use }.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception e) { Plugin.Log.LogWarning("K9 veterinary kit: " + e.Message); }
            JObject answer = null;
            try { if (!string.IsNullOrEmpty(reply)) answer = JObject.Parse(reply); }
            catch (Exception) { }
            if (answer == null || answer["ok"]?.Value<bool>() != true)
            {
                Plugin.Log.LogWarning("The server wouldn't use the K9 veterinary kit. Reply: " + reply);
                said("Couldn't use the K9 veterinary kit: restart the SPT server if you've just updated the mod.");
                return;
            }
            medkit.HpResource = answer["left"]?.Value<float>() ?? medkit.HpResource - use;
            kit.RaiseRefreshEvent(false, false);
            Healed();
        }
    }

    /// <summary>
    /// Dog settings the owner of the SPT server has fixed for everyone on it, from the server mod's config.jsonc: with Fika
    /// that's everyone in the raid, as they all use the same server. Anything left out stays each player's own (F12).
    /// </summary>
    internal static class ServerRules
    {
        private const string Route = "/k9companion/settings";
        internal static bool? CombatMode;
        internal static DogApproach? Approach;
        internal static Aggression? Aggression;
        internal static bool? SoloOnly, DownedMode;
        internal static float? EngageDistance, OrderRange, BitesToKill, BossBitesToKill, BiteCooldown, BiteStagger, DogHealth, AutoRushRange, DefendRange, BleedOutTime, CarryLimit;
        private static string applied = "{}";
        private static bool complained;

        // Asked for along with the K9 state: on the raid screens and just before a raid.
        internal static void Refresh()
        {
            try { Apply(RequestHandler.PostJson(Route, "{}")); }
            catch (Exception e)
            {
                if (!complained) Plugin.Log.LogWarning("Couldn't read the server's dog settings (restart the SPT server if you've just updated the mod): " + e.Message);
                complained = true;
            }
        }

        private static void Apply(string json)
        {
            if (string.IsNullOrEmpty(json) || json == applied) return;
            JObject s;
            try { s = JObject.Parse(json); }
            catch (Exception) { return; }   // a server too old to have them answers with something else
            applied = json;
            CombatMode = s["combatMode"]?.Value<bool?>();
            Approach = Enum.TryParse(s["approach"]?.Value<string>(), true, out DogApproach how) ? how : (DogApproach?)null;
            AutoRushRange = s["autoRushRange"]?.Value<float?>();
            DefendRange = s["defendRange"]?.Value<float?>();
            string aggression = s["aggression"]?.Value<string>();
            Aggression = Enum.TryParse(aggression, true, out Aggression level) ? level : (Aggression?)null;
            EngageDistance = s["engageDistance"]?.Value<float?>();
            OrderRange = s["orderRange"]?.Value<float?>();
            BitesToKill = s["bitesToKill"]?.Value<float?>();
            BossBitesToKill = s["bossBitesToKill"]?.Value<float?>();
            BiteCooldown = s["biteCooldown"]?.Value<float?>();
            BiteStagger = s["biteStagger"]?.Value<float?>();
            DogHealth = s["dogHealth"]?.Value<float?>();
            SoloOnly = s["soloOnly"]?.Value<bool?>();
            DownedMode = s["downedMode"]?.Value<bool?>();
            BleedOutTime = s["bleedOutTime"]?.Value<float?>();
            CarryLimit = s["carryLimit"]?.Value<float?>();
            Plugin.Log.LogInfo(s.Count == 0 ? "Server's dog settings: none, every player uses their own." : "Server's dog settings (for everyone): " + json);
        }
    }
}
