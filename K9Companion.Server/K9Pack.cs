using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace K9Companion.Server;

/// <summary>
/// More than one dog. Dr. Yang sells adoption papers for each breed and coat (once you have your first dog, from Trust Is Earned);
/// the DOG tab adopts the dog on papers in your stash. Every dog has its own name, coat, needs, health, gear, bed,
/// training, presets and kennel; they all live in the hideout, each at its own kennel, and one of them (the active dog)
/// comes on raids.
///
/// Everything the single-dog code keeps (K9State's tag/bed/training/presets, the needs file, the kennel's three Rest Space
/// improvements, the gear holder's slots) is always the ACTIVE dog's; the others' are kept in their DogRecord here and
/// swapped in when you change dogs. The gear is moved by the client (the game's own item moves, so the client's profile
/// stays in step): each dog not out keeps its gear in a roster holder in the quest stash, of which there's always one spare.
/// </summary>
public static class Pack
{
    public const string Shepherd = "shepherd";

    // One breed: its name and price, and its three coats (the pack's coat1-3, named as they look). Every coat is a paper of
    // its own at Dr. Yang's, all at the breed's price; you can have one dog of each breed.
    public sealed record BreedInfo(string Breed, string Name, string Short, int Index, int Price, int Level, string Blurb, string[] Coats, string[] CoatShort);
    public sealed record Card(BreedInfo Of, int Coat, MongoId Template, MongoId Offer)
    {
        public string Breed => Of.Breed;
        public string Name => Of.Name;
        public string CoatName => Of.Coats[Coat - 1];
        public int Price => Of.Price;
        public int Level => Of.Level;
    }

    public static readonly BreedInfo[] Breeds =
    {
        new("rottweiler", "Rottweiler", "Rottie", 0, 950_000, 3,
            "A guard dog with a crushing bite and a thick hide: it takes more to bring down than any other breed.",
            new[] { "black and tan", "black and mahogany", "grey and tan" }, new[] { "B/T", "B/M", "G/T" }),
        new("husky", "Siberian Husky", "Husky", 1, 750_000, 2,
            "Built to run all day: the fastest of Dr. Yang's dogs, and tough enough for a fight.",
            new[] { "black and white", "copper", "silver" }, new[] { "B/W", "Cop", "Sil" }),
        new("beagle", "Beagle", "Beagle", 2, 450_000, 1,
            "A scent hound: it smells valuables further off than any other breed. Light, quick to tire, and no fighter.",
            new[] { "tricolour", "pied", "red and white" }, new[] { "Tri", "Pied", "R/W" }),
        new("shibainu", "Shiba Inu", "Shiba", 3, 550_000, 2,
            "Agile and alert, with a good nose. Small enough to be hard to hit, brave enough to go for the legs.",
            new[] { "light red", "red", "cream" }, new[] { "L.red", "Red", "Crm" }),
        new("pug", "Pug", "Pug", 4, 350_000, 1,
            "Small, stubborn and fearless. Easy to miss in a firefight, quiet on the move, and its bite is more insult than injury.",
            new[] { "fawn", "apricot", "black" }, new[] { "Fawn", "Apr", "Blk" }),
    };

    // The adoption papers: coat 1 keeps the ids the breed's pet card had (cards bought before are its coat-1 papers).
    public static readonly Card[] Cards = Breeds.SelectMany(b => new[]
    {
        new Card(b, 1, K9Ids.Template(0x40 + b.Index), K9Ids.Offer(0xa0 + b.Index)),
        new Card(b, 2, K9Ids.Template(0x50 + b.Index), K9Ids.Offer(0xc0 + b.Index)),
        new Card(b, 3, K9Ids.Template(0x58 + b.Index), K9Ids.Offer(0xc8 + b.Index)),
    }).ToArray();

    public static Card? CardFor(string? breed, int coat = 1) => Cards.FirstOrDefault(c => c.Breed == breed && c.Coat == coat) ?? Cards.FirstOrDefault(c => c.Breed == breed);
    public static Card? CardOf(MongoId template) => Cards.FirstOrDefault(c => c.Template == template);

