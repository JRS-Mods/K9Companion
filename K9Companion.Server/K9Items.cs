using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Cloners;

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

        // The crack house, where Dr. Yang's quest sends you for a vet kit, has spots of its own, each as likely to come up
        // as a typical loose spot on Customs (the middle of the map's own odds): a kit there in about half of raids. It
        // turns up wherever a Grizzly can as well (DogFoodInRaids). The insignia in the Dorms comes up in about seven raids
        // out of ten. (On Customs a spot's weight w comes up with a chance of about 1 - e^(-3.5 w).)
        Place("bigmap", "crackhouse", CrackHouse, .018, (K9Ids.VetKit, 1));
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

/// <summary>
/// The dog's gear. Its jacket is a container (it carries what the old saddlebag did): one of five harnesses (a chest rig
/// and four vests); Dr. Yang sells the vests only ready plated (also on the flea market, through his offers). Goggles go on its head, and a clip-on
/// action camera on its jacket. What the dog is wearing sits in the slots of a hidden "dog" item kept in the profile's
/// quest stash, which the DOG tab shows like the gear screen shows your own slots.
/// </summary>
public static class DogGear
{
    // The black jacket keeps the old saddlebag's id, so saddlebags already in a stash become black harnesses.
    public static readonly MongoId Black = new("6a5d0c0c4fb9a3c5e1d0d061");

    /// <summary>One jacket: a tier of protection with its own look (the client's gear_&lt;look&gt; model and icon_&lt;look&gt;
    /// icon). Armour class 0 = none; the client applies the armour and the speed penalty to the dog (DogArmour there,
    /// same table). Plates fitted in it take a round first where they cover; its own armour is the rest.
    /// Plates: how many armour plates it takes (0, 2 front and back, 4 with the sides), the game's own plates.</summary>
    public sealed record Jacket(MongoId Id, string Look, string Kind, string Short, int ArmourClass, int Width, int Height,
                                int GridWidth, int GridHeight, double Weight, int Price, int Durability = 0, int Speed = 100, int Plates = 0);

    private const int Base = 12000;
    public static readonly Jacket[] Jackets =
    {
        // Durability and speed (%) are the client's DogArmour table; they're repeated here for the item descriptions.
        // Each keeps the id (and so the quests, offers and saved gear) of the jacket it replaced in 0.20.
        // Sized by how big each is: the stash space it takes, and the load it carries, grow with it.
        // Named "K9 <Kind> (<colour>)", short "K9 <Short>": one pattern for all the dog's gear (head gear and camera too).
        new(Black, "harness_chestrig", "Chest Rig", "Rig", 0, 2, 2, 4, 4, 0.8, Base),
        new(new("6a5d0c0c4fb9a3c5e1d0d073"), "harness_pocket", "Pocket Vest", "Pocket", 3, 2, 3, 5, 4, 2.6, Base * 4, 40, 95, 2),
        new(new("6a5d0c0c4fb9a3c5e1d0d071"), "harness_tactical", "Tactical Vest", "Tactical", 4, 3, 3, 5, 5, 4.2, Base * 7, 50, 91, 2),
        // The Last Handler's reward: class 5, light for it, tough, and the biggest of them, with the biggest load.
        new(K9Ids.EliteHarness, "harness_assault", "Assault Vest", "Assault", 5, 3, 4, 6, 6, 3.8, Base * 18, 75, 93, 4),
        // Full length, with its own camera on top (so the clip-on camera isn't needed with it): dearer than a tactical vest
        // and an action camera bought apart.
        new(K9Ids.CameraVest, "harness_camera", "Camera Vest", "CamVest", 4, 3, 4, 6, 5, 4.6, Base * 15, 55, 92, 2),
    };
    private static readonly Dictionary<string, string> LookNames = new()
    { ["harness_chestrig"] = "Black", ["harness_pocket"] = "Black", ["harness_tactical"] = "Olive", ["harness_assault"] = "Camo", ["harness_camera"] = "Coyote" };
    public static string Name(Jacket j) => $"K9 {j.Kind} ({LookNames[j.Look]})";

