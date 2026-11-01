using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Services.Commerce;

namespace K9Companion.Server;

/// <summary>What the server knows about one profile's saddlebag between raid start and raid end.</summary>
public sealed class BagRecord
{
    [JsonPropertyName("bagId")] public string? BagId { get; set; }
    [JsonPropertyName("inRaid")] public bool InRaid { get; set; }
    [JsonPropertyName("dogDied")] public bool DogDied { get; set; }
    [JsonPropertyName("recordedAt")] public long RecordedAt { get; set; }
    // Once a profile has had a jacket the dog never gets another free one; lost jackets are bought from Dr. Yang.
    [JsonPropertyName("firstJacketGiven")] public bool FirstJacketGiven { get; set; }
    // The head gear and camera the dog wore into the raid. They stay in its slots, but are gone if it or you die.
    [JsonPropertyName("kitIds")] public List<string>? KitIds { get; set; }
    // Since 0.31 the collars are items: the dog has been handed its own (EnsureCollars).
    [JsonPropertyName("collarsGiven")] public bool CollarsGiven { get; set; }
}

/// <summary>
/// Keeps every PMC's dog jacket consistent with the game's own raid saving:
///  - Out of raid the jacket the dog wears is in the slot of a hidden holder item in the quest stash (the DOG tab shows
///    that slot); spare jackets are ordinary containers in the stash. The first jacket is free, once.
///  - The client says when the jacket goes into a raid. It then rides in the profile's raid quest-items container,
///    which the game copies back at extract and wipes on death, so the contents are saved by the game itself.
///  - After the raid the jacket goes back on the dog. Leftovers from the pre-raid copy are removed so nothing is
///    duplicated. If you died or the dog died, the jacket (and whatever was still in it) is gone.
///  - Head gear and the camera never leave the dog's slots (the client draws them from what it saw at raid start);
///    the server only notes which ones went, and removes them after the raid if you died or the dog died.
///  - Anything insured among the lost gear (the jacket, its plates and camera, what's in it, the head gear) goes through
///    the game's own insurance, as your own lost gear does: the insurer finds it, or doesn't, and mails it back. The game
///    itself only looks at your own gear, so the dog's is handed to the same process here.
/// Nothing is touched unless the client recorded that the jacket went into the raid.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class BagKeeper(ISptLogger<BagKeeper> logger, ProfileHelper profiles, InventoryHelper inventory,
    TemplateTable templates, FileUtil files, JsonUtil json, K9Store k9, InsuranceService insurance, ICloner cloner)
{
    // The dog's gear as it went into the raid, per profile, taken just before the game's raid end (which wipes what you
    // lose on death): what its insurance is worked out from.
    private readonly Dictionary<MongoId, List<Item>> wentIn = new();

    public void BeforeRaidEnd(MongoId sessionId, PmcData pmc)
    {
        wentIn.Remove(sessionId);
        BagRecord record = Load(pmc);
        List<Item> items = pmc.Inventory?.Items ?? new List<Item>();
        var gear = new List<Item>();
        if (record.InRaid && record.BagId != null) gear.AddRange(items.GetItemWithChildren(new MongoId(record.BagId)));
        foreach (string id in record.KitIds ?? new List<string>()) gear.AddRange(items.GetItemWithChildren(new MongoId(id)));
        if (gear.Count > 0) wentIn[sessionId] = gear.Select(i => cloner.Clone(i)!).ToList();
        // Which of it was insured, and with whom: on your death the game's raid end drops the insurance of everything
        // gone, the dog's gear with it, before the dog's gear is settled.
        var ids = gear.Select(i => i.Id).ToHashSet();
        insuredIn[sessionId] = (pmc.InsuredItems ?? new List<InsuredItem>()).Where(x => x.ItemId is MongoId id && ids.Contains(id)).Select(x => cloner.Clone(x)!).ToList();
    }
    private readonly Dictionary<MongoId, List<InsuredItem>> insuredIn = new();

    // Lost gear (by id) that's insured: through the game's insurance, as your own is.
    private void Insure(MongoId sessionId, PmcData pmc, IEnumerable<MongoId> lost, string? location, EndLocalRaidRequestData request)
    {
        if (!wentIn.TryGetValue(sessionId, out List<Item>? gear)) return;
        pmc.InsuredItems ??= new List<InsuredItem>();
        var lostIds = lost.ToHashSet();
        // Never twice: anything the game already reported lost has gone to the insurer with your own gear.
        foreach (Item reported in request.LostInsuredItems ?? Enumerable.Empty<Item>()) lostIds.Remove(reported.Id);
        // Insured as it went in (the game may have dropped the entry since); the insurer is looked up from the entry.
        List<InsuredItem> before = insuredIn.GetValueOrDefault(sessionId) ?? new List<InsuredItem>();
        foreach (InsuredItem entry in before)
            if (entry.ItemId is MongoId lostId && lostIds.Contains(lostId) && !pmc.InsuredItems.Any(x => x.ItemId == entry.ItemId)) pmc.InsuredItems.Add(entry);
        var insured = gear.Where(i => lostIds.Contains(i.Id) && pmc.InsuredItems.Any(x => x.ItemId == i.Id)).ToList();
        if (insured.Count == 0) return;
        var packages = insurance.MapInsuredItemsToTrader(sessionId, insured, pmc);
        if (packages.Count == 0) return;
        insurance.StoreGearLostInRaidToSendLater(sessionId, packages);
        insurance.StartPostRaidInsuranceLostProcess(pmc, sessionId, location ?? "");
        // Claimed: the items are gone, so their insurance entries go too (as the game does for your own).
        var claimed = insured.Select(i => i.Id).ToHashSet();
        pmc.InsuredItems.RemoveAll(x => x.ItemId is MongoId id && claimed.Contains(id));
        logger.Info($"[K9Companion] {insured.Count} insured piece(s) of the dog's gear went to the insurer.");
    }
    public static readonly MongoId BagTemplate = DogGear.Black;
    private const string Ok = "{\"ok\":true}", Refused = "{\"ok\":false}";
    // A raid starts seconds after the client reports; an older record belongs to a raid that never finished.
    private const long StaleSeconds = 600;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- client reports -------------------------------------------------------------------------------------

    public string RaidStart(MongoId sessionId, string? bagId, List<string>? kitIds = null)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null) return Refused;
        BagRecord record = Load(pmc);
        List<string> kits = k9.Owned(pmc) ? WornKits(pmc, kitIds) : new List<string>();
        if (kits.Count > 0) logger.Info($"[K9Companion] The dog wears {kits.Count} piece(s) of head or camera gear into the raid.");
        if (string.IsNullOrEmpty(bagId) || !k9.Owned(pmc))
        {
            BagRecord none = Cleared(record);
            none.KitIds = kits; none.RecordedAt = Now;
            Save(pmc, none);
            return string.IsNullOrEmpty(bagId) ? Ok : Refused;   // no dog, no jacket on raid day
        }
        Item? bag = pmc.Inventory.Items.FirstOrDefault(i => i.Id.ToString() == bagId && DogGear.IsJacket(i.Template));
        if (bag == null || !(OnDog(pmc, bag) || InStash(pmc, bag)))
        {
            logger.Warning($"[K9Companion] Refused jacket {bagId}: not the jacket this profile's dog is wearing.");
            return Refused;
        }
        Save(pmc, new BagRecord { BagId = bagId, InRaid = true, RecordedAt = Now, FirstJacketGiven = true, KitIds = kits, CollarsGiven = record.CollarsGiven });
        logger.Info($"[K9Companion] Dog jacket {bagId} goes into the raid.");
        return Ok;
    }

    public string DogDied(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        if (pmc == null) return Refused;
        BagRecord record = Load(pmc);
        if (record.InRaid || record.KitIds is { Count: > 0 }) { record.DogDied = true; Save(pmc, record); }
        return Ok;
    }

    // ---- server events --------------------------------------------------------------------------------------

    public void GameStarted(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null) return;
        EmptyVetKits(pmc);
        // Jackets taken out in 0.20 become the ones that replaced them (the same item, with what's inside it).
        int converted = 0;
        foreach (Item item in pmc.Inventory.Items)
            if (DogGear.ReplacedBy.TryGetValue(item.Template, out MongoId replacement)) { item.Template = replacement; converted++; }
        if (converted > 0)
        {
            logger.Info($"[K9Companion] {converted} old dog jacket(s) swapped for the new harnesses.");
            FitJackets(pmc);
        }
        BagRecord record = Load(pmc);
        if (record.InRaid && Now - record.RecordedAt > StaleSeconds) { record = Cleared(record); Save(pmc, record); }
        if (!record.InRaid) HeadsetsToCameras(pmc);
        // A profile without a dog (a new handler before Trust Is Earned) gets no dog gear yet.
        if (!record.InRaid && k9.Owned(pmc)) { EnsureKit(pmc, record); CameraOntoJacket(pmc); EnsureCollars(pmc, record, earned: true); }
    }

    // 0.31.1-0.31.3 made the K9 veterinary kit a med case; it's a medkit again, so anything still inside one goes to the
    // stash (with no room there it stays, and the server says so).
    private void EmptyVetKits(PmcData pmc)
    {
        List<Item> items = pmc.Inventory!.Items!;
        var kits = items.Where(i => i.Template == K9Ids.VetKit).Select(i => i.Id.ToString()).ToHashSet();
        if (kits.Count == 0) return;
        int moved = 0;
        foreach (Item inside in items.Where(i => i.ParentId != null && kits.Contains(i.ParentId)).ToList())
        {
            string parent = inside.ParentId!, slot = inside.SlotId!;
            var was = inside.Location;
            inside.Location = null;
            if (Place(pmc, inside.Id, pmc.Inventory.Stash)) { moved++; continue; }
            inside.ParentId = parent; inside.SlotId = slot; inside.Location = was;
            logger.Warning("[K9Companion] Something is still inside a K9 veterinary kit (it's a medkit again) and there's no room in your stash for it: make some room and restart the game.");
        }
        if (moved > 0) logger.Info($"[K9Companion] The K9 veterinary kit is a medkit again: {moved} item(s) that were inside one went to your stash.");
    }

    /// <summary>
    /// The collars became items in 0.31, worn in the dog's collar slot. Once per profile: a dog set up before then gets
    /// the collars it had earned (K9Keeper.Collars), the one it wore in its slot and the others in the stash (a tactical
    /// collar already in the stash is the one it gets); a new dog comes with the coyote training collar on (its quest
    /// rewards hand over the others).
    /// </summary>
    private void EnsureCollars(PmcData pmc, BagRecord record, bool earned)
    {
        if (record.CollarsGiven) return;
        List<Item> items = pmc.Inventory!.Items!;
        Item? holder = items.FirstOrDefault(i => i.Template == DogGear.Holder);
        if (holder == null) return;
        List<string> styles = earned ? K9Keeper.Collars(pmc) : new List<string> { "training" };
        string? chosen = k9.Load(pmc)?.Collar;
        string worn = chosen != null && styles.Contains(chosen) ? chosen : "training";
        bool slotFree = !items.Any(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.CollarSlot);
        int given = 0;
        foreach (string style in styles)
        {
            MongoId template = DogGear.CollarFor(style);
            Item? collar = items.FirstOrDefault(i => i.Template == template);
            bool made = collar == null;
            if (made) { collar = new Item { Id = new MongoId(), Template = template }; items.Add(collar); }
            if (style == worn && slotFree)
            {
                collar!.ParentId = holder.Id.ToString(); collar.SlotId = DogGear.CollarSlot; collar.Location = null;
                slotFree = false; given++;
            }
            else if (made && Place(pmc, collar!.Id, pmc.Inventory.Stash)) given++;
            else if (made) { items.Remove(collar!); logger.Warning($"[K9Companion] No room in the stash for the dog's {style} collar; Dr. Yang sells them."); }
        }
        record.CollarsGiven = true;
        Save(pmc, record);
        if (given > 0) logger.Info($"[K9Companion] The dog's collars are items now: {given} handed over (its {worn} collar is in its collar slot).");
    }

    // The camera headset was taken out in 0.29.1: each one becomes an action camera (same item, so its insurance and
    // place stay). One on the dog's head moves to the dog's camera slot, and from there onto its jacket
    // (CameraOntoJacket); if that's taken, it goes to the stash.
    private void HeadsetsToCameras(PmcData pmc)
    {
        List<Item> items = pmc.Inventory!.Items!;
        Item? holder = items.FirstOrDefault(i => i.Template == DogGear.Holder);
        int converted = 0;
        foreach (Item headset in items.Where(i => i.Template == K9Ids.CameraHeadset).ToList())
        {
            headset.Template = K9Ids.ActionCamera; converted++;
            if (holder == null || headset.ParentId != holder.Id.ToString() || headset.SlotId != DogGear.HeadSlot) continue;
            if (!items.Any(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.CameraSlot)) { headset.SlotId = DogGear.CameraSlot; continue; }
            if (!Place(pmc, headset.Id, pmc.Inventory.Stash))
                logger.Warning("[K9Companion] No room in the stash for the dog's old camera headset (now an action camera); it stays on the dog's head.");
        }
        if (converted > 0) logger.Info($"[K9Companion] {converted} K9 camera headset(s) became K9 action camera(s).");
    }

    /// <summary>Sets up the dog's gear now (Trust Is Earned hands the dog over).</summary>
    public void SetUpGear(PmcData pmc)
    {
        BagRecord record = Load(pmc);
        if (!record.InRaid) { EnsureKit(pmc, record); EnsureCollars(pmc, record, earned: false); }
    }

    /// <summary>
    /// The kennel's two lockers, hidden containers in the quest stash like the gear holder (the DOG tab opens them once
    /// the kennel is built far enough). Created once per owner; their contents stay at home during raids.
    /// </summary>
    public void EnsureLockers(PmcData pmc)
    {
        List<Item> items = pmc.Inventory!.Items!;
        foreach (MongoId template in new[] { K9Ids.ArmourLocker, K9Ids.PremiumLocker })
        {
            if (items.Any(i => i.Template == template)) continue;
            var locker = new Item { Id = new MongoId(), Template = template };
            items.Add(locker);
            if (Place(pmc, locker.Id, pmc.Inventory.QuestStashItems)) logger.Info("[K9Companion] Kennel locker set up.");
            else { items.Remove(locker); logger.Warning("[K9Companion] No room for a kennel locker yet; trying again next game start."); }
        }
    }

    public void RaidEnded(MongoId sessionId, PmcData pmc, bool isDead, bool isTransfer, EndLocalRaidRequestData request, string? location = null)
    {
        BagRecord record = Load(pmc);
        try { SettleRaid(sessionId, pmc, isDead, isTransfer, request, location, record); }
        finally { wentIn.Remove(sessionId); insuredIn.Remove(sessionId); }
    }

    private void SettleRaid(MongoId sessionId, PmcData pmc, bool isDead, bool isTransfer, EndLocalRaidRequestData request, string? location, BagRecord record)
    {
        PmcData? post = request.Results?.Profile;
        var outcome = BagLedger.RaidEnd(pmc.Inventory!.Items!, record, post?.Inventory?.Items, post?.Inventory?.QuestRaidItems?.ToString(),
            isDead, isTransfer, id => BagLedger.PutBack(pmc.Inventory.Items!, KitFor(pmc, record), id));
        // Moving to the next map: the jacket and head gear stay with the dog (and at risk).
        if (outcome == BagLedger.Outcome.Transit || (isTransfer && record.KitIds is { Count: > 0 })) return;
        if (record.KitIds is { Count: > 0 } && (isDead || record.DogDied))
        {
            foreach (string id in record.KitIds) BagLedger.RemoveWithChildren(pmc.Inventory.Items!, new MongoId(id));
            Insure(sessionId, pmc, record.KitIds.Select(id => new MongoId(id)), location, request);
            logger.Info($"[K9Companion] The dog's head and camera gear ({record.KitIds.Count}) were lost with " + (isDead ? "you." : "the dog."));
        }
        if (outcome != BagLedger.Outcome.Untouched) logger.Info($"[K9Companion] Raid over, dog jacket: {outcome}.");
        if (outcome is BagLedger.Outcome.LostWithPlayer or BagLedger.Outcome.LostWithDog or BagLedger.Outcome.Lost && wentIn.TryGetValue(sessionId, out var gone))
        {
            // The jacket and everything in it as it went in (whatever's left of it was removed above).
            var bagId = new MongoId(record.BagId!);
            var tree = new HashSet<MongoId> { bagId };
            bool grew = true;
            while (grew) { grew = false; foreach (Item i in gone) if (i.ParentId != null && tree.Contains(new MongoId(i.ParentId)) && tree.Add(i.Id)) grew = true; }
            Insure(sessionId, pmc, tree, location, request);
        }
        if (outcome == BagLedger.Outcome.KeptButNoStashSpace)
            logger.Warning("[K9Companion] Nowhere to put the dog's jacket back; it will be put back next time you start the game.");
        record = Cleared(record);
        Save(pmc, record);
        if (k9.Owned(pmc)) EnsureKit(pmc, record);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static BagRecord Cleared(BagRecord record) => new BagRecord { FirstJacketGiven = record.FirstJacketGiven, CollarsGiven = record.CollarsGiven };

    private BagLedger.Kit KitFor(PmcData pmc, BagRecord record) => new BagLedger.Kit
    {
        HolderTemplate = DogGear.Holder, Jackets = DogGear.JacketIds, FirstJacket = DogGear.Black,
        StashId = pmc.Inventory!.Stash?.ToString(), QuestStashId = pmc.Inventory.QuestStashItems?.ToString(),
        RaidContainerId = pmc.Inventory.QuestRaidItems?.ToString(),
        PlaceHolder = id => Place(pmc, id, pmc.Inventory.QuestStashItems),
        PutInStash = id => Place(pmc, id, pmc.Inventory.Stash),
        Preferred = PreferredJacket(pmc),
        // No free jacket: the dog comes with nothing on, and its gear is bought from Dr. Yang.
        FirstJacketGiven = true
    };

    private void EnsureKit(PmcData pmc, BagRecord record)
    {
        FitJackets(pmc);
        var report = BagLedger.EnsureKit(pmc.Inventory!.Items!, KitFor(pmc, record));
        if (report.HolderCreated) logger.Info("[K9Companion] Dog gear set up" + (report.Migrated ? "; the dog now wears the bag it carried before." : "."));
        if (report.FirstJacketGiven) logger.Info("[K9Companion] Your dog has its first jacket on (see the DOG tab).");
        if (report.Returned > 0) logger.Info($"[K9Companion] {report.Returned} dog jacket(s) brought back from an unfinished raid.");
        if (report.NoRoom) logger.Warning("[K9Companion] No room to set up the dog's gear yet; trying again next game start.");
        if (!record.FirstJacketGiven && pmc.Inventory.Items!.Any(i => DogGear.IsJacket(i.Template)))
        { record.FirstJacketGiven = true; Save(pmc, record); }
    }

    // Jackets changed size in 0.15 (each tier has its own), the K9 veterinary kit (2x1) and training kit (3x2) in 0.31. One that no
    // longer fits where it lies in the stash (overlapping something, or past the edge) is moved to a free spot, so the
    // stash always loads.
    private void FitJackets(PmcData pmc)
    {
        List<Item> items = pmc.Inventory!.Items!;
        MongoId? stashId = pmc.Inventory.Stash;
        if (!stashId.HasValue) return;
        var (width, height) = StashSize(pmc, stashId.Value);
        foreach (Item jacket in items.Where(i => (DogGear.IsJacket(i.Template) || i.Template == K9Ids.VetKit || i.Template == K9Ids.TrainingKit) && i.ParentId == stashId.Value.ToString()).ToList())
        {
            var at = jacket.GetParsedLocation();
            var size = templates.Items[jacket.Template].Properties;
            if (at?.X == null || at.Y == null || size?.Width == null || size.Height == null) continue;
            int w = at.IsVertical() ? size.Height.Value : size.Width.Value, h = at.IsVertical() ? size.Width.Value : size.Height.Value;
            int[,] others = inventory.GetContainerMap(width, height, items.Where(i => i.Id != jacket.Id), stashId.Value);
            bool fits = at.X.Value >= 0 && at.Y.Value >= 0 && at.X.Value + w <= width && at.Y.Value + h <= height;
            for (int y = 0; fits && y < h; y++)
                for (int x = 0; fits && x < w; x++)
                    if (others[at.Y.Value + y, at.X.Value + x] != 0) fits = false;
            if (fits) continue;
            var was = jacket.Location;
            jacket.Location = null;
            if (Place(pmc, jacket.Id, stashId)) { logger.Info("[K9Companion] A dog jacket or vet kit no longer fitted where it was in your stash; moved it to a free spot."); continue; }
            if (!DogGear.IsJacket(jacket.Template)) { jacket.Location = was; logger.Warning("[K9Companion] A K9 veterinary or training kit no longer fits where it is in your stash and there's no free spot: make some room."); continue; }
            Item? holder = items.FirstOrDefault(i => i.Template == DogGear.Holder);
            if (holder != null && !items.Any(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.Slot))
            { jacket.ParentId = holder.Id.ToString(); jacket.SlotId = DogGear.Slot; logger.Info("[K9Companion] A dog jacket no longer fitted in your stash; the dog is wearing it now."); continue; }
            jacket.Location = was;
            logger.Warning("[K9Companion] A dog jacket no longer fits where it is in your stash and there's no free spot: make some room.");
        }
    }

    private (int Width, int Height) StashSize(PmcData pmc, MongoId stashId)
    {
        Item? stash = pmc.Inventory!.Items!.FirstOrDefault(i => i.Id == stashId);
        var grid = stash != null && templates.Items.TryGetValue(stash.Template, out TemplateItem? t) ? t.Properties?.Grids?.FirstOrDefault()?.Properties : null;
        int width = grid?.CellsH is > 0 ? grid.CellsH.Value : 10;
        int height = grid?.CellsV is > 0 ? grid.CellsV.Value : 66;
        var rows = pmc.Bonuses?.FirstOrDefault(b => b.Type == BonusType.StashRows);
        if (rows?.Value != null) height += (int)rows.Value.Value;
        return (width, height);
    }

    // The bag the client had marked as worn before the holder existed ("none" = no vest), from the needs file.
    private string? PreferredJacket(PmcData pmc)
    {
        string path = $"user/profileData/{pmc.Id}/K9Companion.needs.json";
        try
        {
            if (!files.FileExists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(files.ReadFile(path));
            return doc.RootElement.TryGetProperty("vestId", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    // Of the ids the client sent, the ones really in the dog's head and camera slots.
    private static List<string> WornKits(PmcData pmc, List<string>? ids)
    {
        if (ids == null || ids.Count == 0) return new List<string>();
        Item? holder = pmc.Inventory!.Items!.FirstOrDefault(i => i.Template == DogGear.Holder);
        if (holder == null) return new List<string>();
        return pmc.Inventory.Items!.Where(i => i.ParentId == holder.Id.ToString() && (i.SlotId == DogGear.HeadSlot || i.SlotId == DogGear.CameraSlot)
                                               && ids.Contains(i.Id.ToString())).Select(i => i.Id.ToString()).ToList();
    }

    // Since 0.25 the action camera clips onto the jacket; one still in the dog's old Camera slot goes onto the jacket it
    // wears, if that has a free camera slot, or else (since 0.31, when the collar slot took its place in the DOG tab)
    // into the stash; with no room there it stays where it is until the next game start.
    private void CameraOntoJacket(PmcData pmc)
    {
        List<Item> items = pmc.Inventory!.Items!;
        Item? holder = items.FirstOrDefault(i => i.Template == DogGear.Holder);
        if (holder == null) return;
        Item? camera = items.FirstOrDefault(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.CameraSlot);
        if (camera == null) return;
        Item? jacket = items.FirstOrDefault(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.Slot);
        bool hasSlot = jacket != null && templates.Items.TryGetValue(jacket.Template, out TemplateItem? t) && t.Properties?.Slots?.Any(s => s.Name == DogGear.CameraSlot) == true;
        if (hasSlot && !items.Any(i => i.ParentId == jacket!.Id.ToString() && i.SlotId == DogGear.CameraSlot))
        {
            camera.ParentId = jacket!.Id.ToString(); camera.SlotId = DogGear.CameraSlot; camera.Location = null;
            logger.Info("[K9Companion] The dog's action camera now clips onto its jacket.");
            return;
        }
        string parent = camera.ParentId!, slot = camera.SlotId!;
        camera.Location = null;
        if (Place(pmc, camera.Id, pmc.Inventory.Stash)) { logger.Info("[K9Companion] The dog's spare action camera went to your stash (its jacket has no free camera mount)."); return; }
        camera.ParentId = parent; camera.SlotId = slot;
        logger.Warning("[K9Companion] No room in the stash for the dog's spare action camera; trying again next game start.");
    }

    private bool OnDog(PmcData pmc, Item item)
    {
        Item? holder = pmc.Inventory!.Items!.FirstOrDefault(i => i.Template == DogGear.Holder);
        return holder != null && item.ParentId == holder.Id.ToString() && item.SlotId == DogGear.Slot;
    }

    // Places an item (with children) in a stash-like container's grid: the stash, or the quest stash for the holder.
    private bool Place(PmcData pmc, MongoId bagId, MongoId? stashId)
    {
        List<Item> items = pmc.Inventory!.Items!;
        Item? stash = stashId.HasValue ? items.FirstOrDefault(i => i.Id == stashId.Value) : null;
        if (stash == null || !templates.Items.TryGetValue(stash.Template, out TemplateItem? stashTemplate)) return false;
        var grid = stashTemplate.Properties?.Grids?.FirstOrDefault()?.Properties;
        int width = grid?.CellsH is > 0 ? grid.CellsH.Value : 10;
        int height = grid?.CellsV is > 0 ? grid.CellsV.Value : 66;
        var rows = pmc.Bonuses?.FirstOrDefault(b => b.Type == BonusType.StashRows);
        if (rows?.Value != null && stashId == pmc.Inventory.Stash) height += (int)rows.Value.Value;
        int[,] map = inventory.GetContainerMap(width, height, items, stashId!.Value);
        var result = inventory.PlaceItemInContainer(map, items.GetItemWithChildren(bagId), stashId.Value.ToString(), "hideout");
        return result.Success == true;
    }

    private static bool InStash(PmcData pmc, Item item)
    {
        var byId = pmc.Inventory!.Items!.ToDictionary(i => i.Id.ToString());
        for (Item? at = item; at?.ParentId != null; at = byId.GetValueOrDefault(at.ParentId))
            if (at.ParentId == pmc.Inventory.Stash?.ToString()) return true;
        return false;
    }

    private string RecordPath(PmcData pmc) => $"user/profileData/{pmc.Id}/K9Companion.json";

    private BagRecord Load(PmcData pmc)
    {
        string path = RecordPath(pmc);
        try { return files.FileExists(path) ? json.Deserialize<BagRecord>(files.ReadFile(path)) ?? new BagRecord() : new BagRecord(); }
        catch (Exception e) { logger.Error($"[K9Companion] Couldn't read {path}: {e.Message}"); return new BagRecord(); }
    }

    private void Save(PmcData pmc, BagRecord record)
    {
        string path = RecordPath(pmc);
        string folder = System.IO.Path.GetDirectoryName(path)!;
        if (!files.DirectoryExists(folder)) files.CreateDirectory(folder);
        files.WriteFile(path, json.Serialize(record, true)!);
    }
}