    /// <summary>Breeds this player can't buy papers for: one they have a dog of, or papers of (any coat, anywhere on them).</summary>
    public static HashSet<string> Taken(PmcData? pmc, K9State? state)
    {
        var taken = new HashSet<string>();
        if (state?.Dogs != null) foreach (DogRecord d in state.Dogs) taken.Add(d.Breed);
        if (pmc?.Inventory?.Items != null)
            foreach (Item i in pmc.Inventory.Items) { Card? c = CardOf(i.Template); if (c != null) taken.Add(c.Breed); }
        return taken;
    }
    public static bool IsBreed(string? breed) => breed == Shepherd || CardFor(breed) != null;

    // The roster holder: as the dog gear holder (same four slots), for a dog that isn't out.
    public static readonly MongoId RosterHolder = new("6a5d0c0c4fb9a3c5e1d0d090");
    public static readonly MongoId[] RosterSlotIds = { new("6a5d0c0c4fb9a3c5e1d0d091"), new("6a5d0c0c4fb9a3c5e1d0d092"), new("6a5d0c0c4fb9a3c5e1d0d093"), new("6a5d0c0c4fb9a3c5e1d0d094") };
}

/// <summary>One dog. While it's the active dog, its kept fields here are stale: K9State, the needs file etc. hold its own.</summary>
public sealed class DogRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("breed")] public string Breed { get; set; } = Pack.Shepherd;
    [JsonPropertyName("coat")] public int Coat { get; set; } = 1;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("adopted")] public long Adopted { get; set; }
    // Growing up (Growth): a dog from adoption papers starts young; dogs from before (and the K9 unit's shepherd) are grown.
    [JsonPropertyName("young")] public bool Young { get; set; }
    [JsonPropertyName("raidsSurvived")] public int RaidsSurvived { get; set; }
    // Kept while it isn't the active dog:
    [JsonPropertyName("tag")] public string Tag { get; set; } = "steel";
    [JsonPropertyName("bed")] public int Bed { get; set; }
    [JsonPropertyName("training")] public int Training { get; set; }
    [JsonPropertyName("presets")] public List<string> Presets { get; set; } = new();
    // Its needs file as it was (null: a new dog, fed and healthy), and when it was set aside (it heals at its kennel meanwhile).
    [JsonPropertyName("needs")] public string? Needs { get; set; }
    [JsonPropertyName("asideSince")] public long AsideSince { get; set; }
    // Its kennel's three levels, as the hideout keeps them (built, or under construction until the time given).
    [JsonPropertyName("kennel")] public List<KennelStep> Kennel { get; set; } = new();
    // The roster holder its gear is in (null: none, or it has no gear).
    [JsonPropertyName("holder")] public string? Holder { get; set; }
}

public sealed class KennelStep
{
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
}

public sealed class PackRequest : IRequestData
{
    [JsonPropertyName("dog")] public string? Dog { get; set; }
    [JsonPropertyName("breed")] public string? Breed { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("coat")] public int? Coat { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("z")] public double? Z { get; set; }
    [JsonPropertyName("yaw")] public double? Yaw { get; set; }
}

