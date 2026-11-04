using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Cloners;

namespace K9Companion.Server;

/// <summary>
/// Dr. Yang, the clinic vet: a new trader (own id, base, assort, portrait and text) that sells everything the dog needs
/// and buys food and drink back. Vanilla traders, assortments and templates are left untouched; the base starts as a
/// copy of Jaeger's, so every field the game expects is present.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 3)]
public sealed class DrYang(TradersTable traders, LocaleTable locales, TraderConfig traderConfig, RagfairConfig ragfairConfig,
    ImageRouter images, ModHelper mods, ICloner cloner, ISptLogger<DrYang> logger) : IOnLoad
{
    public static readonly MongoId Id = new("6a5d0c0c4fb9a3c5e1d0d0a0");
    private static readonly MongoId Jaeger = new("5c0647fdd443bc2504c2d371");
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private const int RestockSeconds = 3600;

    // Offer id, item, price (roubles), stock per restock, loyalty level, by his four loyalty levels:
    //  LL1 dog food, water, treats (care items: K9Shop)       LL2 dog beds, harnesses, training kit (vet basics: K9Shop)
    //  LL3 vet kits, repair materials (armour 2-3 and Chinese weapons: K9Shop)
    //  LL4 premium food, rare accessories (armour 4-5, elite harness, advanced vet gear: K9Shop / jackets)
    // Goggles LL2, the action camera and the camera vest LL3 (DogGear).
    // Some are also tied to a quest in K9Quests. The original food and water offers keep their ids and level.
    private static readonly (string Offer, MongoId Item, int Price, int Stock, int Level)[] Stock =
    {
        ("6a5d0c0c4fb9a3c5e1d0d0b1", DogFoodItems.DogFood, DogFoodItems.DogFoodPrice, 30, 1),
        ("6a5d0c0c4fb9a3c5e1d0d0b2", DogFoodItems.DogMeat, DogFoodItems.DogMeatPrice, 40, 1),
        ("6a5d0c0c4fb9a3c5e1d0d0b3", new MongoId("5448fee04bdc2dbc018b4567"), 3200, 30, 1),    // Bottle of water (0.6L)
        ("6a5d0c0c4fb9a3c5e1d0d0b4", new MongoId("60098b1705871270cd5352a1"), 18000, 10, 1),   // Emergency Water Ration
        ("6a5d0c0c4fb9a3c5e1d0d0b5", new MongoId("5d1b33a686f7742523398398"), 55000, 3, 1),    // Canister with purified water
        (K9Ids.Offer(1).ToString(), DogFoodItems.DogFood, 3400, 60, 1),                       // Strays of Tarkov: bulk dog food
        (K9Ids.Offer(2).ToString(), DogFoodItems.DogMeat, 2100, 60, 1),                       // Strays of Tarkov: bulk dog meat
        (K9Ids.Offer(9).ToString(), K9Ids.Treats, 3500, 40, 1),
        (K9Ids.Offer(3).ToString(), new MongoId(K9Ids.Fleece), 9000, 4, 1),                   // Learning the Ropes: bed lining
        (K9Ids.Offer(12).ToString(), K9Ids.BedLiner, 12000, 3, 2),
        (K9Ids.Offer(13).ToString(), K9Ids.ThermalBed, 45000, 2, 2),
        (K9Ids.Offer(14).ToString(), K9Ids.TrainingKit, 30000, 3, 2),
        (K9Ids.Offer(4).ToString(), new MongoId(K9Ids.Ripstop), 24000, 3, 2),
        (K9Ids.Offer(5).ToString(), K9Ids.VetKit, K9Items.VetKitPrice, 5, 3),
        (K9Ids.Offer(7).ToString(), new MongoId(K9Ids.Cordura), 19000, 3, 3),                 // Working Dog: jacket repairs
        (K9Ids.Offer(8).ToString(), new MongoId(K9Ids.Aramid), 9500, 4, 3),                   // Teeth and Steel: armour repairs
        (K9Ids.Offer(26).ToString(), K9Ids.PremiumFood, 9000, 20, 4),
        // The collars (DogGear.Collars): the coyote one at LL1, the black at LL2, the tactical at LL4.
        (K9Ids.Offer(0x35).ToString(), K9Ids.TrainingCollar, 6500, 3, 1),
        (K9Ids.Offer(0x36).ToString(), K9Ids.BlackCollar, 22000, 2, 2),
        (K9Ids.Offer(29).ToString(), K9Ids.TacticalCollar, 85000, 1, 4),
    };

    // The dog's vests are sold only ready plated (no bare vests, and not the chest rig, which takes no plates), dearer the better the plates and higher up his loyalty levels: the
    // vest's price plus the plates' (the game's handbook prices) with his 15% on top. Plate, (side plate or null), level.
    private record Plated(string Look, string Plate, int PlatePrice, string? Side, int SidePrice, int Level, int Stock);
    private static readonly Plated[] PlatedVests =
    {
        new("harness_pocket", "656f9d5900d62bcd2e02407c", 12580, null, 0, 2, 2),                                    // AR500 LP, class 3
        new("harness_pocket", "656fa0fb498d1b7e3e071d9c", 16079, null, 0, 3, 2),                                    // GA Steel, class 4
        new("harness_tactical", "656fa25e94b480b8a500c0e0", 21620, null, 0, 3, 2),                                  // KA Titan, class 4
        new("harness_tactical", "655746010177119f4a097ff7", 37084, null, 0, 4, 1),                                  // SAPI III+, class 5
        new("harness_camera", "656f9fa0498d1b7e3e071d98", 18241, null, 0, 3, 1),                                    // SPRTN Omega, class 4
        new("harness_assault", "655746010177119f4a097ff7", 37084, "6557458f83942d705f0c4962", 8804, 4, 1),          // SAPI III+ and sides, class 5
        new("harness_assault", "64afdcb83efdfea28601d041", 72426, "64afdb577bb3bfe8fe03fd1d", 16120, 4, 1),         // ESAPI and ESBI sides, class 6
    };
    /// <summary>The plated vests' offers (they wait for the dog, like the bare ones).</summary>
    public static IEnumerable<MongoId> PlatedOffers => PlatedVests.Select((_, i) => K9Ids.Offer(0x90 + i));

    private static IEnumerable<(string Offer, MongoId Item, int Price, int Stock, int Level)> Offers()
        => Stock.Concat(DogGear.Kits.Select(k => (k.Offer, k.Id, k.Price, 2, k.Level)));

    // Four loyalty levels. The questline's standing rewards reach LL2 by Trust Is Earned, LL3 by Teeth and Steel and
    // LL4 with The Last Handler; buying his gear counts towards the spending.
    private static readonly (int Level, long Spent, double Standing)[] Loyalty = { (1, 0, 0), (10, 150_000, .15), (20, 400_000, .30), (30, 800_000, .50) };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (traders.ContainsKey(Id)) { logger.Warning("[K9Companion] A trader with Dr. Yang's id already exists; not adding another."); return Task.CompletedTask; }
        Trader? template = traders.GetTrader(Jaeger);
        if (template?.Base == null) { logger.Error("[K9Companion] Jaeger's base wasn't found to copy; Dr. Yang not added."); return Task.CompletedTask; }

        TraderBase trader = cloner.Clone(template.Base)!;
        trader.Id = Id;
        trader.Name = "Dr. Yang"; trader.Surname = "Yang"; trader.Nickname = "Dr. Yang";
        trader.Location = "Tarkov veterinary clinic";
        trader.Avatar = $"{AvatarRoute()}.jpg";
        trader.UnlockedByDefault = true;
        trader.Currency = CurrencyType.RUB;
        trader.BalanceRub = 5_000_000; trader.BalanceDollar = 0; trader.BalanceEuro = 0;
        trader.Medic = false; trader.CustomizationSeller = false; trader.AvailableInRaid = false;
        trader.Discount = 0; trader.DiscountEnd = 0;
        trader.Insurance!.Availability = false;
        trader.Repair!.Availability = false;
        // Buys food and drink (including the dog's) and the dog's gear (jackets, head gear, camera, collars) back from you.
        trader.ItemsBuy = new ItemBuyData
        {
            Category = new HashSet<MongoId> { new("5448e8d04bdc2ddf718b4569"), new("5448e8d64bdc2dce718b4568") },
            IdList = DogGear.JacketIds.Union(DogGear.KitIds).Union(DogGear.Collars.Keys).ToHashSet()
        };
        trader.ItemsBuyProhibited = new ItemBuyData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() };
        // One entry per loyalty level, as the game's traders have (with only level 1, the trade screen found nothing for
        // higher levels and threw while drawing your stash).
        trader.ItemsSell = Enumerable.Range(1, 4).ToDictionary(l => l.ToString(), _ => new ItemSellData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() });
        trader.LoyaltyLevels = Loyalty.Select(l => new TraderLoyaltyLevel
        {
            MinLevel = l.Level, MinSalesSum = l.Spent, MinStanding = l.Standing, BuyPriceCoefficient = 50, ExchangePriceCoefficient = 0,
            HealPriceCoefficient = 0, InsurancePriceCoefficient = 0, RepairPriceCoefficient = 0
        }).ToList();

        var assort = new TraderAssort
        {
            NextResupply = 0,
            Items = new List<Item>(),
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
            LoyalLevelItems = new Dictionary<MongoId, int>()
        };
        foreach (var (offer, item, price, stock, level) in Offers())
        {
            var offerId = new MongoId(offer);
            assort.Items.Add(new Item
            {
                Id = offerId, Template = item, ParentId = "hideout", SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = stock }
            });
            assort.BarterScheme[offerId] = [[new BarterScheme { Count = price, Template = Roubles }]];
            assort.LoyalLevelItems[offerId] = level;
        }

        // The plated vests: the vest with its plates in their slots, as one offer.
        for (int i = 0; i < PlatedVests.Length; i++)
        {
            Plated p = PlatedVests[i];
            DogGear.Jacket? jacket = DogGear.Jackets.FirstOrDefault(j => j.Look == p.Look);
            if (jacket == null) continue;
            MongoId offerId = K9Ids.Offer(0x90 + i);
            assort.Items.Add(new Item { Id = offerId, Template = jacket.Id, ParentId = "hideout", SlotId = "hideout", Upd = new Upd { UnlimitedCount = false, StackObjectsCount = p.Stock } });
            var slots = new List<(string Slot, string Plate)> { ("Front_plate", p.Plate), ("Back_plate", p.Plate) };
            if (p.Side != null) { slots.Add(("Left_side_plate", p.Side)); slots.Add(("Right_side_plate", p.Side)); }
            for (int n = 0; n < slots.Count; n++)
                assort.Items.Add(new Item { Id = new MongoId(K9Ids.Make(5, 1, i * 4 + n)), Template = new MongoId(slots[n].Plate), ParentId = offerId.ToString(), SlotId = slots[n].Slot });
            int price = (int)Math.Round((jacket.Price + (p.PlatePrice * 2 + p.SidePrice * (p.Side != null ? 2 : 0)) * 1.15) / 100) * 100;
            assort.BarterScheme[offerId] = [[new BarterScheme { Count = price, Template = Roubles }]];
            assort.LoyalLevelItems[offerId] = p.Level;
        }

        traders[Id] = new Trader
        {
            Base = trader,
            Assort = assort,
            QuestAssort = new Dictionary<string, Dictionary<MongoId, MongoId>>
            {
                ["started"] = new(), ["success"] = new(), ["fail"] = new()
            },
            Dialogue = new Dictionary<string, List<string>?>()
        };

        // Restocks every hour, like a vanilla trader; offers also appear on the flea market.
        traderConfig.UpdateTime.Add(new UpdateTime { Name = "drYang", TraderId = Id, Seconds = new MinMax<int>(RestockSeconds, RestockSeconds) });
        ragfairConfig.Traders[Id] = true;

        if (File.Exists(Portrait)) images.AddRoute(AvatarRoute(), Portrait);
        else logger.Warning("[K9Companion] Dr. Yang's portrait is missing: " + Portrait);

        AddText();
        logger.Info($"[K9Companion] Dr. Yang is open for business ({Offers().Count()} offers, restock every {RestockSeconds / 60} min).");
        return Task.CompletedTask;
    }

    private string Portrait => System.IO.Path.Combine(mods.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "res", "dryang.jpg");

    // The game keeps trader pictures by address, so the address carries a fingerprint of the picture: a new picture
    // gets a new address and is downloaded instead of the old copy being shown.
    private string AvatarRoute()
    {
        string stamp = "0";
        if (File.Exists(Portrait))
            using (var sha = System.Security.Cryptography.SHA1.Create())
                stamp = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(Portrait)))[..8].ToLowerInvariant();
        return $"/files/trader/avatar/{Id}_{stamp}";
    }

    private void AddText()
    {
        var text = new Dictionary<string, string>
        {
            [$"{Id} FullName"] = "Dr. Wei Yang",
            [$"{Id} FirstName"] = "Wei",
            [$"{Id} Nickname"] = "Dr. Yang",
            [$"{Id} Location"] = "Tarkov veterinary clinic",
            [$"{Id} Description"] = "Before the conflict Dr. Yang ran the only veterinary clinic in Tarkov. He stayed for the animals that were left behind, and now keeps the city's working dogs fed, watered and patched up - for a fair price."
        };
        foreach (var language in locales.Global.Values)
            language.AddTransformer(data =>
            {
                if (data != null) foreach (var (key, value) in text) data[key] = value;
                return data;
            });
    }
}