    /// <summary>
    /// Jackets taken out in 0.20 (the hi-vis basic jacket and the coyote heavy armour). Their items still exist, so a
    /// profile that has one loads, but nothing sells or rewards them: any in a profile become the jacket next to them
    /// (same id, so whatever's inside stays) when the game starts (BagKeeper).
    /// </summary>
    public static readonly Jacket[] Retired =
    {
        new(new("6a5d0c0c4fb9a3c5e1d0d074"), "harness_pocket", "basic protective jacket (old)", "Old jacket", 2, 2, 2, 4, 4, 1.4, Base * 2, 30, 98),
        new(new("6a5d0c0c4fb9a3c5e1d0d072"), "harness_assault", "heavy armour (old)", "Old armour", 5, 3, 4, 6, 5, 6.5, Base * 12, 60, 86),
    };
    public static readonly Dictionary<MongoId, MongoId> ReplacedBy = new()
    {
        [new("6a5d0c0c4fb9a3c5e1d0d074")] = new("6a5d0c0c4fb9a3c5e1d0d073"),
        [new("6a5d0c0c4fb9a3c5e1d0d072")] = K9Ids.EliteHarness,
    };
    public static readonly MongoId Holder = new("6a5d0c0c4fb9a3c5e1d0d080");
    public static readonly MongoId HolderSlotId = new("6a5d0c0c4fb9a3c5e1d0d081"), HeadSlotId = new("6a5d0c0c4fb9a3c5e1d0d082"),
        CameraSlotId = new("6a5d0c0c4fb9a3c5e1d0d083"), CollarSlotId = new("6a5d0c0c4fb9a3c5e1d0d084");
    public const string Slot = "DogJacket", HeadSlot = "DogHead", CameraSlot = "DogCamera", CollarSlot = "DogCollar";

    /// <summary>
    /// The collars, by the style the client draws (its gear_collar_&lt;style&gt; model). Items since 0.31 (before, the DOG
    /// tab picked from the ones the quests had earned): the dog wears the one in its collar slot, into any raid, and
    /// never loses it there. K9Items registers them; BagKeeper hands existing dogs the ones they had earned.
    /// </summary>
    public static readonly Dictionary<MongoId, string> Collars = new()
    {
        [K9Ids.TrainingCollar] = "training", [K9Ids.BlackCollar] = "black", [K9Ids.TacticalCollar] = "buckle",
    };
    public static MongoId CollarFor(string? style)
    {
        foreach (var (id, s) in Collars) if (s == style) return id;
        return K9Ids.TrainingCollar;
    }

    /// <summary>A piece of gear that isn't a jacket: head gear or the clip-on camera (the client's gear_&lt;look&gt; model
    /// and icon_&lt;look&gt; icon). The dog wears it into PMC raids; it's lost if the dog or you die (BagKeeper).</summary>
    public sealed record Kit(MongoId Id, string Look, string Slot, string Name, string Short, int Width, int Height, double Weight,
                             int Price, int Level, string Offer, string Description);
    public static readonly Kit[] Kits =
    {
        new(K9Ids.GogglesTan, "head_goggles_tan", HeadSlot, "K9 Ballistic Goggles (Tan)", "K9 Goggles", 2, 1, 0.2, 38000, 2, K9Ids.Offer(0x31).ToString(),
            "Strap-on ballistic goggles cut for a dog's head: tinted lens over the eyes, strap behind the ears. Put them on your dog's head in the DOG tab. Lost if the dog or you die in a raid."),
        new(K9Ids.GogglesBlack, "head_goggles_black", HeadSlot, "K9 Ballistic Goggles (Black)", "K9 Goggles", 2, 1, 0.2, 42000, 2, K9Ids.Offer(0x32).ToString(),
            "Strap-on ballistic goggles cut for a dog's head: smoked lens over the eyes, strap behind the ears. Put them on your dog's head in the DOG tab. Lost if the dog or you die in a raid."),
        new(K9Ids.ActionCamera, "camera_action", CameraSlot, "K9 Action Camera (Black)", "K9 Cam", 1, 1, 0.15, 60000, 3, K9Ids.Offer(0x34).ToString(),
            "A rugged action camera on a harness mount. It clips onto the back of whatever K9 jacket your dog wears, facing forward (the camera vest has its own), so you can watch what your dog sees in a raid. Fit it in the DOG tab. Lost if the dog or you die in a raid."),
    };
    public static HashSet<MongoId> KitIds => Kits.Select(k => k.Id).ToHashSet();