[Injectable(InjectionType.Singleton)]
public sealed class PackKeeper(ISptLogger<PackKeeper> logger, ProfileHelper profiles, K9Store store, BagKeeper bags, FileUtil files, JsonUtil json)
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string NeedsPath(PmcData pmc) => $"user/profileData/{pmc.Id}/K9Companion.needs.json";

    /// <summary>
    /// How grown a dog is, 0 (just adopted) to 1: a thirtieth for every day you've had it and a twentieth for every raid
    /// it's come back from, together. Grown in 30 days at home or 20 raids survived, sooner with both. The client sizes
    /// the dog by it, and its health, bite and pace with it.
    /// </summary>
    public static double Growth(DogRecord d)
    {
        if (!d.Young) return 1;
        double days = Math.Max(0, Now - d.Adopted) / 86400.0;
        return Math.Clamp(days / 30.0 + d.RaidsSurvived / 20.0, 0, 1);
    }

    /// <summary>The roster as it should be: an owner has at least their first dog (the shepherd), which is active.</summary>
    public static bool Sanitize(K9State state)
    {
        bool changed = false;
        state.Dogs ??= new();
        state.Dogs.RemoveAll(d => d == null || !K9Store.IsId(d.Id) || !Pack.IsBreed(d.Breed));
        if (state.Owned && state.Dogs.Count == 0)
        {
            state.Dogs.Add(new DogRecord { Id = new MongoId().ToString(), Breed = Pack.Shepherd, Coat = 1, Adopted = Now });
            changed = true;
        }
        foreach (DogRecord d in state.Dogs)
        {
            d.Coat = Math.Clamp(d.Coat, 1, 3); d.Bed = Math.Clamp(d.Bed, 0, 2); d.Training = Math.Clamp(d.Training, 0, 3);
            d.Name = Clean(d.Name); d.Presets ??= new(); d.Kennel ??= new();
            d.Kennel.RemoveAll(k => k == null || k.Level is < 1 or > 3);
        }
        if (state.Dogs.Count > 0 && !state.Dogs.Any(d => d.Id == state.Active)) { state.Active = state.Dogs[0].Id; changed = true; }
        return changed;
    }

    private static string Clean(string? name)
    {
        string n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return n.Length > 24 ? n[..24] : n;
    }

    // ---- what the client sees -------------------------------------------------------------------------------------

    public List<Dictionary<string, object?>> Roster(PmcData pmc, K9State state)
    {
        var list = new List<Dictionary<string, object?>>();
        List<Item> items = pmc.Inventory?.Items ?? new List<Item>();
        foreach (DogRecord d in state.Dogs)
        {
            bool active = d.Id == state.Active;
            JsonElement? needs = null;
            if (!active && d.Needs != null) { try { needs = JsonDocument.Parse(d.Needs).RootElement; } catch { /* a fresh dog */ } }
            Item? jacket = !active && d.Holder != null ? items.FirstOrDefault(i => i.ParentId == d.Holder && i.SlotId == DogGear.Slot) : null;
            Item? collar = !active && d.Holder != null ? items.FirstOrDefault(i => i.ParentId == d.Holder && i.SlotId == DogGear.CollarSlot) : null;
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = d.Id, ["breed"] = d.Breed, ["coat"] = d.Coat, ["name"] = d.Name, ["active"] = active,
                ["kennel"] = active ? Kennel.CurrentLevel(pmc) : KennelLevel(d), ["building"] = active ? 0 : Building(d),
                ["bed"] = active ? state.Bed : d.Bed, ["training"] = active ? state.Training : d.Training,
                ["holder"] = d.Holder, ["jacket"] = jacket?.Template.ToString(), ["collar"] = collar?.Template.ToString(),
                ["growth"] = Math.Round(Growth(d), 3), ["raidsSurvived"] = d.RaidsSurvived, ["adopted"] = d.Adopted,
                // Its needs as it left them (the active dog's are the needs file itself).
                ["needs"] = needs,
            });
        }
        return list;
    }

    private static int KennelLevel(DogRecord d)
    {
        int level = 0;
        while (level < 3 && d.Kennel.Any(k => k.Level == level + 1 && (k.Completed || (k.At > 0 && k.At <= Now)))) level++;
        return level;
    }

    private static int Building(DogRecord d) => d.Kennel.FirstOrDefault(k => !k.Completed && k.At > Now)?.Level ?? 0;

    // ---- adopting, naming ------------------------------------------------------------------------------------------

    /// <summary>The dog on a pet card the client has just used up (the card is gone through the game's own item removal).</summary>
    public string Adopt(MongoId sessionId, PackRequest? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return Fail("no character");
        Pack.Card? card = Pack.CardFor(request?.Breed, request?.Coat ?? 1);
        if (card == null) return Fail("unknown breed");
        if (!state.Owned || !Kennel.QuestDone(pmc, K9Ids.TrustIsEarned)) return Fail("Dr. Yang hands out other dogs once you have your first (Trust Is Earned)");
        // One dog of each breed (the shepherd and the five on pet cards) is the only limit.
        if (state.Dogs.Any(d => d.Breed == card.Breed)) return Fail($"you have a {card.Name} already: one dog of each breed");
        var dog = new DogRecord { Id = new MongoId().ToString(), Breed = card.Breed, Coat = card.Coat,
                                  Name = Clean(request?.Name), Adopted = Now, AsideSince = Now, Young = true };
        state.Dogs.Add(dog);
        store.Save(pmc, state);
        logger.Info($"[K9Companion] Adopted a {card.CoatName} {card.Name} ({state.Dogs.Count} dogs now).");
        return json.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["dog"] = dog.Id, ["dogs"] = Roster(pmc, state) }, false)!;
    }

    /// <summary>
    /// A dog given back to Dr. Yang (DOGS panel): it leaves the roster, and its breed's papers are on his shelf again. Not the
    /// active dog, and not the K9 unit's shepherd. The client has moved its gear to your stash first; its roster holder,
    /// empty now, stays as the spare.
    /// </summary>
    public string Release(MongoId sessionId, PackRequest? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        DogRecord? dog = state?.Dogs.FirstOrDefault(d => d.Id == request?.Dog);
        if (pmc == null || state == null || dog == null) return Fail("no such dog");
        if (dog.Id == state.Active) return Fail("that's your active dog: make another dog active first");
        if (dog.Breed == Pack.Shepherd) return Fail("the K9 unit's shepherd stays with you");
        state.Dogs.Remove(dog);
        store.Save(pmc, state);
        logger.Info($"[K9Companion] A {Pack.CardFor(dog.Breed, dog.Coat)?.Name ?? dog.Breed} went back to Dr. Yang ({state.Dogs.Count} dogs now).");
        return json.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["dogs"] = Roster(pmc, state) }, false)!;
    }

    public string Rename(MongoId sessionId, PackRequest? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        DogRecord? dog = state?.Dogs.FirstOrDefault(d => d.Id == request?.Dog);
        if (pmc == null || state == null || dog == null) return Fail("no such dog");
        if (request!.Name != null) dog.Name = Clean(request.Name);
        if (request.Coat is int coat) dog.Coat = Math.Clamp(coat, 1, 3);
        store.Save(pmc, state);
        return json.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["dogs"] = Roster(pmc, state) }, false)!;
    }

    /// <summary>Where a dog that isn't out has its kennel (the client places it once): kept in its needs, as the active dog's is.</summary>
    public string Home(MongoId sessionId, PackRequest? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        DogRecord? dog = state?.Dogs.FirstOrDefault(d => d.Id == request?.Dog && d.Id != state.Active);
        if (pmc == null || state == null || dog == null || request!.X is not double x || request.Y is not double y || request.Z is not double z || request.Yaw is not double yaw
            || !double.IsFinite(x + y + z + yaw) || Math.Abs(x) > 20000 || Math.Abs(y) > 20000 || Math.Abs(z) > 20000) return Fail("no such dog or spot");
        Dictionary<string, object> needs = new();
        if (dog.Needs != null)
            try { needs = (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dog.Needs) ?? new()).ToDictionary(p => p.Key, p => (object)p.Value); }
            catch { /* start from a fresh dog's */ }
        needs["homeSet"] = true; needs["homeX"] = Math.Round(x, 3); needs["homeY"] = Math.Round(y, 3); needs["homeZ"] = Math.Round(z, 3);
        needs["homeYaw"] = Math.Round(((yaw % 360) + 360) % 360, 1);
        dog.Needs = JsonSerializer.Serialize(needs);
        store.Save(pmc, state);
        return "{\"ok\":true}";
    }

    // ---- changing dogs ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes another dog the active one: the active dog's tag, bed, training, presets, needs and kennel go into its record,
    /// and the other's come out of its own. The reply says which roster holders the client moves the gear through (the
    /// active dog's into 'out', the new dog's out of 'in'), and the kennel levels and needs now in force.
    /// </summary>
    public string Switch(MongoId sessionId, PackRequest? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc?.Hideout == null || state == null) return Fail("no character");
        DogRecord? from = state.Dogs.FirstOrDefault(d => d.Id == state.Active), to = state.Dogs.FirstOrDefault(d => d.Id == request?.Dog);
        if (from == null || to == null) return Fail("no such dog");
        if (from == to) return Reply(pmc, state, null, null);
        string? outHolder = FreeHolder(pmc, state);
        if (outHolder == null && HasGear(pmc)) return Fail("there's no spare holder for its gear yet: restart the game once and try again");

        // The active dog's own things into its record.
        from.Tag = state.Tag; from.Bed = state.Bed; from.Training = state.Training; from.Presets = state.Presets.ToList();
        from.Needs = files.FileExists(NeedsPath(pmc)) ? files.ReadFile(NeedsPath(pmc)) : null;
        from.AsideSince = Now;
        from.Kennel = new List<KennelStep>();
        pmc.Hideout.Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        for (int i = 0; i < 3; i++)
            if (pmc.Hideout.Improvements.Remove(K9Ids.Kennel[i], out HideoutImprovement? built))
                from.Kennel.Add(new KennelStep { Level = i + 1, Completed = built.Completed == true, At = built.ImproveCompleteTimestamp ?? 0 });
        from.Holder = outHolder;

        // The new dog's out of its record.
        state.Tag = to.Tag; state.Bed = to.Bed; state.Training = to.Training; state.Presets = to.Presets.ToList();
        string inHolder = to.Holder ?? "";
        to.Holder = null;
        foreach (KennelStep k in to.Kennel)
            pmc.Hideout.Improvements[K9Ids.Kennel[k.Level - 1]] = new HideoutImprovement { Completed = k.Completed || (k.At > 0 && k.At <= Now), ImproveCompleteTimestamp = k.At };
        to.Kennel = new List<KennelStep>();
        WriteNeeds(pmc, to, KennelLevelNow(pmc));
        to.Needs = null;
        state.Active = to.Id;
        K9Store.Sanitize(state);
        store.Save(pmc, state);
        logger.Info($"[K9Companion] Active dog is now {(to.Name.Length > 0 ? to.Name : to.Breed)}.");
        return Reply(pmc, state, outHolder, inHolder);
    }

    private static int KennelLevelNow(PmcData pmc) => Kennel.CurrentLevel(pmc);

    // The dog's needs file as it left it, healed for the time it rested at its kennel (as the client heals a dog between raids).
    private void WriteNeeds(PmcData pmc, DogRecord dog, int kennel)
    {
        if (dog.Needs == null) { if (files.FileExists(NeedsPath(pmc))) files.DeleteFile(NeedsPath(pmc)); return; }
        string text = dog.Needs;
        try
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) ?? new();
            if (o.TryGetValue("health", out JsonElement h) && h.TryGetDouble(out double health) && dog.AsideSince > 0)
            {
                double hours = Math.Max(0, Now - dog.AsideSince) / 3600.0;
                double rate = kennel <= 0 ? 6 : kennel == 1 ? 10 + 3 * dog.Bed : kennel == 2 ? 16 + 3 * dog.Bed : 25 + 3 * dog.Bed;
                var copy = o.ToDictionary(p => p.Key, p => (object)p.Value);
                copy["health"] = Math.Round(Math.Min(100, health + hours * rate), 2);
                text = JsonSerializer.Serialize(copy);
            }
        }
        catch (Exception e) { logger.Warning($"[K9Companion] A dog's needs couldn't be read, keeping them as they were: {e.Message}"); }
        files.WriteFile(NeedsPath(pmc), text);
    }

    private string Reply(PmcData pmc, K9State state, string? outHolder, string? inHolder)
    {
        var kennel = new Dictionary<string, object?>();
        for (int i = 0; i < 3; i++)
            kennel[K9Ids.Kennel[i].ToString()] = pmc.Hideout?.Improvements != null && pmc.Hideout.Improvements.TryGetValue(K9Ids.Kennel[i], out var b)
                ? new Dictionary<string, object?> { ["completed"] = b.Completed == true, ["at"] = b.ImproveCompleteTimestamp ?? 0 } : null;
        return json.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true, ["active"] = state.Active, ["out"] = outHolder, ["in"] = inHolder, ["kennel"] = kennel,
            ["tag"] = state.Tag, ["bed"] = state.Bed, ["training"] = state.Training, ["presets"] = state.Presets, ["dogs"] = Roster(pmc, state)
        }, false)!;
    }

    private static string Fail(string why) => JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["problem"] = why });

    // ---- roster holders ----------------------------------------------------------------------------------------------

    private static bool HasGear(PmcData pmc)
    {
        List<Item> items = pmc.Inventory?.Items ?? new List<Item>();
        Item? holder = items.FirstOrDefault(i => i.Template == DogGear.Holder);
        return holder != null && items.Any(i => i.ParentId == holder.Id.ToString());
    }

    // A roster holder no dog is using, and empty.
    private static string? FreeHolder(PmcData pmc, K9State state)
    {
        List<Item> items = pmc.Inventory?.Items ?? new List<Item>();
        var used = state.Dogs.Select(d => d.Holder).Where(h => h != null).ToHashSet();
        return items.Where(i => i.Template == Pack.RosterHolder && !used.Contains(i.Id.ToString()) && !items.Any(c => c.ParentId == i.Id.ToString()))
                    .Select(i => i.Id.ToString()).FirstOrDefault();
    }

    /// <summary>
    /// Each game start: a roster holder for every dog that isn't out and has gear, and one spare for the next change of
    /// dogs, in the quest stash (the client only sees items that were there when the game loaded). A holder that's gone
    /// is forgotten; one nobody uses but that still holds gear (a change of dogs cut short) is given back to the dog it
    /// came from if that's clear, else left for the client to empty.
    /// </summary>
    public void GameStarted(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc?.Inventory?.Items == null || state == null || !state.Owned) return;
        List<Item> items = pmc.Inventory.Items;
        bool changed = false;
        foreach (DogRecord d in state.Dogs)
            if (d.Holder != null && !items.Any(i => i.Id.ToString() == d.Holder && i.Template == Pack.RosterHolder)) { d.Holder = null; changed = true; }
        int made = 0;
        while (FreeHolder(pmc, state) == null && made < 2)
        {
            var holder = new Item { Id = new MongoId(), Template = Pack.RosterHolder };
            items.Add(holder);
            if (!bags.PlaceInQuestStash(pmc, holder.Id)) { items.Remove(holder); logger.Warning("[K9Companion] No room in the quest stash for a dog's gear holder."); break; }
            made++;
        }
        if (changed) store.Save(pmc, state);
        if (made > 0) logger.Info("[K9Companion] A spare holder for your dogs' gear is ready.");
    }
}

