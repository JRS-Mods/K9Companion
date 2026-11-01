using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
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