    /// <summary>
    /// Gear taken out: the camera headset (0.29.1; the dog's camera is the action camera or the camera vest's). Its item
    /// still exists so a profile that has one loads, but nothing sells it or takes it, and any in a profile becomes an
    /// action camera when the game starts (BagKeeper).
    /// </summary>
    public static readonly Kit[] RetiredKits =
    {
        new(K9Ids.CameraHeadset, "head_headset", HeadSlot, "K9 Camera Headset (old)", "K9 Headset", 2, 2, 0.45, 60000, 4, K9Ids.Offer(0x33).ToString(),
            "No longer made: it becomes a K9 Action Camera."),
    };

    public static bool IsJacket(MongoId template) => Jackets.Any(j => j.Id == template);
    // Jackets with a camera of their own: no slot for the clip-on one.
    public static readonly HashSet<string> BuiltInCamera = new() { "harness_camera" };
    public static HashSet<MongoId> JacketIds => Jackets.Select(j => j.Id).ToHashSet();
}

/// <summary>Registers the jackets and the hidden holder item.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class GearItems(CustomItemService items, TemplateTable templates, LocaleTable locales, ICloner cloner, ISptLogger<GearItems> logger) : IOnLoad
{
    // The game's Item case: a plain container that is allowed to take items during a raid. Its one grid ("main") is the
    // jackets' load (they were item cases before 0.25, so what's inside them already sits in "main").
    private static readonly MongoId ItemCase = new("59fb042886f7746c5005a7b2");
    // The jackets are armoured rigs like the game's own: copies of the Tasmanian Tiger SK plate carrier (front and back
    // plate slots, nothing else to fill), the sides' slots from the ANA Tactical M2 for the vest that takes four.
    private static readonly MongoId Carrier = new("628cd624459354321c4b7fa2"), SideCarrier = new("5ab8dced86f774646209ec87");
    private const string ArmouredRigsHandbook = "5b5f6f8786f77447ed563642";
    private const string ContainersHandbook = "5b5f6fa186f77409407a7eb7", ElectronicsHandbook = "5b47574386f77428ca22b2ef";
    private static readonly MongoId FlashStorage = new("5c05300686f7746dce784e5d");   // VPX flash storage module

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        MongoId parent = templates.Items[ItemCase].Parent;
        MongoId rigParent = templates.Items[Carrier].Parent;
        int index = 0;
        foreach (var j in DogGear.Jackets.Concat(DogGear.Retired))
        {
            index++;
            bool retired = DogGear.Retired.Contains(j);
            bool camera = !retired && !DogGear.BuiltInCamera.Contains(j.Look);
            string plates = j.Plates == 0 ? "" : $" Takes {(j.Plates == 4 ? "front, back and side" : "front and back")} armour plates (any the game has; fit them in its inspect window or drag them onto it): a plate stops a round where it covers, before the vest's own armour.";
            string clip = camera ? " A K9 action camera clips onto it." : "";
            string repair = j.ArmourClass <= 3 ? "aramid, Cordura or ripstop fabric, or KEKTAPE" : "aramid fiber or Cordura fabric";
            string protection = j.ArmourClass == 0 ? "No armour: light and quick."
                : $"Armour class {j.ArmourClass} over the chest and back, durability {j.Durability}; the dog moves at {j.Speed}% speed in it. " +
                  $"Wear carries over between raids; repair it at a kennel with a Working Dog Station using {repair}.";
            if (!Clone(j.Id, rigParent, "k9companion_jacket_" + (retired ? "old_" + j.Id.ToString()[^3..] : j.Look), j.Price, new LocaleDetails
            {
                Name = DogGear.Name(j),
                ShortName = "K9 " + j.Short,
                Description = $"{protection}{plates}{clip} Carries a {j.GridWidth}x{j.GridHeight} load. Put it on your dog in the DOG tab and fill it like any container; " +
                              "it goes into PMC raids with the dog (look at the dog and press your interact key to open it) and comes home with you when you both make it out."
            }, Carrier, ArmouredRigsHandbook)) continue;
            var props = templates.Items[j.Id].Properties!;
            props.Width = j.Width; props.Height = j.Height; props.Weight = j.Weight;
            // Insurable like the game's own rigs: lost with you or the dog, the insurer may find it (BagKeeper.Insure).
            // Never listed on the flea market (the flea's made-up sellers would offer them bare): it shows only Dr. Yang's
            // plated offers, which the flea takes from his shelves regardless (ragfair.json traderItems is false).
            props.CanSellOnRagfair = false; props.InsuranceDisabled = retired;
            // One load grid, the item case's "main" (the carrier's own pouches would leave what's inside without a place).
            var main = cloner.Clone(templates.Items[ItemCase].Properties!.Grids!.First())!;
            main.Parent = j.Id;
            props.Grids = new List<Grid> { main };
            props.Slots = Slots(j, index, camera);
            // A plain grid: the carrier's pouch layout drawn over the one load grid jumbles it.
            props.RigLayoutName = "";
            // Its own armour, as the inspect window shows it (class, durability, chest and back): the client's DogArmour
            // tier is what actually stops rounds, and the durability shown follows the dog's wear (DogArmour.ShowWear).
            if (j.ArmourClass > 0)
            {
                props.ArmorClass = j.ArmourClass;
                props.Durability = props.MaxDurability = j.Durability;
                props.ArmorMaterial = j.ArmourClass <= 3 ? ArmorMaterial.Aramid : ArmorMaterial.UHMWPE;
                props.ArmorColliders = new List<string> { "RibcageUp", "RibcageLow", "SpineTop", "SpineDown" };
                // The rest of what the game reads on any armour (the carrier copied has none of it; a null crashes the
                // handbook while the profile loads). Values as the game's own soft armour.
                props.ArmorPlateColliders = new List<string>();
                props.Indestructibility = .9;
                props.DeafStrength = "None";
                props.BlindnessProtection = 0;
                props.MaterialType = "BodyArmor";
            }
            var grid = props.Grids?.FirstOrDefault()?.Properties;
            if (grid != null)
            {
                // Never smaller than the old 4x4 saddlebag, so nothing already inside ends up outside the grid.
                grid.CellsH = j.GridWidth; grid.CellsV = j.GridHeight;
                // No jacket inside a jacket.
                var filter = grid.Filters?.FirstOrDefault();
                if (filter != null) filter.ExcludedFilter = (filter.ExcludedFilter ?? new HashSet<MongoId>()).Union(DogGear.JacketIds.Append(DogGear.Holder)).ToHashSet();
            }
        }

        // Head gear and the camera: small items (copies of a flash storage module, which only shows if one is dropped on
        // the ground; the stash shows the client's own picture of the gear).
        foreach (var k in DogGear.Kits.Concat(DogGear.RetiredKits))
        {
            if (!Clone(k.Id, templates.Items[FlashStorage].Parent, "k9companion_" + k.Look, k.Price, new LocaleDetails
            { Name = k.Name, ShortName = k.Short, Description = k.Description }, FlashStorage, ElectronicsHandbook)) continue;
            var props = templates.Items[k.Id].Properties!;
            bool retired = DogGear.RetiredKits.Contains(k);
            props.Width = k.Width; props.Height = k.Height; props.Weight = k.Weight;
            props.CanSellOnRagfair = !retired; props.InsuranceDisabled = retired;
        }

        if (Clone(DogGear.Holder, parent, "k9companion_dog_gear", 1, new LocaleDetails
        {
            Name = "K9 dog", ShortName = "Dog", Description = "What your dog is wearing."
        }))
        {
            var props = templates.Items[DogGear.Holder].Properties!;
            props.Width = 1; props.Height = 1; props.Weight = 0;
            props.CanSellOnRagfair = false; props.InsuranceDisabled = true;
            props.Grids = new List<Grid>();
            props.Slots = new List<Slot>
            {
                new()
                {
                    Name = DogGear.Slot, Id = DogGear.HolderSlotId, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e",
                    Required = false, MergeSlotWithChildren = false,
                    Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.JacketIds } } }
                },
                KitSlot(DogGear.HeadSlot, DogGear.HeadSlotId),
                KitSlot(DogGear.CameraSlot, DogGear.CameraSlotId),
                new()
                {
                    Name = DogGear.CollarSlot, Id = DogGear.CollarSlotId, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e",
                    Required = false, MergeSlotWithChildren = false,
                    Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.Collars.Keys.ToHashSet() } } }
                },
            };
        }

        // The slot's heading in the DOG tab.
        foreach (var language in locales.Global.Values)
            language.AddTransformer(data =>
            {
                if (data != null) { data[DogGear.Slot] = "Jacket"; data[DogGear.HeadSlot] = "Head"; data[DogGear.CameraSlot] = "Camera"; data[DogGear.CollarSlot] = "Collar"; }
                return data;
            });
        logger.Info($"[K9Companion] {DogGear.Jackets.Length} dog jackets and {DogGear.Kits.Length} pieces of head and camera gear registered.");
        return Task.CompletedTask;
    }

    // A jacket's slots: its plates (copies of the carriers' own, so they take what those take) and the clip-on camera.
    private List<Slot> Slots(DogGear.Jacket j, int index, bool camera)
    {
        var slots = new List<Slot>();
        var names = new List<(MongoId from, string name)>();
        if (j.Plates >= 2) { names.Add((Carrier, "Front_plate")); names.Add((Carrier, "Back_plate")); }
        if (j.Plates >= 4) { names.Add((SideCarrier, "Left_side_plate")); names.Add((SideCarrier, "Right_side_plate")); }
        int n = 0;
        foreach (var (from, name) in names)
        {
            Slot? source = templates.Items[from].Properties?.Slots?.FirstOrDefault(s => s.Name == name);
            if (source == null) { logger.Warning($"[K9Companion] The game's {name} slot wasn't found; {DogGear.Name(j)} goes without it."); continue; }
            Slot copy = cloner.Clone(source)!;
            copy.Id = new MongoId(K9Ids.Make(0xa, index, ++n)); copy.Parent = j.Id; copy.Required = false;
            // No plate comes with it.
            foreach (var f in copy.Properties?.Filters ?? new List<SlotFilter>()) f.Plate = null;
            slots.Add(copy);
        }
        if (camera)
            slots.Add(new Slot
            {
                Name = DogGear.CameraSlot, Id = new MongoId(K9Ids.Make(0xa, index, 0xc)), Parent = j.Id, Prototype = "55d30c4c4bdc2db4468b457e",
                Required = false, MergeSlotWithChildren = false,
                Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = new HashSet<MongoId> { K9Ids.ActionCamera } } } }
            });
        return slots;
    }

    // One of the dog's slots for a kind of kit (head gear, the camera).
    private static Slot KitSlot(string name, MongoId id) => new()
    {
        Name = name, Id = id, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e", Required = false, MergeSlotWithChildren = false,
        Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.Kits.Where(k => k.Slot == name).Select(k => k.Id).ToHashSet() } } }
    };

    private bool Clone(MongoId id, MongoId parent, string name, int price, LocaleDetails text, MongoId? from = null, string handbook = ContainersHandbook)
    {
        var result = items.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = from ?? ItemCase, NewId = id, ParentId = parent, NewItemName = name,
            HandbookParentId = handbook, HandbookPriceRoubles = price, FleaPriceRoubles = price,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = text }
        });
        if (!result.Success) logger.Error($"[K9Companion] Couldn't create {name}: " + string.Join("; ", result.Errors ?? new List<string>()));
        return result.Success == true;
    }
}