[Injectable]
public sealed class PackRouter(JsonUtil json, PackKeeper pack) : StaticRouter(json, [
    new RouteAction<PackRequest>("/k9companion/pack/adopt",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(pack.Adopt(sessionId, info))),
    new RouteAction<PackRequest>("/k9companion/pack/release",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(pack.Release(sessionId, info))),
    new RouteAction<PackRequest>("/k9companion/pack/rename",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(pack.Rename(sessionId, info))),
    new RouteAction<PackRequest>("/k9companion/pack/home",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(pack.Home(sessionId, info))),
    new RouteAction<PackRequest>("/k9companion/pack/switch",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(pack.Switch(sessionId, info)))
]);

/// <summary>The pet cards (Dr. Yang, from Trust Is Earned) and the roster holder template.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 6)]
public sealed class PackItems(CustomItemService items, TemplateTable templates, TradersTable traders, ISptLogger<PackItems> logger) : IOnLoad
{
    // "Documents with decrypted data": a 1x1 folder of papers, so the adoption papers look like papers in the stash.
    private const string Papers = "660bc341c38b837877075e4c";
    internal static readonly MongoId Yang = new("6a5d0c0c4fb9a3c5e1d0d0a0");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (Pack.Card card in Pack.Cards)
        {
            if (templates.Items.ContainsKey(card.Template)) continue;
            var result = items.CreateItemFromClone(new NewItemFromCloneDetails
            {
                ItemTplToClone = new MongoId(Papers), NewId = card.Template, ParentId = templates.Items[new MongoId(Papers)].Parent,
                NewItemName = $"k9companion_papers_{card.Breed}_{card.Coat}",
                // What a trader pays for it is worked out from this: papers aren't worth reselling.
                FleaPriceRoubles = card.Price / 10, HandbookPriceRoubles = card.Price / 10, HandbookParentId = "5b47574386f77428ca22b2f1",
                Locales = K9Lang.Locales(new LocaleDetails { Name = $"K9 adoption papers: {card.Name} ({card.CoatName})", ShortName = $"{card.Of.Short} {card.Of.CoatShort[card.Coat - 1]}",
                    Description = $"A young {card.Name}, {card.CoatName}. " + card.Of.Blurb + " Use the papers in the DOG tab (DOGS, ADOPT) to take the dog home: it lives at its own kennel in your hideout and grows up with you, with every day you have it and every raid it comes back from. One dog of each breed. Dr. Yang's K9 programme." })
            });
            if (result.Success != true) { logger.Warning($"[K9Companion] The {card.Name} pet card couldn't be made: {string.Join(", ", result.Errors ?? new())}"); continue; }
            var props = templates.Items[card.Template].Properties!;
            props.CanSellOnRagfair = false; props.InsuranceDisabled = true; props.QuestItem = false;
            props.Width = 1; props.Height = 1;
        }

