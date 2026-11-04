using System.Reflection;
using System.Text.Json.Serialization;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

public sealed class BagStartRequest : IRequestData
{
    [JsonPropertyName("bagId")] public string? BagId { get; set; }
    [JsonPropertyName("kitIds")] public List<string>? KitIds { get; set; }
}

public sealed class BagEmptyRequest : IRequestData { }

[Injectable]
public sealed class BagRouter(JsonUtil json, BagKeeper keeper) : StaticRouter(json, [
    new RouteAction<BagStartRequest>("/k9companion/bag/raid-start",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.RaidStart(sessionId, info?.BagId, info?.KitIds))),
    new RouteAction<BagEmptyRequest>("/k9companion/bag/dog-died",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.DogDied(sessionId))),
    new RouteAction<BagEmptyRequest>("/k9companion/bag/dog-revived",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.DogRevived(sessionId)))
]);

[Injectable]
public sealed class K9Router(JsonUtil json, K9Keeper k9) : StaticRouter(json, [
    new RouteAction<BagEmptyRequest>("/k9companion/k9/state",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.State(sessionId))),
    new RouteAction<K9Request>("/k9companion/k9/event",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.Event(sessionId, info))),
    new RouteAction<K9Request>("/k9companion/k9/save",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.Save(sessionId, info))),
    new RouteAction<K9Request>("/k9companion/k9/raid-start",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.RaidStart(sessionId, info))),
    new RouteAction<BagEmptyRequest>("/k9companion/k9/dog-died",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.DogDied(sessionId))),
    new RouteAction<BagEmptyRequest>("/k9companion/k9/dog-revived",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(k9.DogRevived(sessionId)))
]);

[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public sealed class BagPatches(IEnumerable<IRuntimePatch> patches) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (IRuntimePatch patch in patches.Where(p => p.GetType().Namespace == typeof(BagPatches).Namespace)) patch.Enable();
        return Task.CompletedTask;
    }
}

// Each game start: the K9 state first (ownership, kennel, contracts), then the dog gear (holder, worn jacket) if owned.
[Injectable]
public sealed class GameStartPatch : AbstractPatch
{
    private static BagKeeper? keeper;
    private static K9Keeper? k9;
    public GameStartPatch(BagKeeper bagKeeper, K9Keeper k9Keeper) { keeper = bagKeeper; k9 = k9Keeper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GameController), nameof(GameController.GameStart));

    // Before the game's profile check, which drops quests that no longer exist: Dr. Yang's retired quests hand their
    // progress on first (K9Keeper.MigrateQuestline).
    [PatchPrefix]
    public static void Prefix(MongoId sessionId) => k9?.BeforeGameStart(sessionId);

    [PatchPostfix]
    public static void Postfix(MongoId sessionId)
    {
        k9?.GameStarted(sessionId);
        keeper?.GameStarted(sessionId);
    }
}

// Scavs and PMCs may carry the K9 tag and the K9 unit insignia that the questline wants (K9Items.OnBots), found-in-raid
// once they're off them like a dogtag. The player's own scav doesn't start with either.
[Injectable]
public sealed class BotQuestItemsPatch : AbstractPatch
{
    private static readonly HashSet<EquipmentSlots> Anywhere = new() { EquipmentSlots.Pockets, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack };
    private static BotGeneratorHelper? helper;
    public BotQuestItemsPatch(BotGeneratorHelper botGeneratorHelper) { helper = botGeneratorHelper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotLootGenerator), nameof(BotLootGenerator.GenerateLoot));

    [PatchPostfix]
    public static void Postfix(MongoId botId, BotGenerationDetails botGenerationDetails, BotBaseInventory botInventory)
    {
        // Dog food a bot carries counts as found in raid, as Dr. Yang's quests want it (scav loot isn't, otherwise).
        foreach (Item food in botInventory.Items ?? Enumerable.Empty<Item>())
            if (DogFoodInRaids.IsDogFood(food.Template)) (food.Upd ??= new Upd()).SpawnedInSession = true;
        if (botGenerationDetails.IsPlayerScav || !(botGenerationDetails.IsPmc || botGenerationDetails.RoleLowercase is "assault" or "cursedassault" or "marksman")) return;
        foreach (var (template, chance) in K9Items.OnBots)
        {
            if (Random.Shared.NextDouble() >= chance) continue;
            var id = new MongoId();
            var item = new Item { Id = id, Template = template, Upd = new Upd { StackObjectsCount = 1, SpawnedInSession = true } };
            helper?.AddItemWithChildrenToEquipmentSlot(botId, Anywhere, id, template, new[] { item }, botInventory);
        }
    }
}