/// <summary>
/// Dog food and the vet kit in raids, by the game's own loot tables rather than numbers of ours. Dr. Yang's quests take
/// them found in raid only, so each joins every container and loose spot that can hold one of its vanilla look-alikes:
/// <list type="bullet">
/// <item>the can of dog food wherever a can of food can be (beef stew, peas, squash, fish), the pack of dog meat wherever
/// dried meat or sausage can, at nearly twice the average of their odds there (so as common as the commonest food, Iskra
/// rations and sugar: about two of each a raid on Customs, more on Woods and Interchange), and in scavs' pockets, vests
/// and bags (BotQuestItemsPatch marks those found in raid, as scav loot isn't otherwise);</item>
/// <item>the vet kit, a copy of the Grizzly, wherever a Grizzly can be, as often, on every map (scavs don't carry it).</item>
/// </list>
/// A person can eat the food too, and it does them little good: hardly any energy, and it leaves them thirsty.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public sealed class DogFoodInRaids(LocationTable locations, BotTable bots, TemplateTable templates, ISptLogger<DogFoodInRaids> logger) : IOnLoad
{
    private static readonly MongoId[] Cans =
    {
        new("57347d7224597744596b4e72"), new("57347da92459774491567cf5"),   // beef stew, small and large
        new("57347d692459774491567cf1"), new("57347d8724597744596b4e76"),   // green peas, squash spread
        new("57347d9c245977448b40fa85"), new("57347d5f245977448b40fa81"),   // herring, humpback salmon
        new("5673de654bdc2d180f8b456d"), new("5bc9c29cd4351e003562b8a3"),   // pacific saury, sprats
    };

    private static readonly MongoId[] Meat =
    {
        new("65815f0e647e3d7246384e14"), new("635a758bfefc88a93f021b8a"),   // Tarker dried meat, Salty Dog beef sausage
    };

    // Ours, what it goes in with, how its odds compare with theirs, and whether scavs carry it.
    private static readonly (MongoId Ours, MongoId[] Like, double Scale, bool OnScavs)[] Families =
    {
        (DogFoodItems.DogFood, Cans, 1.8, true),
        (DogFoodItems.DogMeat, Meat, 1.8, true),
        (K9Ids.VetKit, new[] { new MongoId(K9Ids.Grizzly) }, 1.0, false),
    };

    public static bool IsDogFood(MongoId tpl) => tpl == DogFoodItems.DogFood || tpl == DogFoodItems.DogMeat;

    private static readonly string[] Scavs = { "assault", "cursedassault", "marksman" };

    // What eating it does to a person (energy, hydration): the beef it copies gives 45-55 energy for 5 water.
    private static readonly (MongoId Item, double Energy, double Hydration)[] ForPeople =
    {
        (DogFoodItems.DogFood, 15, -20), (DogFoodItems.DogMeat, 10, -25), (K9Ids.PremiumFood, 20, -20), (K9Ids.Treats, 5, -10),
    };

    // Its odds where it goes in with any of its look-alikes: the average of theirs there, scaled. Null if none are there.
    private static double? OddsAmong(IEnumerable<(MongoId Tpl, double Odds)> there, MongoId[] like, double scale)
    {
        var theirs = there.Where(t => like.Contains(t.Tpl)).Select(t => t.Odds).ToList();
        return theirs.Count == 0 ? null : theirs.Average() * scale;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (var (item, energy, hydration) in ForPeople)
        {
            if (templates.Items.GetValueOrDefault(item)?.Properties is not { } props) continue;
            props.EffectsHealth = new Dictionary<HealthFactor, EffectsHealthProperties>
            {
                [HealthFactor.Energy] = new() { Value = energy }, [HealthFactor.Hydration] = new() { Value = hydration },
            };
        }

        int maps = 0;
        foreach (Location map in locations.GetDictionary().Values)
        {
            if (map?.StaticLoot == null && map?.LooseLoot == null) continue;
            map.StaticLoot?.AddTransformer(InContainers);
            map.LooseLoot?.AddTransformer(LyingLoose);
            maps++;
        }

        int pools = 0;
        foreach (string role in Scavs)
        {
            if (!bots.Types.TryGetValue(role, out BotType? type) || type?.BotInventory?.Items is not { } carried) continue;
            foreach (var pool in new[] { carried.Backpack, carried.Pockets, carried.TacticalVest })
            {
                if (pool == null) continue;
                foreach (var (ours, like, scale, onScavs) in Families)
                {
                    if (!onScavs || pool.ContainsKey(ours)) continue;
                    double? odds = OddsAmong(pool.Select(p => (p.Key, p.Value)), like, scale);
                    if (odds.HasValue && pool.TryAdd(ours, odds.Value)) pools++;
                }
            }
        }
        logger.Info($"[K9Companion] Dog food and the vet kit turn up in raids on {maps} maps as the game's own food and medkits do, and dog food in {pools} scav loot pools.");
        return Task.CompletedTask;
    }

    private static Dictionary<MongoId, StaticLootDetails>? InContainers(Dictionary<MongoId, StaticLootDetails>? loot)
    {
        if (loot == null) return loot;
        foreach (StaticLootDetails details in loot.Values)
        {
            if (details.ItemDistribution == null) continue;
            var list = details.ItemDistribution.ToList();
            bool added = false;
            foreach (var (ours, like, scale, _) in Families)
            {
                if (list.Any(d => d.Tpl == ours)) continue;
                double? odds = OddsAmong(list.Select(d => (d.Tpl, (double)(d.RelativeProbability ?? 0f))), like, scale);
                if (!odds.HasValue) continue;
                list.Add(new ItemDistribution { Tpl = ours, RelativeProbability = (float)odds.Value });
                added = true;
            }
            if (added) details.ItemDistribution = list;
        }
        return loot;
    }

    // A loose spot lists the items it can be (each under a key) and the odds of each: ours joins every spot that can be one
    // of its look-alikes.
    private static LooseLoot? LyingLoose(LooseLoot? loot)
    {
        if (loot?.Spawnpoints == null) return loot;
        foreach (Spawnpoint point in loot.Spawnpoints)
        {
            if (point.Template?.Items == null || point.ItemDistribution == null) continue;
            var items = point.Template.Items.ToList();
            var odds = point.ItemDistribution.ToList();
            // What each key in this spot is: its item with no parent.
            var keyed = new Dictionary<string, MongoId>();
            foreach (var i in items)
                if (string.IsNullOrEmpty(i.ParentId) && i.ComposedKey != null) keyed.TryAdd(i.ComposedKey, i.Template);
            var there = new List<(MongoId, double)>();
            foreach (var d in odds)
                if (d.ComposedKey?.Key is { } k && keyed.TryGetValue(k, out MongoId tpl)) there.Add((tpl, (double)(d.RelativeProbability ?? 0)));
            bool added = false;
            foreach (var (ours, like, scale, _) in Families)
            {
                string key = ours.ToString();
                if (items.Any(i => i.ComposedKey == key)) continue;
                double? chance = OddsAmong(there, like, scale);
                if (!chance.HasValue) continue;
                items.Add(new SptLootItem { Id = new MongoId(), Template = ours, ComposedKey = key, Upd = new Upd { StackObjectsCount = 1 } });
                odds.Add(new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = key }, RelativeProbability = chance.Value });
                added = true;
            }
            if (!added) continue;
            point.Template.Items = items;
            point.ItemDistribution = odds;
        }
        return loot;
    }
}