        if (templates.Items.TryGetValue(DogGear.Holder, out TemplateItem? holder) && !templates.Items.ContainsKey(Pack.RosterHolder))
        {
            var result = items.CreateItemFromClone(new NewItemFromCloneDetails
            {
                ItemTplToClone = DogGear.Holder, NewId = Pack.RosterHolder, ParentId = holder.Parent, NewItemName = "k9companion_dog_gear_kept",
                FleaPriceRoubles = 1, HandbookPriceRoubles = 1, HandbookParentId = "5b5f6fa186f77409407a7eb7",
                Locales = K9Lang.Locales(new LocaleDetails { Name = "K9 dog (at its kennel)", ShortName = "Dog", Description = "What one of your dogs not out with you is wearing." })
            });
            if (result.Success == true)
            {
                TemplateItem roster = templates.Items[Pack.RosterHolder];
                roster.Properties!.Slots = holder.Properties!.Slots!.Select((s, i) => new Slot
                {
                    Name = s.Name, Id = Pack.RosterSlotIds[i], Parent = Pack.RosterHolder, Prototype = s.Prototype,
                    Required = false, MergeSlotWithChildren = false, Properties = s.Properties
                }).ToList();
                roster.Properties.CanSellOnRagfair = false; roster.Properties.InsuranceDisabled = true;
            }
            else logger.Warning("[K9Companion] The dogs' spare gear holder couldn't be made: " + string.Join(", ", result.Errors ?? new()));
        }

