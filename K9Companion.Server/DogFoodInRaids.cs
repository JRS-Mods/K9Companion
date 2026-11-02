using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;

namespace K9Companion.Server;

/// <summary>
/// Dog food in raids. Dr. Yang's quests take it found in raid only, so it turns up wherever its vanilla look-alike does,
/// as often: the can wherever the can of beef stew is, the pack of meat wherever the dried beef is, in containers, lying
/// loose and in scavs' pockets, vests and bags (BotQuestItemsPatch marks those found in raid, as scav loot isn't otherwise).
/// A person can eat it too, and it does them little good: hardly any energy, and it leaves them thirsty.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public sealed class DogFoodInRaids(LocationTable locations, BotTable bots, TemplateTable templates, ISptLogger<DogFoodInRaids> logger) : IOnLoad
{
    private static readonly (MongoId Ours, MongoId Like)[] Pairs =
    {
        (DogFoodItems.DogFood, new MongoId("57347d7224597744596b4e72")),   // can of beef stew (large)
        (DogFoodItems.DogMeat, new MongoId("65815f0e647e3d7246384e14")),   // pack of dried beef
    };

    public static bool IsDogFood(MongoId tpl) => tpl == DogFoodItems.DogFood || tpl == DogFoodItems.DogMeat;

    private static readonly string[] Scavs = { "assault", "cursedassault", "marksman" };

    // What eating it does to a person (energy, hydration): the beef it copies gives 45-55 energy for 5 water.
    private static readonly (MongoId Item, double Energy, double Hydration)[] ForPeople =
    {
        (DogFoodItems.DogFood, 15, -20), (DogFoodItems.DogMeat, 10, -25), (K9Ids.PremiumFood, 20, -20), (K9Ids.Treats, 5, -10),
    };

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
                foreach (var (ours, like) in Pairs)
                    if (pool.TryGetValue(like, out double weight) && pool.TryAdd(ours, weight)) pools++;
            }
        }
        logger.Info($"[K9Companion] Dog food turns up in raids on {maps} maps and in {pools} scav loot pools, as often as the beef it copies.");
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
            foreach (var (ours, like) in Pairs)
            {
                ItemDistribution? source = list.FirstOrDefault(d => d.Tpl == like);
                if (source == null || list.Any(d => d.Tpl == ours)) continue;
                list.Add(new ItemDistribution { Tpl = ours, RelativeProbability = source.RelativeProbability });
                added = true;
            }
            if (added) details.ItemDistribution = list;
        }
        return loot;
    }

    // A loose spot lists the items it can be (each under a key) and the odds of each: ours joins every spot that can be its
    // look-alike, at the same odds.
    private static LooseLoot? LyingLoose(LooseLoot? loot)
    {
        if (loot?.Spawnpoints == null) return loot;
        foreach (Spawnpoint point in loot.Spawnpoints)
        {
            if (point.Template?.Items == null || point.ItemDistribution == null) continue;
            var items = point.Template.Items.ToList();
            var odds = point.ItemDistribution.ToList();
            bool added = false;
            foreach (var (ours, like) in Pairs)
            {
                SptLootItem? source = items.FirstOrDefault(i => i.Template == like && string.IsNullOrEmpty(i.ParentId));
                string key = ours.ToString();
                if (source == null || items.Any(i => i.ComposedKey == key)) continue;
                LooseLootItemDistribution? theirs = odds.FirstOrDefault(d => d.ComposedKey?.Key == source.ComposedKey);
                if (theirs == null) continue;
                items.Add(new SptLootItem { Id = new MongoId(), Template = ours, ComposedKey = key, Upd = new Upd { StackObjectsCount = 1 } });
                odds.Add(new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = key }, RelativeProbability = theirs.RelativeProbability });
                added = true;
            }
            if (!added) continue;
            point.Template.Items = items;
            point.ItemDistribution = odds;
        }
        return loot;
    }
}
