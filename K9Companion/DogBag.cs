using System;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Common.Http;

namespace K9Companion
{
    /// <summary>
    /// The dog's jacket on raid day. At home the dog wears it in the DOG tab (see DogGear); when a PMC raid starts it
    /// goes with the dog: the client tells the server first, then moves the jacket into the profile's raid quest-items
    /// container, which the game already saves when you extract and wipes when you die. After the raid the server puts it
    /// back on the dog. If the dog dies, whatever is still in its jacket at the end of the raid is lost.
    /// </summary>
    internal static class DogBag
    {
        private const string StartRoute = "/k9companion/bag/raid-start", DogDiedRoute = "/k9companion/bag/dog-died", DogRevivedRoute = "/k9companion/bag/dog-revived";

        internal static void Patch(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate))
                         ?? throw new MissingMethodException(nameof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate));
            // First: Fika replaces raid creation with its own (its prefix skips the original, and any prefix after it).
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DogBag), nameof(BeforeRaid)) { priority = Priority.First });
        }

        // Runs just before the game drops the stash from the profile for the raid. Nothing moves unless the server
        // has recorded that the bag is going into this raid, so the two sides can't disagree.
        private static void BeforeRaid(TarkovApplication __instance)
        {
            try
            {
                Profile profile = __instance.Session?.Profile;
                RaidSettings raid = __instance.CurrentRaidSettings;
                if (profile?.Inventory == null || raid == null) return;
                bool pmcRaid = raid.Side == ESideType.Pmc;
                // Whether the dog comes along at all: you need a dog, and it must be fit enough (K9.NotDeployable).
                K9.Refresh();
                string stays = K9.NotDeployable();
                bool deployed = Plugin.Active.Value && stays == null;
                Deployed = deployed;
                if (Plugin.Active.Value && stays != null && K9.Owned) Plugin.Log.LogInfo("The dog stays home this raid: " + stays + ".");
                K9.RaidStart(deployed);
                // Head gear and the camera go with the dog into PMC raids (lost if it or you die). They stay in its slots
                // at home; the dog wears what's there now.
                bool kitsGo = pmcRaid && deployed;
                DogGear.RaidHead = kitsGo ? DogGear.HeadLook(profile) : null;
                DogGear.RaidCamera = kitsGo ? DogGear.CameraLook(profile) : null;
                // The collar goes into any raid with the dog and is never lost there (it stays in its slot at home).
                DogGear.RaidCollar = deployed ? DogGear.CollarStyle(profile) : null;
                DogGear.RaidJacket = pmcRaid && deployed ? DogGear.Worn(profile) : null;
                DogGear.RaidHeadItem = kitsGo ? DogGear.SlotOf(profile, DogGear.HeadSlot)?.ContainedItem : null;
                DogGear.RaidCameraItem = kitsGo ? DogGear.CameraWorn(profile) : null;
                DogGear.RaidCollarItem = deployed ? DogGear.SlotOf(profile, DogGear.CollarSlot)?.ContainedItem : null;
                DogGear.RaidLoadoutTaken = true;
                // The dog gets ready along with your character, while the loading screen is up.
                if (deployed) DogPreload.Begin(DogGear.Look(DogGear.RaidJacket), DogGear.RaidHead, DogGear.RaidCamera != null, DogGear.RaidCollar);
                else DogPreload.Release();
                string kits = kitsGo ? string.Join(",", DogGear.KitsWorn(profile).Select(i => "\"" + i.Id + "\"")) : "";
                // The jacket the dog wears (its slot in the DOG tab); none when the slot is empty.
                Item bag = pmcRaid && deployed ? DogGear.Worn(profile) : null;
                // Always report, so a record left by an earlier raid can never be applied to this one.
                string reply = RequestHandler.PostJson(StartRoute, "{\"bagId\":" + (bag != null ? "\"" + bag.Id + "\"" : "null") + ",\"kitIds\":[" + kits + "]}");
                if (bag == null) return;
                if (reply == null || !reply.Contains("\"ok\":true")) { Plugin.Log.LogWarning("Server mod didn't accept the dog's jacket; it stays home. Reply: " + reply); return; }
                if (MoveToRaidContainer(profile, bag)) Plugin.Log.LogInfo("Dog's jacket goes into the raid with " + Contents(bag) + " item(s).");
                else
                {
                    // The server expects the bag in the raid; tell it the bag stayed home.
                    RequestHandler.PostJson(StartRoute, "{\"bagId\":null,\"kitIds\":[" + kits + "]}");
                    Plugin.Log.LogWarning("No room to carry the dog's jacket into the raid; it stays home.");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Dog's jacket couldn't be prepared for the raid (it stays home): " + e); }
        }

        /// <summary>Decided just before each raid (null until then): whether the dog goes along.</summary>
        internal static bool? Deployed;

        internal static CompoundItem FindInRaid(Player player)
            => player?.Profile?.Inventory?.QuestRaidItems?.GetAllItems().FirstOrDefault(DogGear.IsJacket) as CompoundItem;

        private static int Contents(Item bag) => bag.GetAllItems().Count() - 1;

        private static bool MoveToRaidContainer(Profile profile, Item bag)
        {
            // From the dog's slot (or, for an old setup, the stash grid) into the raid container.
            Grid target = profile.Inventory.QuestRaidItems?.Grids?.FirstOrDefault();
            ItemAddress from = bag.CurrentAddress;
            if (target == null || from == null) return false;
            GridItemAddress spot = target.FindLocationForItem(bag);
            if (spot == null) return false;
            if (from.RemoveWithoutRestrictions(bag).Failed) return false;
            if (target.Add(bag, spot.LocationInGrid, false).Succeeded) return true;
            from.AddWithoutRestrictions(bag);   // put it back exactly where it was
            return false;
        }

        // Opens the jacket the way the game opens a backpack on the ground: your inventory with its grid beside it.
        internal static string Open(Player player)
        {
            CompoundItem bag = FindInRaid(player);
            if (bag == null) return "The dog isn't wearing a jacket this raid.";
            GamePlayerOwner owner = player.GetComponent<GamePlayerOwner>();
            if (owner == null) return "Can't open the jacket right now.";
            // As a plain container (like a backpack on the ground): its grid shows beside your gear and items move
            // with your own inventory controller. The "fake container" view would re-home the jacket into a throwaway
            // stash owned by another controller, which broke transfers and left the player "busy".
            owner.ShowInventoryScreenLoot(bag, () => { }, false);
            return null;
        }

        internal static void DogDied()
        {
            try { RequestHandler.PostJson(DogDiedRoute, "{}"); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server the dog died: " + e.Message); }
        }

        // A downed dog was reported as dead (so a raid ending, or a crash, counts as just that), and has been brought back.
        internal static void DogRevived()
        {
            try { RequestHandler.PostJson(DogRevivedRoute, "{}"); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server the dog was revived: " + e.Message); }
        }
    }
}