        // Dr. Yang's offers: each card once Trust Is Earned is done, at its loyalty level.
        Trader? yang = traders.GetTrader(Yang);
        if (yang?.Assort?.Items != null)
        {
            Dictionary<MongoId, MongoId>? success = null;
            if (yang.QuestAssort != null && !yang.QuestAssort.TryGetValue("success", out success)) yang.QuestAssort["success"] = success = new Dictionary<MongoId, MongoId>();
            foreach (Pack.Card card in Pack.Cards.Where(c => templates.Items.ContainsKey(c.Template)))
            {
                yang.Assort.Items.RemoveAll(i => i.Id == card.Offer);
                yang.Assort.Items.Add(new Item { Id = card.Offer, Template = card.Template, ParentId = "hideout", SlotId = "hideout",
                                                // One at a time: a breed's card goes off the shelf once you have it or its dog (CardShelf).
                                                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = 1, BuyRestrictionMax = 1, BuyRestrictionCurrent = 0 } });
                yang.Assort.BarterScheme[card.Offer] = [[new BarterScheme { Count = card.Price, Template = K9Ids.Roubles }]];
                yang.Assort.LoyalLevelItems[card.Offer] = card.Level;
                if (success != null) success[card.Offer] = K9Ids.Quest(K9Ids.TrustIsEarned);
            }
            logger.Info($"[K9Companion] Dr. Yang has {Pack.Cards.Length} adoption papers ({Pack.Breeds.Length} breeds, 3 coats each; after Trust Is Earned).");
        }
        else logger.Warning("[K9Companion] Dr. Yang isn't loaded; no pet cards.");
        return Task.CompletedTask;
    }
}