/// <summary>
/// The rest of Dr. Yang's shelves: the game's own care items and Asian-styled gear, and, with the WTT Content Backport
/// installed, its Asian rifles (the QBZ-191 and the Howa Type 20) with their rounds and magazines. The rifles are copies
/// of the builds WTT's own traders sell. Everything is priced from the game's handbook, as vanilla traders are.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 5)]
public sealed class K9Shop(TradersTable traders, TemplateTable templates, GlobalTable globals, ICloner cloner, ISptLogger<K9Shop> logger) : IOnLoad
{
    // The WTT Content Backport's Asian rifles: template, stock, loyalty level, offer number.
    private static readonly (string Template, int Stock, int Level, int Offer)[] WttWeapons =
    {
        ("69f9ebbcaae020b0db02f65d", 2, 3, 0x40),   // Norinco QBZ-191 5.8x42 assault rifle (China)
        ("6a3bffbebc377285900b85cf", 2, 4, 0x41),   // Howa Type 20 5.56x45 assault rifle (Japan)
    };

    // Their rounds and magazines (the Type 20 takes the game's own STANAG magazines).
    private static readonly (string Template, int Stock, int Level, int Offer)[] WttGoods =
    {
        ("69f9ec5a7596e93e340c0a1e", 6, 3, 0x50),   // QBZ-191 5.8x42 30-round magazine
        ("6a42661705016139300b2085", 300, 3, 0x51), // 5.8x42mm DBX95
        ("6a07208057b2695f9d001e63", 300, 3, 0x52), // 5.8x42mm DBP191
        ("6a426637ddc63098d100ae67", 120, 4, 0x53), // 5.8x42mm DVX12
        ("6a42662fcb506840dd053827", 120, 4, 0x54), // 5.8x42mm DVC12
    };