/// <summary>
/// Every id the K9 expansion adds. All are 24 hex digits under the mod's own prefix; the 20th digit says what kind of
/// thing it is (1 quest, 2 condition, 3 reward, 4 reward item, 5 trader offer, 6 item template, 7 hideout improvement,
/// 8 counter part), so nothing here can collide with the 0.15 ids (which have a 0 there) or with each other.
/// </summary>
public static class K9Ids
{
    private const string Prefix = "6a5d0c0c4fb9a3c5e1d";

    public static string Make(int kind, int a, int b) => $"{Prefix}{kind:x1}{a:x2}{b:x2}";
    public static MongoId Quest(int n) => new(Make(1, 0, n));
    public static string Condition(int quest, int index) => Make(2, quest, index);
    public static string Reward(int quest, int index) => Make(3, quest, index);
    public static string RewardItem(int quest, int index) => Make(4, quest, index);
    public static string CounterPart(int quest, int index) => Make(8, quest, index);
    public static MongoId Offer(int n) => new(Make(5, 0, n));
    public static MongoId Template(int n) => new(Make(6, 0, n));

    // ---- the questline (1-10) and the contracts (0x11-0x15) ----------------------------------------------------
    public const int ManBestFriend = 1, StraysOfTarkov = 2, FamiliarScent = 3, MissingUnit = 4, TrustIsEarned = 5,
        LearningTheRopes = 6, WorkingDog = 7, TeethAndSteel = 8, NoDogLeftBehind = 9, LastHandler = 10;
    public const int DailyRations = 0x11, SafeReturn = 0x12, SupplyRun = 0x13, WorkingPartnership = 0x14, ThePack = 0x15;
    // The story, five quests since 0.30: Strays of Tarkov and A Familiar Scent became part of The Missing Unit,
    // Learning the Ropes part of Teeth and Steel, No Dog Left Behind part of The Last Handler, and Working Dog (the
    // dog's loot search) went. Their numbers aren't reused (profiles may still name them).
    public static readonly int[] Story = { ManBestFriend, MissingUnit, TrustIsEarned, TeethAndSteel, LastHandler };
    public static readonly int[] RetiredStory = { StraysOfTarkov, FamiliarScent, LearningTheRopes, WorkingDog, NoDogLeftBehind };
    public static readonly int[] Contracts = { DailyRations, SafeReturn, SupplyRun, WorkingPartnership, ThePack };

