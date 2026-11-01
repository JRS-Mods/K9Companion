using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace K9Companion.Server;

/// <summary>
/// The item bookkeeping for the dog's jacket, kept free of server services so it can be tested on its own.
/// It only ever removes items or re-parents them; the only item it creates is the hidden holder and, once per profile,
/// the dog's first (empty) jacket.
/// </summary>
public static class BagLedger
{
    public enum Outcome { Untouched, Kept, KeptButNoStashSpace, LostWithPlayer, LostWithDog, Lost, Transit }

    /// <summary>
    /// Settles the jacket after the game has saved the raid inventory (and, on death, wiped it).
    /// serverItems: the server profile's items. postItems/postRaidContainerId: what the client sent back.
    /// putBack: puts the jacket (with children) back on the dog, or in the stash; true on success.
    /// </summary>
    public static Outcome RaidEnd(List<Item> serverItems, BagRecord record, List<Item>? postItems, string? postRaidContainerId,
                                  bool isDead, bool isTransfer, Func<MongoId, bool> putBack)
    {
        if (!record.InRaid || record.BagId == null) return Outcome.Untouched;
        if (isTransfer) return Outcome.Transit;
        var bagId = new MongoId(record.BagId);
        Item? bag = serverItems.FirstOrDefault(i => i.Id == bagId);
        List<Item> postBag = postItems?.GetItemWithChildren(bagId) ?? new List<Item>();
        bool cameBack = postBag.Count > 0 && postBag[0].ParentId == postRaidContainerId;
        bool keep = !isDead && !record.DogDied && bag != null && cameBack;
        if (bag == null) return isDead ? Outcome.LostWithPlayer : record.DogDied ? Outcome.LostWithDog : Outcome.Lost;
        if (keep)
        {
            // The game has already copied the raid contents in; drop leftovers of the pre-raid copy so they can't come back.
            var current = new HashSet<MongoId>(postBag.Select(i => i.Id));
            foreach (Item stale in serverItems.GetItemWithChildren(bagId).Where(i => !current.Contains(i.Id)).ToList())
                serverItems.Remove(stale);
            return putBack(bagId) ? Outcome.Kept : Outcome.KeptButNoStashSpace;
        }
        RemoveWithChildren(serverItems, bagId);
        return isDead ? Outcome.LostWithPlayer : record.DogDied ? Outcome.LostWithDog : Outcome.Lost;
    }

    /// <summary>Where the profile's dog gear lives and how to move it; filled in by BagKeeper (or a test).</summary>
    public sealed class Kit
    {
        public MongoId HolderTemplate;
        public HashSet<MongoId> Jackets = new();
        public MongoId? FirstJacket;                             // the free one (the lightest); any jacket if unset
        public string Slot = DogGear.Slot;
        public string? StashId, QuestStashId, RaidContainerId;
        public Func<MongoId, bool> PlaceHolder = _ => false;     // puts the holder in the quest stash
        public Func<MongoId, bool> PutInStash = _ => false;
        public string? Preferred;                                // the jacket chosen before holders existed ("none" = none)
        public bool FirstJacketGiven;
    }

    public sealed class KitReport { public bool HolderCreated, Migrated, FirstJacketGiven, NoRoom; public int Returned; }

    /// <summary>
    /// Out of raid: the holder exists in the quest stash; a jacket stranded in the raid container goes back on the dog
    /// (or into the stash); when the holder is new, the jacket the dog wore before moves onto it; and a profile that has
    /// never had a jacket gets one, on the dog.
    /// </summary>
    public static KitReport EnsureKit(List<Item> items, Kit kit)
    {
        var report = new KitReport();
        Item? holder = items.FirstOrDefault(i => i.Template == kit.HolderTemplate);
        if (holder == null)
        {
            holder = new Item { Id = new MongoId(), Template = kit.HolderTemplate };
            items.Add(holder);
            if (!kit.PlaceHolder(holder.Id)) { items.Remove(holder); report.NoRoom = true; return report; }
            report.HolderCreated = true;
            // Before holders the dog "wore" a bag from the stash: that one goes on the dog now.
            if (kit.Preferred != "none")
            {
                var inStash = items.Where(i => kit.Jackets.Contains(i.Template) && i.ParentId == kit.StashId).ToList();
                Item? worn = inStash.FirstOrDefault(i => i.Id.ToString() == kit.Preferred) ?? inStash.FirstOrDefault();
                if (worn != null) { PutOn(items, kit, worn); report.Migrated = true; }
            }
        }
        foreach (Item stranded in items.Where(i => kit.Jackets.Contains(i.Template) && kit.RaidContainerId != null && i.ParentId == kit.RaidContainerId).ToList())
        {
            if (PutBack(items, kit, stranded.Id)) report.Returned++;
            else report.NoRoom = true;
        }
        if (!kit.FirstJacketGiven && !items.Any(i => kit.Jackets.Contains(i.Template)))
        {
            var jacket = new Item { Id = new MongoId(), Template = kit.FirstJacket ?? kit.Jackets.First() };
            items.Add(jacket);
            PutOn(items, kit, jacket);
            report.FirstJacketGiven = true;
        }
        return report;
    }

    /// <summary>On the dog if its slot is free, otherwise in the stash.</summary>
    public static bool PutBack(List<Item> items, Kit kit, MongoId jacketId)
    {
        Item? jacket = items.FirstOrDefault(i => i.Id == jacketId);
        if (jacket == null) return false;
        Item? holder = items.FirstOrDefault(i => i.Template == kit.HolderTemplate);
        bool slotFree = holder != null && !items.Any(i => i.ParentId == holder.Id.ToString() && i.SlotId == kit.Slot && i.Id != jacketId);
        if (slotFree) { PutOn(items, kit, jacket); return true; }
        return kit.PutInStash(jacketId);
    }

    private static void PutOn(List<Item> items, Kit kit, Item jacket)
    {
        Item holder = items.First(i => i.Template == kit.HolderTemplate);
        jacket.ParentId = holder.Id.ToString(); jacket.SlotId = kit.Slot; jacket.Location = null;
    }

    public static void RemoveWithChildren(List<Item> items, MongoId rootId)
    {
        foreach (Item item in items.GetItemWithChildren(rootId).ToList()) items.Remove(item);
    }
}