    // The game's own items Dr. Yang stocks: template, stock, loyalty level, offer number.
    private static readonly (string Template, int Stock, int Level, int Offer)[] Goods =
    {
        ("5751a25924597722c463c472", 10, 1, 10),   // Army bandage            basic care
        ("544fb25a4bdc2dfb738b4567", 10, 1, 11),   // Aseptic bandage
        ("590c661e86f7741e566b646a", 4, 2, 15),    // Car first aid kit       basic veterinary supplies
        ("544fb45d4bdc2dee738b4568", 3, 2, 16),    // Salewa
        ("590c678286f77426c9660122", 3, 3, 17),    // IFAK                    veterinary medical kits
        ("60098ad7c2240c0fe85c570a", 2, 3, 18),    // AFAK
        ("5e2af29386f7746d4159f077", 3, 3, 19),    // KEKTAPE                 specialist (repairs)
        ("64be7110bf597ba84a0a41ea", 2, 3, 20),    // Type 56 Chicom chest harness
        ("68bee22e79c8186398098e6d", 1, 3, 21),    // DevTac Samurai Menpo masks
        ("68bee238a48c3c320808abc4", 1, 3, 22),
        ("68bee23f6b0736bebc012add", 1, 3, 23),
        ("68bee246ede5c8489f08e1b3", 1, 3, 24),
        ("5a718f958dc32e00094b97e7", 2, 3, 25),    // SGM Tactical 50-round Glock drum (made in Korea)
        ("590c657e86f77412b013051d", 2, 4, 27),    // Grizzly                 advanced veterinary equipment
        ("5d02797c86f774203f38e30a", 1, 4, 28),    // Surv12 field surgical kit
        ("66bdc28a0b603c26902b2011", 1, 4, 30),    // DevTac Ronin ballistic helmet (with its plates)
        ("5b4329f05acfc47a86086aa1", 1, 4, 31),    // DevTac Ronin Respirator

        // Ammunition and magazines for the Soviet-pattern guns the K9 quests give, and the Type 20's.
        ("59e4d3d286f774176a36250a", 300, 1, 0x60), // 7.62x39mm HP
        ("5656d7c34bdc2d9d198b4587", 300, 2, 0x61), // 7.62x39mm PS gzh
        ("59e0d99486f7744a32234762", 120, 4, 0x62), // 7.62x39mm BP gzh
        ("573602322459776445391df1", 300, 1, 0x63), // 7.62x25mm TT LRNPC
        ("573603562459776430731618", 200, 2, 0x64), // 7.62x25mm TT Pst gzh
        ("573719762459775a626ccbc1", 300, 1, 0x65), // 9x18mm PM P gzh
        ("573719df2459775a626ccbc2", 120, 3, 0x66), // 9x18mm PM PBM gzh
        ("5e023cf8186a883be655e54f", 120, 2, 0x67), // 7.62x54mm R T-46M gzh
        ("5887431f2459777e1612938f", 120, 3, 0x68), // 7.62x54mm R LPS gzh
        ("59e77a2386f7742ee578960a", 60, 4, 0x69),  // 7.62x54mm R PS gzh
        ("59e5d83b86f7745aed03d262", 4, 1, 0x6a),   // AK 7.62x39 ribbed metal 10-round magazine
        ("59d625f086f774661516605d", 6, 1, 0x6b),   // AK 7.62x39 30-round magazine
        ("5a01c29586f77474660c694c", 4, 2, 0x6c),   // AK 7.62x39 6L10 bakelite 30-round magazine
        ("571a29dc2459771fb2755a6a", 6, 1, 0x6d),   // TT-105 8-round magazine
        ("5448c12b4bdc2d02308b456f", 6, 1, 0x6e),   // PM 8-round magazine
        ("5ea034eb5aad6446a939737b", 4, 2, 0x6f),   // PPSh-41 35-round magazine
        ("5ea034f65aad6446a939737e", 2, 3, 0x70),   // PPSh-41 71-round drum
        ("5ae0973a5acfc4001562206c", 4, 2, 0x71),   // Mosin 5-round magazine
        ("5c471c442e221602b542a6f8", 3, 3, 0x72),   // SVD 10-round magazine
        ("6513f0a194c72326990a3868", 2, 4, 0x73),   // RPD Buben 100-round box
        ("646372518610c40fc20204e8", 2, 4, 0x74),   // PK 100-round box
        ("54527a984bdc2d4e668b4567", 300, 3, 0x75), // 5.56x45mm M855
        ("54527ac44bdc2d36668b4567", 120, 4, 0x76), // 5.56x45mm M855A1
        ("55d4887d4bdc2d962f8b4570", 4, 3, 0x77),   // Colt AR-15 STANAG 30-round magazine

        // More medical and veterinary supplies.
        ("5e831507ea0a7c419c2f9bd9", 6, 1, 0x80),   // Esmarch tourniquet
        ("544fb37f4bdc2dee738b4567", 5, 1, 0x81),   // Analgin painkillers
        ("5d1b3f2d86f774253763b735", 5, 1, 0x82),   // Disposable syringe
        ("6389c6c7dbfd5e4b95197e68", 4, 1, 0x83),   // Aquapeps water purification tablets
        ("590c695186f7741e566b64a2", 4, 2, 0x84),   // Augmentin antibiotic pills
        ("5af0548586f7743a532b7e99", 3, 2, 0x85),   // Ibuprofen painkillers
        ("60098af40accd37ef2175f27", 4, 2, 0x86),   // CAT hemostatic tourniquet
        ("59e3606886f77417674759a5", 3, 2, 0x87),   // Bottle of saline solution
        ("59e361e886f774176c10a2a5", 3, 2, 0x88),   // Bottle of hydrogen peroxide
        ("5c0e533786f7747fa23f4d47", 2, 3, 0x89),   // Zagustin hemostatic injector
        ("5d02778e86f774203e7dedbe", 2, 3, 0x8a),   // CMS surgical kit
        ("619cc01e0a7c3a1a2731940c", 2, 3, 0x8b),   // Medical tools
        ("5b4335ba86f7744d2837a264", 2, 3, 0x8c),   // Medical bloodset
        ("544fb3f34bdc2d03748b456a", 2, 4, 0x8d),   // Morphine injector
        ("5af0534a86f7743b6f354284", 1, 4, 0x8e),   // Ophthalmoscope
    };

