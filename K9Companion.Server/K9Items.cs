using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace K9Companion.Server;

/// <summary>
/// The K9 questline's items: the story items found in raids (where each is, see OnLoadAsync), the K9 veterinary kit, the dog's supplies and collars, and the kennel's two lockers (hidden containers in the quest stash,
/// like the dog's gear holder). All are clones of vanilla items; the client shows its own models and icons for the
/// collars, the food, the training kit, the vet kit and supply case and the K9 tag (GearVisuals there), the rest
/// keep those items' looks.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class K9Items(CustomItemService items, TemplateTable templates, LocationTable locations, PmcConfig pmcs, ISptLogger<K9Items> logger) : IOnLoad
{
    private sealed record Story(MongoId Id, string CloneOf, string Name, string Short, string Description);

    // Container templates (as the map loot tables name them).
    private const string Jacket = "578f8778245977358849a9b5", DeadScav = "5909e4b686f7747f5b744fa4", Drawer = "578f87b7245977356274f2cd";

    // Of these, only the tag and the insignia still spawn: profiles may hold the others (no quest has wanted the whistle
    // since 0.30, nor the jacket or the records since 0.37, nor the vet case since 0.38: the quest takes a K9 vet kit), so
    // they stay items.
    private static readonly Story[] StoryItems =
    {
        new(K9Ids.K9Tag, "5bc9bdb8d4351e003562b8a1", "K9 identification tag", "K9 tag",
            "A stamped steel tag from a working dog's collar: unit number, dog's name, handler's name. Scavs and PMCs carry them, and they turn up in scav corpses, jackets and drawers on Customs. Dr. Yang wants to know whose it was."),
        new(K9Ids.HandlerJacket, K9Ids.Fleece, "Dog handler's jacket", "Handler",
            "A worn fleece jacket with a K9 patch on the sleeve. It still carries the handler's scent."),
        new(K9Ids.DogWhistle, "56742c2e4bdc2d95058b456d", "Dog whistle", "Whistle",
            "A brass training whistle on a lanyard, the kind a handler uses at the edge of a dog's hearing."),
        new(K9Ids.TransportRecords, "590c645c86f77412b01304d9", "K9 transport records", "K9 log",
            "A transport log from the military base: which dogs left, in which truck, and where to."),
        new(K9Ids.VetSupplyCase, K9Ids.MedTools, "Veterinary supply case", "Vet case",
            "A sealed case of veterinary sutures, antiseptic and dosing syringes. Dr. Yang no longer needs these: The Last Handler takes a K9 veterinary kit."),
        new(K9Ids.K9Insignia, "5c1267ee86f77416ec610f72", "K9 unit insignia", "K9 badge",
            "The cloth-and-metal insignia of the base's canine unit. Scavs and PMCs carry them for luck, and they turn up in the Dorms on Customs."),
    };

    // What a scav or a PMC may carry, and the chance of each (BotQuestItemsPatch).
    public static readonly (MongoId Item, double Chance)[] OnBots = { (K9Ids.K9Tag, .25), (K9Ids.K9Insignia, .15) };

    // Where the loose items lie, as the map's own loose loot table names its spots (locationId), so they rest on real
    // furniture and floors. The crack house has no locked doors: these are its medicine shelves and tables on both floors.
    // The Dorms have many: these are in both blocks, on all floors, 5.5 m or more from any locked room's door.
    private static readonly string[] CrackHouse =
    {
        "(87.08203, 1.7120004, -145.03401)", "(82.12602, 2.6469998, -144.72502)", "(86.5, 1.7710004, -159.707)", "(89.12201, 1.8110003, -155.25902)",
        "(85.012, 5.088, -152.611)", "(86.871, 5.316, -162.202)", "(79.375, 5.39, -153.58)", "(88.53702, 5.6460004, -143.82501)",
        "(90.31601, 5.749, -153.28102)", "(89.60803, 5.673, -161.63402)"
    };
    private static readonly string[] Dorms =
    {
        "(168.696, 0.936, 156.919)", "(172.94, 0.868, 174.089)", "(182.87, 0.536, 170.585)", "(170.745, 3.631, 170.675)",
        "(180.68262, 3.6986003, 157.54118)", "(185.016, 3.884, 172.116)", "(189.696, 3.844, 183.657)", "(181.725, 6.687, 145.14297)",
        "(180.865, 7.758, 157.706)", "(226.309, 0.74, 149.457)", "(223.57, 3.943, 146.887)", "(235.223, 3.35, 153.501)"
    };

    // Category ids for the lockers' filters: food, drink, meds, medical supplies, household (fabrics) and building materials.
    private static readonly string[] LockerCategories =
        { "5448e8d04bdc2ddf718b4569", "5448e8d64bdc2dce718b4568", "543be5664bdc2dd4348b4569", "57864c8c245977548867e7f1",
          "590c745b86f7743cc433c5f2", "57864ada245977548638de91" };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (Story s in StoryItems)
        {
            if (!Clone(s.CloneOf, s.Id, "k9companion_" + s.Short.ToLowerInvariant().Replace(' ', '_'), 2000,
                    new LocaleDetails { Name = s.Name, ShortName = s.Short, Description = s.Description + " Dr. Yang's K9 questline." }))
                continue;
            var props = templates.Items[s.Id].Properties!;
            props.CanSellOnRagfair = false; props.InsuranceDisabled = true; props.QuestItem = false;
        }
        AddToLoot(K9Ids.K9Tag, "bigmap", new[] { Jacket, DeadScav, Drawer }, .15);
        // A PMC's loot comes from every item of the right kinds, ours too. The tag and the insignia are given to bots at a
        // set chance instead (OnBots), and what nobody wants any more shouldn't look as if it mattered.
        pmcs.GlobalLootBlacklist.AddRange(new[] { K9Ids.K9Tag, K9Ids.K9Insignia, K9Ids.HandlerJacket, K9Ids.DogWhistle, K9Ids.TransportRecords, K9Ids.VetSupplyCase });

        // A medkit for the dog only (a copy of the Grizzly, so it can be bound to a quick slot like one): using it in any
        // way treats the dog instead of you (the client's DogVet). 2x1; a full kit is all of the dog's health.
        if (Clone(K9Ids.Grizzly, K9Ids.VetKit, "k9companion_vet_kit", VetKitPrice, new LocaleDetails
        {
            Name = "K9 veterinary kit", ShortName = "K9 vet",
            Description = "Dr. Yang's field kit for working dogs: wound dressings, antiseptic and a sedative. The only thing that heals your dog, and " +
                          "for your dog only: use it with your dog beside you and you treat the dog. A full kit restores all of its health, a part-used " +
                          "one its share, and only what the dog needs is used (it goes half as far again at a kennel with a veterinary station)."
        }))
            DogMedKit(templates.Items[K9Ids.VetKit]);

        // Measured on generated raids: vet kits in the crack house in about eight raids out of ten, and an insignia in the
        // Dorms in about seven out of ten. (On Customs a spot's weight w comes up with a chance of about 1 - e^(-3.5 w).)
        Place("bigmap", "crackhouse", CrackHouse, .07, (K9Ids.VetKit, 1));
        Place("bigmap", "dorms", Dorms, .04, (K9Ids.K9Insignia, 1));

        // Dog supplies sold by loyalty level (DrYang): what each one does is the client's (DogNeeds, DogScreen.Kennel).
        Supply(K9Ids.Treats, "5448ff904bdc2d6f028b456e", "k9companion_treats", 3500, "K9 training treats", "Treats",
            "Dried liver treats for training and rewards. Your dog eats them like food (15% hunger).");
        Supply(K9Ids.PremiumFood, "57347da92459774491567cf5", "k9companion_premium_food", 9000, "K9 premium dog food", "K9 food+",
            "A large tin of high-protein working-dog food: a whole day's food for your dog (100% hunger).");
        Supply(K9Ids.BedLiner, K9Ids.Fleece, "k9companion_bed_liner", 12000, "K9 fleece bed liner", "Bed liner",
            "A fitted fleece liner for the kennel bed: the first bed upgrade in the DOG tab (instead of a fleece fabric).");
        Supply(K9Ids.ThermalBed, K9Ids.Ripstop, "k9companion_thermal_bed", 45000, "K9 thermal dog bed", "Thermal bed",
            "An insulated, raised working-dog bed: the second bed upgrade in the DOG tab (instead of ripstop and paracord).");
        Supply(K9Ids.TrainingKit, K9Ids.Paracord, "k9companion_training_kit", 30000, "K9 training kit", "Training",
            "Long line, clicker and bite sleeve. Use it in the DOG tab (TRAIN) to train your dog a level (up to 3): each level lets it smell valuables 2 m further off.");
        // The collars (DogGear.Collars): copies of a ring, which only shows dropped on the ground (the client draws the collar).
        Supply(K9Ids.TrainingCollar, "5d235a5986f77443f6329bc6", "k9companion_training_collar", 6500, "K9 training collar (Coyote)", "K9 collar",
            "A coyote nylon training collar with a quick-release buckle and a D-ring. Put it in your dog's collar slot in the DOG tab; the dog keeps it in raids.");
        Supply(K9Ids.BlackCollar, "5d235a5986f77443f6329bc6", "k9companion_black_collar", 22000, "K9 training collar (Black)", "K9 collar",
            "A black reinforced nylon training collar with a metal buckle. Put it in your dog's collar slot in the DOG tab; the dog keeps it in raids.");
        Supply(K9Ids.TacticalCollar, "5d235a5986f77443f6329bc6", "k9companion_tactical_collar", 85000, "K9 tactical collar", "K9 collar",
            "A wide handler's collar with a cobra buckle that won't give and a grab handle. Put it in your dog's collar slot in the DOG tab; the dog keeps it in raids.");
        foreach (var id in new[] { K9Ids.BedLiner, K9Ids.ThermalBed })
        {
            var props = templates.Items.GetValueOrDefault(id)?.Properties;
            if (props != null) { props.Width = 2; props.Height = 2; }
        }
        // The training kit is a duffel bag (its model, client-side): 3x2 since 0.31, not the paracord's 2x1.
        if (templates.Items.GetValueOrDefault(K9Ids.TrainingKit)?.Properties is { } kit) { kit.Width = 3; kit.Height = 2; }

        Locker(K9Ids.ArmourLocker, "k9companion_locker", "K9 armour locker", "Locker", 6, 6,
            "The Working Dog Station's locker: dog jackets, food, water and medical supplies.");
        Locker(K9Ids.PremiumLocker, "k9companion_premium_storage", "K9 premium storage", "Storage", 8, 8,
            "The Tactical K9 Facility's storage room: dog jackets, food, water and medical supplies.");
        logger.Info($"[K9Companion] K9 items registered ({StoryItems.Length} story items, vet kit, collars, 2 kennel lockers).");
        return Task.CompletedTask;
    }

    // A story item joins its map's loot tables for a few container types, weighted so that roughly 'chance' of the items
    // drawn from such a container are it (the tables' other items keep their relative odds).
    private void AddToLoot(MongoId item, string mapName, string[] containers, double chance)
    {
        Location? map = locations.GetLocation(mapName);
        if (map?.StaticLoot == null) { logger.Warning($"[K9Companion] No loot table for {mapName}; {item} won't spawn."); return; }
        map.StaticLoot.AddTransformer(loot =>
        {
            if (loot == null) return loot;
            foreach (string container in containers)
            {
                if (!loot.TryGetValue(new MongoId(container), out StaticLootDetails? details) || details.ItemDistribution == null) continue;
                var list = details.ItemDistribution.Where(d => d.Tpl != item).ToList();
                double total = list.Sum(d => (double)(d.RelativeProbability ?? 0));
                if (total <= 0) continue;
                list.Add(new ItemDistribution { Tpl = item, RelativeProbability = (float)Math.Max(1, total * chance / (1 - chance)) });
                details.ItemDistribution = list;
            }
            return loot;
        });
    }

    // Containers can't say where they are in a map (their tables are per container type), so to be found in one building an
    // item lies loose: a spawn point of our own at each of the map's spots, drawn with all the others by its weight. It shares
    // the spot's locationId, so if the game also picks the spot only one of the two spawns and nothing lies on top of anything.
    private void Place(string mapName, string place, string[] spots, double weight, params (MongoId Item, double Odds)[] what)
    {
        Location? map = locations.GetLocation(mapName);
        if (map?.LooseLoot == null) { logger.Warning($"[K9Companion] No loose loot table for {mapName}; nothing placed in {place}."); return; }
        map.LooseLoot.AddTransformer(loot =>
        {
            if (loot?.Spawnpoints == null) return loot;
            var all = loot.Spawnpoints.ToList();
            int n = 0;
            foreach (string spot in spots)
            {
                SpawnpointTemplate? theirs = all.FirstOrDefault(p => p.LocationId == spot)?.Template;
                if (theirs == null) { logger.Warning($"[K9Companion] {mapName} has no loot spot {spot} any more ({place})."); continue; }
                var items = what.Select(w => new SptLootItem { Id = new MongoId(), Template = w.Item, ComposedKey = w.Item.ToString(), Upd = new Upd { StackObjectsCount = 1 } }).ToList();
                all.Add(new Spawnpoint
                {
                    LocationId = spot, Probability = weight,
                    Template = theirs with { Id = $"k9companion_{place} ({n++})", IsAlwaysSpawn = false, Root = items[0].Id.ToString(), Items = items },
                    ItemDistribution = what.Select(w => new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = w.Item.ToString() }, RelativeProbability = w.Odds }).ToList()
                });
            }
            loot.Spawnpoints = all;
            return loot;
        });
    }

    public const int VetKitPrice = 28000, VetKitResource = 600;

    private static void DogMedKit(TemplateItem item)
    {
        var props = item.Properties!;
        props.Width = 2; props.Height = 1;
        props.MaxHpResource = VetKitResource;
        props.CanSellOnRagfair = true;
        // Nothing it does for you (it's never used on you): no bleeding, fracture or health effects listed.
        props.EffectsHealth = new();
        props.EffectsDamage = new();
    }

    private void Supply(MongoId id, string cloneOf, string name, int price, string full, string shortName, string description)
    {
        if (Clone(cloneOf, id, name, price, new LocaleDetails { Name = full, ShortName = shortName, Description = description }))
            templates.Items[id].Properties!.CanSellOnRagfair = true;
    }

    private void Locker(MongoId id, string name, string full, string shortName, int width, int height, string description)
    {
        MongoId itemCase = new("59fb042886f7746c5005a7b2");
        if (!Clone(itemCase.ToString(), id, name, 1, new LocaleDetails { Name = full, ShortName = shortName, Description = description })) return;
        var props = templates.Items[id].Properties!;
        props.Width = 1; props.Height = 1; props.Weight = 0;
        props.CanSellOnRagfair = false; props.InsuranceDisabled = true;
        var grid = props.Grids?.FirstOrDefault()?.Properties;
        if (grid == null) return;
        grid.CellsH = width; grid.CellsV = height;
        var filter = grid.Filters?.FirstOrDefault();
        if (filter == null) return;
        filter.Filter = LockerCategories.Select(c => new MongoId(c)).Concat(DogGear.JacketIds).Concat(DogGear.KitIds).Concat(DogGear.Collars.Keys)
            .Concat(new[] { K9Ids.VetKit, K9Ids.Treats, K9Ids.PremiumFood, K9Ids.BedLiner, K9Ids.ThermalBed, K9Ids.TrainingKit }).ToHashSet();
        filter.ExcludedFilter = new HashSet<MongoId> { DogGear.Holder, K9Ids.ArmourLocker, K9Ids.PremiumLocker };
    }

    private bool Clone(string cloneOf, MongoId id, string name, int price, LocaleDetails text)
    {
        var source = new MongoId(cloneOf);
        if (!templates.Items.TryGetValue(source, out TemplateItem? original))
        {
            logger.Error($"[K9Companion] Can't create {text.Name}: vanilla item {cloneOf} is missing.");
            return false;
        }
        var result = items.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = source, NewId = id, ParentId = original.Parent, NewItemName = name,
            HandbookParentId = HandbookCategory(source), HandbookPriceRoubles = price, FleaPriceRoubles = price,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = text }
        });
        if (!result.Success) logger.Error($"[K9Companion] Couldn't create {text.Name}: " + string.Join("; ", result.Errors ?? new List<string>()));
        return result.Success == true;
    }

    // The clone sits in the same handbook category as the item it copies (containers for the lockers).
    private static string HandbookCategory(MongoId source) => source.ToString() switch
    {
        "59fb042886f7746c5005a7b2" => "5b5f6fa186f77409407a7eb7",   // containers
        K9Ids.Grizzly => "5b47574386f77428ca22b338",                   // medkits
        K9Ids.Bloodset or K9Ids.MedTools => "5b47574386f77428ca22b2f3",   // medical supplies
        K9Ids.Fleece or K9Ids.Ripstop or K9Ids.Paracord => "5b47574386f77428ca22b2f4",   // others (fabrics)
        "5448ff904bdc2d6f028b456e" or "57347da92459774491567cf5" => "5b47574386f77428ca22b336",   // food
        "56742c2e4bdc2d95058b456d" => "5b47574386f77428ca22b2f2",        // flammable materials (the lighter)
        "590c645c86f77412b01304d9" => "5b47574386f77428ca22b341",        // info items
        _ => "5b47574386f77428ca22b2f1"                                  // valuables
    };
}

