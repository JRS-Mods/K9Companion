using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The dog's gear, registered by the server mod: a jacket (a container: a chest rig or one of four vests), head gear
    /// (goggles) and a clip-on action camera, worn in the slots of a hidden "dog" item that lives in
    /// the profile's quest stash. The DOG tab shows those slots like the gear screen shows yours. All of it goes into PMC
    /// raids with the dog: the jacket rides in the raid container (DogBag); the head gear and camera stay in their slots
    /// (the dog wears their looks, taken just before the raid) and the server removes them if the dog or you die.
    /// </summary>
    internal static class DogGear
    {
        // Each jacket's look (its gear_<look> model on the dog and icon_<look> icon), shared with the server mod (DogGear.cs
        // there). The chest rig keeps the old saddlebag's id; the two jackets taken out in 0.20 look like what replaced them.
        internal static readonly Dictionary<string, string> Jackets = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d0d061"] = "harness_chestrig",
            ["6a5d0c0c4fb9a3c5e1d0d073"] = "harness_pocket",
            ["6a5d0c0c4fb9a3c5e1d0d071"] = "harness_tactical",
            ["6a5d0c0c4fb9a3c5e1d60020"] = "harness_assault",   // The Last Handler's reward
            ["6a5d0c0c4fb9a3c5e1d60021"] = "harness_camera",   // camera vest (has its own camera)
            ["6a5d0c0c4fb9a3c5e1d0d074"] = "harness_pocket",  // old basic jacket (becomes the pocket vest at game start)
            ["6a5d0c0c4fb9a3c5e1d0d072"] = "harness_assault",   // old heavy armour (becomes the assault vest)
        };
        // Head gear and the clip-on camera, by look (gear_<look> model, icon_<look> icon), as the server mod's DogGear.Kits.
        internal static readonly Dictionary<string, string> Kits = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d60022"] = "head_goggles_tan",
            ["6a5d0c0c4fb9a3c5e1d60023"] = "head_goggles_black",
            ["6a5d0c0c4fb9a3c5e1d60024"] = "head_headset",   // taken out in 0.29.1 (the server makes any into action cameras)
            ["6a5d0c0c4fb9a3c5e1d60025"] = "camera_action",
        };
        // The collars, by style (gear_collar_<style> on the dog, collar_<style> as an item), as the server mod's DogGear.Collars.
        internal static readonly Dictionary<string, string> Collars = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d60017"] = "training",
            ["6a5d0c0c4fb9a3c5e1d60018"] = "black",
            ["6a5d0c0c4fb9a3c5e1d60016"] = "buckle",   // K9 tactical collar
        };
        // The dog's supplies and the K9 story items made from their own models (tools/convert_dog_items.py:
        // gear_item_<key> model, icon_item_<key> icon), by the item they stand for. Shown only as items, never on the dog.
        internal static readonly Dictionary<string, string> Supplies = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d0d062"] = "item_can",      // Can of dog food
            ["6a5d0c0c4fb9a3c5e1d60012"] = "item_can",      // K9 premium dog food
            ["6a5d0c0c4fb9a3c5e1d0d064"] = "item_meat",     // Pack of dog meat
            ["6a5d0c0c4fb9a3c5e1d60015"] = "item_duffel",   // K9 training kit
            ["6a5d0c0c4fb9a3c5e1d60010"] = "item_vetkit",   // K9 veterinary kit
            ["6a5d0c0c4fb9a3c5e1d60005"] = "item_vetkit",   // Veterinary supply case (quest item)
            ["6a5d0c0c4fb9a3c5e1d60001"] = "item_tag",      // K9 identification tag (quest item)
        };
        internal const string HolderTemplate = "6a5d0c0c4fb9a3c5e1d0d080", Slot = "DogJacket", HeadSlot = "DogHead", CameraSlot = "DogCamera",
            CollarSlot = "DogCollar";
        // Jackets that come with a camera of their own: the clip-on one isn't drawn on them.
        internal static readonly HashSet<string> BuiltInCamera = new HashSet<string> { "harness_camera" };
        // The kennel's lockers (hidden containers in the quest stash, opened from the DOG tab at kennel level 2 and 3).
        internal const string ArmourLocker = "6a5d0c0c4fb9a3c5e1d60030", PremiumLocker = "6a5d0c0c4fb9a3c5e1d60031";

        internal static bool IsJacket(Item item) => item != null && Jackets.ContainsKey(item.TemplateId.ToString());
        internal static bool IsHolder(Item item) => item != null && item.TemplateId.ToString() == HolderTemplate;
        internal static bool IsLocker(Item item) => item != null && (item.TemplateId.ToString() == ArmourLocker || item.TemplateId.ToString() == PremiumLocker);

        internal static CompoundItem Locker(Profile profile, string template)
            => profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(i => i.TemplateId.ToString() == template) as CompoundItem;
        internal static string Look(Item jacket) => jacket != null && Jackets.TryGetValue(jacket.TemplateId.ToString(), out string c) ? c : null;
        // The look of any of the dog's gear (jacket, head gear, camera, collar) or supplies: its model as an item and its stash icon.
        internal static string AnyLook(Item item)
        {
            if (item == null) return null;
            string id = item.TemplateId.ToString();
            return Look(item) ?? (Kits.TryGetValue(id, out string k) ? k : Collars.TryGetValue(id, out string c) ? "collar_" + c
                : Supplies.TryGetValue(id, out string s) ? s : null);
        }
        // The collar in the dog's collar slot (null = none; an older server mod has no slot).
        internal static string CollarStyle(Profile profile)
            => SlotOf(profile, CollarSlot)?.ContainedItem is Item c && Collars.TryGetValue(c.TemplateId.ToString(), out string style) ? style : null;
        // The collar the dog wears into this raid (taken just before it, as the head gear is).
        internal static string RaidCollar;
        // What the dog takes into this raid, item by item, as it was at raid start (null = nothing in that place): the
        // deploying screen's loadout window shows these, since by then the jacket is in the raid container and the raid's
        // copy of your profile has no quest stash (so no dog holder) to look in.
        internal static Item RaidJacket, RaidHeadItem, RaidCameraItem, RaidCollarItem;
        internal static bool RaidLoadoutTaken;

        // The hidden "dog" item (out of raid only: the game drops the quest stash from the profile for a raid).
        internal static CompoundItem Holder(Profile profile)
            => profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(IsHolder) as CompoundItem;

        internal static Slot JacketSlot(Profile profile) => SlotOf(profile, Slot);
        internal static Slot SlotOf(Profile profile, string name) => Holder(profile)?.Slots?.FirstOrDefault(s => s.ID == name);

        // What the dog wears on its head and its jacket's camera mount at home (null = nothing; the server mod adds those
        // slots, so an older one has neither).
        internal static string HeadLook(Profile profile) => AnyLook(SlotOf(profile, HeadSlot)?.ContainedItem);
        // The clip-on camera rides on the jacket (since 0.25); an old one may still be in the dog's own Camera slot.
        internal static string CameraLook(Profile profile) => AnyLook(CameraOn(Worn(profile)) ?? SlotOf(profile, CameraSlot)?.ContainedItem);
        internal static Item CameraOn(Item jacket) => (jacket as CompoundItem)?.Slots?.FirstOrDefault(s => s.ID == CameraSlot)?.ContainedItem;
        internal static Item CameraWorn(Profile profile) => CameraOn(Worn(profile)) ?? SlotOf(profile, CameraSlot)?.ContainedItem;

        // The plate a jacket has where a round comes in: front or back by the round's way, else the side it came from.
        internal static Item PlateFor(Item jacket, Vector3 travel, Transform dog)
        {
            var slots = (jacket as CompoundItem)?.Slots;
            if (slots == null || dog == null) return null;
            float along = Vector3.Dot(travel.normalized, dog.forward);
            string name = along < -.5f ? "Front_plate" : along > .5f ? "Back_plate"
                : Vector3.Dot(travel, dog.right) > 0 ? "Left_side_plate" : "Right_side_plate";
            return slots.FirstOrDefault(s => string.Equals(s.ID, name, StringComparison.OrdinalIgnoreCase))?.ContainedItem;
        }

        // The head gear (and an old-style camera) the dog has on, sent to the server at raid start: they're at risk in
        // the raid. A camera on the jacket goes and comes back with the jacket.
        internal static IEnumerable<Item> KitsWorn(Profile profile)
            => new[] { SlotOf(profile, HeadSlot)?.ContainedItem, SlotOf(profile, CameraSlot)?.ContainedItem }.Where(i => i != null);

        // The same, taken just before a raid (DogBag), when the quest stash is still there to look in.
        internal static string RaidHead, RaidCamera;

        // The jacket the dog is wearing at home.
        internal static CompoundItem Worn(Profile profile) => JacketSlot(profile)?.ContainedItem as CompoundItem;

        // True for any place inside the dog's gear: the holder, a jacket, a kennel locker, or anything in them.
        internal static bool Inside(ItemAddress address)
        {
            Item at = address?.Container?.ParentItem;
            for (int depth = 0; at != null && depth < 16; depth++)
            {
                if (IsHolder(at) || IsJacket(at) || IsLocker(at)) return true;
                at = at.Parent?.Container?.ParentItem;
            }
            return false;
        }
    }
}