    // ---- new item templates -----------------------------------------------------------------------------------
    public static readonly MongoId K9Tag = Template(0x01), HandlerJacket = Template(0x02), DogWhistle = Template(0x03),
        TransportRecords = Template(0x04), VetSupplyCase = Template(0x05), K9Insignia = Template(0x06),
        VetKit = Template(0x10), EliteHarness = Template(0x20), ArmourLocker = Template(0x30), PremiumLocker = Template(0x31);
    // The camera vest, head gear and the clip-on camera (DogGear.Kits).
    public static readonly MongoId CameraVest = Template(0x21), GogglesTan = Template(0x22), GogglesBlack = Template(0x23),
        CameraHeadset = Template(0x24), ActionCamera = Template(0x25);
    // Dog supplies sold by loyalty level (see DrYang).
    public static readonly MongoId Treats = Template(0x11), PremiumFood = Template(0x12), BedLiner = Template(0x13), ThermalBed = Template(0x14),
        TrainingKit = Template(0x15), TacticalCollar = Template(0x16);
    // The other two collars (the tactical collar is the third): the dog wears one in its collar slot (DogGear.Collars).
    public static readonly MongoId TrainingCollar = Template(0x17), BlackCollar = Template(0x18);

    // ---- the kennel: three improvements of the hideout's Rest Space ---------------------------------------------
    public static readonly MongoId[] Kennel = { new(Make(7, 0, 1)), new(Make(7, 0, 2)), new(Make(7, 0, 3)) };

