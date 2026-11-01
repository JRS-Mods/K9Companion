using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Finds the dog's food and water in places it is allowed to come from, and uses it up with the same discard
    /// transaction the game uses (local in a raid, sent to the server in the hideout). Nothing is ever created.
    /// </summary>
    internal static class DogPantry
    {
        // The dog's own jacket first, then your gear, then (at home) your stash.
        internal static IEnumerable<Item> Sources(Player owner, bool hideout, bool bagOnly)
        {
            Inventory inventory = owner?.Profile?.Inventory;
            if (inventory == null) yield break;
            CompoundItem bag = hideout ? DogGear.Worn(owner.Profile) : DogBag.FindInRaid(owner);
            if (bag != null) foreach (Item item in bag.GetAllItems()) yield return item;
            if (bagOnly) yield break;
            if (inventory.Equipment != null) foreach (Item item in inventory.Equipment.GetAllItems()) yield return item;
            if (hideout && inventory.Stash != null) foreach (Item item in inventory.Stash.GetAllItems()) yield return item;
        }

        internal static float ValueOf(Item item, bool food)
        {
            FoodDrinkComponent portion = item.GetItemComponent<FoodDrinkComponent>();
            float left = portion != null && portion.MaxResource > 1 ? portion.RelativeValue : 1;
            return DogNeeds.Value(item.TemplateId.ToString(), food, left);
        }

        // The item that covers 'wanted' with the least waste; failing that, the biggest one there is.
        internal static Item Best(Player owner, bool hideout, bool food, float wanted, bool bagOnly = false)
        {
            Item best = null; float bestValue = 0; bool bestCovers = false;
            foreach (Item item in Sources(owner, hideout, bagOnly))
            {
                float value = ValueOf(item, food);
                if (value <= 0) continue;
                bool covers = value >= wanted;
                bool better = best == null || (covers && (!bestCovers || value < bestValue)) || (!covers && !bestCovers && value > bestValue);
                if (better) { best = item; bestValue = value; bestCovers = covers; }
            }
            return best;
        }

        // True while the item is somewhere you can hand it over from: your gear or the dog's jacket in a raid, or at home
        // also your stash.
        internal static bool Carried(Player owner, Item item)
        {
            Inventory inventory = owner?.Profile?.Inventory;
            ItemAddress at = item?.CurrentAddress;
            if (inventory == null || at == null) return false;
            // (Not Item.GetRootItem: that stops at the first grid, so for anything in a rig or a backpack it's the item itself.)
            if (at.IsChildOf(inventory.Equipment) || at.IsChildOf(inventory.QuestRaidItems)) return true;
            return owner is HideoutPlayer && (at.IsChildOf(inventory.Stash) || at.IsChildOf(inventory.QuestStashItems));
        }

        // Uses up what the dog needs ('wanted' points). From a multi-portion item in a raid only that share is taken, as
        // when you eat or drink part of it yourself (the game saves the remainder at extraction); a single portion, or the
        // last of one, goes with the game's own discard transaction. done(points given), 0 = nothing used.
        internal static void Consume(Player owner, Item item, bool food, float wanted, Action<float> done)
        {
            float value = ValueOf(item, food);
            FoodDrinkComponent portion = item.GetItemComponent<FoodDrinkComponent>();
            if (!(owner is HideoutPlayer) && portion != null && portion.MaxResource > 1 && portion.HpPercent > 0 && value > wanted + .5f && wanted > 0)
            {
                float perUnit = value / portion.HpPercent;
                float use = Mathf.Ceil(wanted / perUnit);
                if (portion.HpPercent - use >= .5f)
                {
                    portion.HpPercent -= use;
                    item.RaiseRefreshEvent(false, false);
                    done(use * perUnit);
                    return;
                }
            }
            Consume(owner, item, ok => done(ok ? value : 0));
        }

        internal static void Consume(Player owner, Item item, Action<bool> done) => Use(owner?.InventoryController, item, done);

        /// <summary>Uses an item up with the game's own discard transaction, through whichever inventory controller is at hand.</summary>
        internal static void Use(InventoryController controller, Item item, Action<bool> done)
        {
            if (controller == null || item == null) { done(false); return; }
            var removal = ItemManipulator.Remove(item, controller, simulate: true);
            if (removal.Failed) { done(false); return; }
            controller.TryRunNetworkTransaction(removal, result => done(result.Succeed));
        }

        // ---- at home (the DOG tab) ------------------------------------------------------------------------------

        // What the dog's care can come from out of a raid: your stash, the kennel's lockers, the dog's jacket and your gear.
        // The K9 veterinary kit in a raid (on you or in the dog's jacket) that covers 'wanted' percent with the least waste,
        // else the fullest.
        internal static Item BestKit(Player owner, float wanted)
        {
            Item best = null; float bestHolds = 0; bool bestCovers = false;
            foreach (Item item in Sources(owner, false, false))
            {
                if (!DogVet.IsKit(item)) continue;
                float holds = DogVet.Holds(item, 1);
                if (holds <= 0) continue;
                bool covers = holds >= wanted;
                if (best == null || (covers && (!bestCovers || holds < bestHolds)) || (!covers && !bestCovers && holds > bestHolds)) { best = item; bestHolds = holds; bestCovers = covers; }
            }
            return best;
        }

        internal static IEnumerable<Item> AtHome(Profile profile)
        {
            Inventory inventory = profile?.Inventory;
            if (inventory == null) yield break;
            var sources = new List<Item> { inventory.Stash, DogGear.Locker(profile, DogGear.ArmourLocker), DogGear.Locker(profile, DogGear.PremiumLocker),
                                           DogGear.Worn(profile), inventory.Equipment };
            foreach (Item root in sources)
                if (root != null) foreach (Item item in root.GetAllItems()) if (item != root) yield return item;
        }

        /// <summary>The item worth closest to 'wanted' (least waste), else the biggest; value 0 means "not this".</summary>
        internal static Item BestAtHome(Profile profile, Func<Item, float> value, float wanted)
        {
            Item best = null; float bestValue = 0; bool bestCovers = false;
            foreach (Item item in AtHome(profile))
            {
                float v = value(item);
                if (v <= 0) continue;
                bool covers = v >= wanted;
                if (best == null || (covers && (!bestCovers || v < bestValue)) || (!covers && !bestCovers && v > bestValue)) { best = item; bestValue = v; bestCovers = covers; }
            }
            return best;
        }
    }
}
