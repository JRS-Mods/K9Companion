using System;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.HealthSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.Utilities;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>What the game's own interaction raycast finds when you look at the dog in a raid.</summary>
    internal sealed class DogInteractive : InteractableObject
    {
        internal Companion Dog;
    }

    /// <summary>What it finds when you look at the kennel's bowls and dog house in the hideout (first person).</summary>
    internal sealed class KennelInteractive : InteractableObject { }

    /// <summary>
    /// The dog in the game's own interaction systems:
    ///  - Look at it and press your interact key (E): open its jacket, feed it, give it water or pet it, in the same prompt
    ///    list as containers and doors (scroll to pick, as there).
    ///  - Right-click food or a drink in your inventory: "Feed dog" / "Give water to dog", in the item's own menu.
    ///  - The K9 veterinary kit is for the dog only: however you use it (USE, HEAL ALL, a double-click, its quick slot key,
    ///    dragging it onto your body), you treat the dog instead (DogVet).
    ///  - In the hideout, look at the kennel (its bowls and dog house): fill the food or water bowl from your stash, or
    ///    open the kennel's upgrades.
    /// </summary>
    internal static class DogInteraction
    {
        internal const string FeedKey = "Feed dog", WaterKey = "Give water to dog";

        internal static void Patch(Harmony harmony)
        {
            var actions = AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions),
                new[] { typeof(GamePlayerOwner), typeof(IInteractive) }) ?? throw new MissingMethodException(nameof(InteractionContextHelper), "GetAvailableActions(owner, interactive)");
            harmony.Patch(actions, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(Actions)));
            var menu = AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions))
                       ?? throw new MissingMethodException(nameof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions));
            harmony.Patch(menu, postfix: new HarmonyMethod(typeof(DogInteraction), nameof(Menu)));
            // Every way of using a med ends in Player.Proceed(Meds, body parts, ...); a quick slot key goes through
            // TryProceed first (which would put your weapon away), so both are caught.
            var proceed = AccessTools.GetDeclaredMethods(typeof(Player)).FirstOrDefault(m => m.Name == nameof(Player.Proceed)
                              && m.GetParameters().Length > 1 && m.GetParameters()[0].ParameterType == typeof(Meds))
                          ?? throw new MissingMethodException(nameof(Player), "Proceed(Meds, ...)");
            harmony.Patch(proceed, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(UseMeds)));
            var quick = AccessTools.Method(typeof(Player), nameof(Player.TryProceed)) ?? throw new MissingMethodException(nameof(Player), nameof(Player.TryProceed));
            harmony.Patch(quick, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(QuickUse)));
            // The item menu: USE and HEAL ALL go by whether YOU are hurt; for the kit, by whether the dog is. Out of a raid,
            // dragging it onto your body goes through the menu's health controller instead of Player.Proceed.
            harmony.Patch(AccessTools.Method(typeof(ItemContextInteractionsSwitcher), nameof(ItemContextInteractionsSwitcher.IsInteractive)),
                postfix: new HarmonyMethod(typeof(DogInteraction), nameof(KitButtons)));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.UseItem)), prefix: new HarmonyMethod(typeof(DogInteraction), nameof(QuickUse)));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.HealAll)), prefix: new HarmonyMethod(typeof(DogInteraction), nameof(UseMeds)));
            foreach (var apply in AccessTools.GetDeclaredMethods(typeof(OfflineHealthController)).Where(m => m.Name == nameof(OfflineHealthController.ApplyItem)))
                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(AppliedAtHome)));
        }

        private static bool UseMeds(Meds meds) => !TreatInstead(meds);
        private static bool QuickUse(Item item) => !TreatInstead(item);
        private static bool AppliedAtHome(Item item, ref bool __result)
        {
            if (!TreatInstead(item)) return true;
            __result = false;
            return false;
        }

        // The K9 veterinary kit is never used on a person: the dog gets it, if it can.
        private static bool TreatInstead(Item item)
        {
            if (!DogVet.IsKit(item)) return false;
            DogVet.Use(item);
            return true;
        }

        private static void KitButtons(ItemContextInteractionsSwitcher __instance, EItemInfoButton button, ref IResult __result)
        {
            if ((button != EItemInfoButton.Use && button != EItemInfoButton.HealAll && button != EItemInfoButton.UseAll) || !DogVet.IsKit(__instance._item)) return;
            try
            {
                string why = DogVet.Problem(__instance._item);
                __result = why == null ? (IResult)SuccessfulResult.New : new FailedResult(why);
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 veterinary kit's menu: " + e.Message); }
        }

        // The game has no interactions for an object type it doesn't know (it throws), so the dog's are answered first.
        private static bool Actions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (interactive is KennelInteractive) { __result = Kennel(); return false; }
            if (!(interactive is DogInteractive target)) return true;
            Player player = owner?.Player;
            Companion dog = target.Dog;
            CompoundItem jacket = DogBag.FindInRaid(player);
            var state = new AvailableInteractionState();
            if (dog != null && dog.Downed)
            {
                Item kit = dog.ReviveKit(player);
                string why = kit == null ? "no K9 veterinary kit" : dog.TreatProblem(kit);
                string name = "Revive " + Plugin.DogName.Value;
                state.Actions.Add(new InteractionAction { Name = why == null ? name : name + ": " + why, Disabled = why != null, Action = () => DogVet.Use(kit) });
            }
            state.Actions.Add(new InteractionAction
            {
                // Named for what it is: "Open chest rig", "Open camera vest".
                Name = jacket != null ? "Open " + JacketWord(jacket) : "No jacket this raid",
                Disabled = jacket == null,
                Action = () =>
                {
                    string problem = DogBag.Open(player);
                    if (problem != null) NotificationManager.DisplayWarningNotification(problem);
                }
            });
            if (dog != null && !dog.IsDead)
            {
                foreach (DogOrder order in new[] { DogOrder.Feed, DogOrder.Water })
                {
                    bool can = dog.Available(order, out string why);
                    state.Actions.Add(new InteractionAction
                    {
                        Name = can ? (dog.NeedsLabelFor(order) ?? (order == DogOrder.Feed ? "Feed" : "Give water")) : (order == DogOrder.Feed ? "Feed" : "Give water") + ": " + why,
                        Disabled = !can,
                        Action = () => dog.Order(order)
                    });
                }
                state.Actions.Add(new InteractionAction { Name = "Pet " + Plugin.DogName.Value, Action = () => dog.PetDog(player) });
            }
            state.InitSelected();
            __result = state;
            return false;
        }

        private static AvailableInteractionState Kennel()
        {
            var state = new AvailableInteractionState();
            Companion dog = Companion.Current;
            if (dog != null && Plugin.SurvivalNeeds.Value)
                foreach (bool food in new[] { true, false })
                {
                    string what = food ? "food" : "water";
                    float level = dog.DishLevel(food);
                    state.Actions.Add(new InteractionAction
                    {
                        Name = level > 90 ? $"The {what} bowl is full" : $"Fill the {what} bowl ({Mathf.RoundToInt(level)}%)",
                        Disabled = level > 90,
                        Action = () => dog.FillDish(food)
                    });
                }
            state.Actions.Add(new InteractionAction
            {
                Name = "K9 Kennel upgrades",
                Action = () => { if (!KennelIcon.OpenScreen()) NotificationManager.DisplayWarningNotification("Open the kennel from its paw icon on the hideout overview."); }
            });
            state.InitSelected();
            return state;
        }

        // "chest rig", "pocket vest", "camera vest": the jacket's name ("K9 Chest Rig (Black)"), lower case, without the
        // "K9" and the colour.
        private static string JacketWord(Item jacket)
        {
            string name = jacket.LocalizedName() ?? "";
            if (name.StartsWith("K9 ")) name = name.Substring(3);
            int colour = name.IndexOf(" (", StringComparison.Ordinal);
            if (colour > 0) name = name.Substring(0, colour);
            return string.IsNullOrEmpty(name) ? "jacket" : name.ToLowerInvariant();
        }

        // Adds the feeding entries to food and drinks you carry, while the dog is alive.
        private static void Menu(ItemContext itemContext, ContextInteractions<EItemInfoButton> __result)
        {
            try
            {
                Item item = itemContext?.Item;
                Companion dog = Companion.Raid ?? Companion.Current;
                if (__result == null || item == null || dog == null || dog.IsDead) return;
                Player owner = dog.Owner;
                if (owner == null || !DogPantry.Carried(owner, item)) return;
                Sprite icon = ResourcesCache.Pop<Sprite>("Characteristics/Icons/" + EItemInfoButton.Use);
                if (Plugin.SurvivalNeeds.Value)
                {
                    Add(__result, item, true, FeedKey, icon);
                    Add(__result, item, false, WaterKey, icon);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dog feeding menu: " + e.Message); }
        }

        private static void Add(ContextInteractions<EItemInfoButton> menu, Item item, bool food, string key, Sprite icon)
        {
            if (DogPantry.ValueOf(item, food) <= 0) return;
            foreach (DynamicContextInteraction existing in menu.DynamicInteractions) if (existing.Key == key) return;
            menu.method_2("k9companion_" + (food ? "feed" : "water"), key, () =>
            {
                // In a raid it's handed over (the dog comes and eats from your hand); at home it has it at once.
                Companion dog = Companion.Raid ?? Companion.Current;
                string result = dog == null ? "No dog." : dog.Owner is HideoutPlayer ? dog.FeedAtHome(item, food) : dog.FeedFromInventory(item, food);
                if (result != null) NotificationManager.DisplayWarningNotification(result);
            }, icon);
        }
    }
}