// Items handed over for a quest: Dr. Yang's hand-in objectives are ticked off in the profile (see K9Keeper.SyncCompletion).
[Injectable]
public sealed class QuestHandoverPatch : AbstractPatch
{
    private static K9Keeper? k9;
    public QuestHandoverPatch(K9Keeper k9Keeper) { k9 = k9Keeper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestController), nameof(QuestController.HandoverQuest));

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, MongoId sessionID, ItemEventRouterResponse __result)
    {
        if (pmcData != null) k9?.SyncCompletion(pmcData, __result?.ProfileChanges?.GetValueOrDefault(sessionID));
    }
}

// A quest taken: what was already done for it (its dog objectives, objectives carried over from a quest it took over
// in 0.30) is ticked off at once.
[Injectable]
public sealed class QuestAcceptPatch : AbstractPatch
{
    private static K9Keeper? k9;
    public QuestAcceptPatch(K9Keeper k9Keeper) { k9 = k9Keeper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestController), nameof(QuestController.AcceptQuest));

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, MongoId sessionID, ItemEventRouterResponse __result)
    {
        if (pmcData != null) k9?.QuestAccepted(pmcData, __result?.ProfileChanges?.GetValueOrDefault(sessionID));
    }
}

// A quest handed in: Trust Is Earned gives a new handler the dog; Dr. Yang's contracts start their cool-down.
[Injectable]
public sealed class QuestCompletePatch : AbstractPatch
{
    private static K9Keeper? k9;
    public QuestCompletePatch(K9Keeper k9Keeper) { k9 = k9Keeper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(QuestController), nameof(QuestController.CompleteQuest));

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, CompleteQuestRequestData request, MongoId sessionId, ItemEventRouterResponse __result)
    {
        if (pmcData?.Quests?.Any(q => q.QId == request.QuestId && q.Status == QuestStatusEnum.Success) == true)
            k9?.QuestCompleted(sessionId, pmcData, request.QuestId, __result);
    }
}

// After the game has saved the raid's inventory (and wiped it on death): put the jacket back on the dog, or settle its loss.
[Injectable]
public sealed class RaidEndPatch : AbstractPatch
{
    private static BagKeeper? keeper;
    private static K9Keeper? k9;
    public RaidEndPatch(BagKeeper bagKeeper, K9Keeper k9Keeper) { keeper = bagKeeper; k9 = k9Keeper; }
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LocationLifecycleService), "HandlePostRaidPmc");

    // Before the game's raid end touches the profile: the dog's gear as it went in (for its insurance, if it's lost).
    [PatchPrefix]
    public static void Prefix(MongoId sessionId, SptProfile fullServerProfile)
    {
        var pmc = fullServerProfile?.CharacterData?.PmcData;
        if (pmc != null) keeper?.BeforeRaidEnd(sessionId, pmc);
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, SptProfile fullServerProfile, bool isDead, bool isTransfer, EndLocalRaidRequestData request, string locationName)
    {
        var pmc = fullServerProfile?.CharacterData?.PmcData;
        if (pmc == null) return;
        keeper?.RaidEnded(sessionId, pmc, isDead, isTransfer, request, locationName);
        k9?.RaidEnded(pmc, isDead, isTransfer, request);
    }
}