    private const double Markup = 1.15;
    private static readonly MongoId Yang = new("6a5d0c0c4fb9a3c5e1d0d0a0");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Trader? yang = traders.GetTrader(Yang);
        if (yang?.Assort?.Items == null) { logger.Warning("[K9Companion] Dr. Yang isn't loaded; his shelves are skipped."); return Task.CompletedTask; }
        int goods = 0, wtt = 0;
        foreach (var (template, stock, level, offer) in Goods)
        {
            var tpl = new MongoId(template);
            if (!templates.Items.ContainsKey(tpl)) { logger.Warning($"[K9Companion] Dr. Yang can't stock {template}: not in this game version."); continue; }
            Add(yang.Assort, K9Ids.Offer(offer), WithPreset(tpl), stock, level);
            goods++;
        }
        // Only with the WTT Content Backport (its items and its traders' builds): without it these are left out quietly.
        foreach (var (template, stock, level, offer) in WttWeapons)
        {
            List<Item>? build = TraderBuild(new MongoId(template));
            if (build == null) continue;
            Add(yang.Assort, K9Ids.Offer(offer), build, stock, level);
            wtt++;
        }
        foreach (var (template, stock, level, offer) in WttGoods)
        {
            var tpl = new MongoId(template);
            if (!templates.Items.ContainsKey(tpl)) continue;
            Add(yang.Assort, K9Ids.Offer(offer), WithPreset(tpl), stock, level);
            wtt++;
        }
        logger.Info($"[K9Companion] Dr. Yang's shelves: {goods} care, gear, ammunition and magazine offers" +
                    (wtt > 0 ? $", and {wtt} WTT Content Backport offers (QBZ-191, Type 20 and their rounds)." : "; the WTT Content Backport's Asian rifles aren't installed."));
        return Task.CompletedTask;
    }

    // A built gun as another trader sells it (the WTT Content Backport gives its guns no preset): the first offer with
    // that gun at its root, with everything fitted to it.
    private List<Item>? TraderBuild(MongoId template)
    {
        foreach (Trader trader in traders.Values)
        {
            List<Item>? stock = trader.Assort?.Items;
            Item? root = stock?.FirstOrDefault(i => i.Template == template && i.ParentId == "hideout");
            if (root == null) continue;
            var build = new List<Item> { root };
            for (int n = 0; n < build.Count; n++)
                build.AddRange(stock!.Where(i => i.ParentId == build[n].Id.ToString()));
            return Copy(build, template);
        }
        return null;
    }

    // Items with fresh ids, kept fitted to each other, the root on the given template.
    private List<Item> Copy(List<Item> items, MongoId root)
    {
        var map = items.ToDictionary(i => i.Id.ToString(), _ => new MongoId());
        var build = items.Select(item =>
        {
            Item c = cloner.Clone(item)!;
            c.Id = map[item.Id.ToString()];
            if (item.ParentId != null && map.TryGetValue(item.ParentId, out MongoId parent)) c.ParentId = parent.ToString();
            return c;
        }).ToList();
        build[0].Template = root;
        return build;
    }

    // ---- offers -------------------------------------------------------------------------------------------

    // An item with what it comes with (a helmet's plates, a gun's parts): the game's default preset as it is, if it has one.
    private List<Item> WithPreset(MongoId template)
    {
        Preset? preset = globals.ItemPresets.Values.FirstOrDefault(p => p.Encyclopedia == template && p.Items?.FirstOrDefault()?.Template == template);
        return preset != null ? Copy(preset.Items.ToList(), template) : new List<Item> { new() { Id = new MongoId(), Template = template } };
    }

    private void Add(TraderAssort assort, MongoId offer, List<Item> build, int stock, int level)
    {
        assort.Items.RemoveAll(i => i.Id == offer);
        MongoId oldRoot = build[0].Id;
        foreach (Item i in build) if (i.ParentId == oldRoot.ToString()) i.ParentId = offer.ToString();
        build[0].Id = offer; build[0].ParentId = "hideout"; build[0].SlotId = "hideout"; build[0].Location = null;
        Upd upd = build[0].Upd ??= new Upd();
        upd.UnlimitedCount = false; upd.StackObjectsCount = stock;
        assort.Items.AddRange(build);
        double price = build.Sum(i => Price(i.Template)) * Markup;
        assort.BarterScheme[offer] = [[new BarterScheme { Count = Math.Max(100, Math.Round(price / 100) * 100), Template = K9Ids.Roubles }]];
        assort.LoyalLevelItems[offer] = level;
    }

    // The handbook price, as traders use (flea prices of single weapon parts run far above what a built gun is worth).
    private double Price(MongoId template)
    {
        double? handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == template)?.Price;
        if (handbook is > 0) return handbook.Value;
        return templates.Prices.TryGetValue(template, out double flea) && flea > 0 ? flea : 1000;
    }
}