    // ---- vanilla templates --------------------------------------------------------------------------------------
    public static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    public const string Water06 = "5448fee04bdc2dbc018b4567", Aquamari = "5c0fa877d174af02a012e1cf", WaterRation = "60098b1705871270cd5352a1";
    public const string Ai2 = "5755356824597772cb798962", CarKit = "590c661e86f7741e566b646a", Salewa = "544fb45d4bdc2dee738b4568",
        Ifak = "590c678286f77426c9660122", Afak = "60098ad7c2240c0fe85c570a", Grizzly = "590c657e86f77412b013051d";
    public const string Bloodset = "5b4335ba86f7744d2837a264", Saline = "59e3606886f77417674759a5", MedTools = "619cc01e0a7c3a1a2731940c",
        Syringe = "5d1b3f2d86f774253763b735", Ophthalmoscope = "5af0534a86f7743b6f354284";
    public const string CorrugatedHose = "59e35cbb86f7741778269d83", Bolts = "57347c5b245977448d35f6e1", ScrewNuts = "57347c77245977448d35f6e2",
        Toolset = "590c2e1186f77425357b6124", MetalParts = "61bf7b6302b3924be92fa8c3", MilitaryCable = "5d0375ff86f774186372f685",
        Ledx = "5c0530ee86f774697952d952";
    public const string Fleece = "5e2af47786f7746d404f3aaa", Ripstop = "5e2af4a786f7746d3f3c3400", Paracord = "5c12688486f77426843c7d32",
        Aramid = "5e2af4d286f7746d4159f07a", Cordura = "5e2af41e86f774755a234b67", Kektape = "5e2af29386f7746d4159f077";

    // ---- maps ---------------------------------------------------------------------------------------------------
    public const string Customs = "56f40101d2720b2a4d8b45d6", Woods = "5704e3c2d2720bac5b8b4567",
        Reserve = "5704e5fad2720bc05b8b4567", Streets = "5714dc692459777137212e12", AnyMap = "any";
}
