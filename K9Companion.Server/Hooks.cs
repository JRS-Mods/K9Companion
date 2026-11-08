using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Modding.Custom;
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
    new RouteAction<BagEmptyRequest>("/k9companion/bag/gear-taken",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.GearTaken(sessionId))),
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

/// <summary>
/// Dog settings the server's owner fixes for everyone on it: config.jsonc in this mod's folder, read each time a game asks
/// (just before a raid and on the raid screens), so a change needs no restart. Anything left null stays each player's own
/// setting. Made with everything null if it isn't there; a new version of the mod only adds its new settings to it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 5)]
public sealed class DogSettingsFile(ModHelper mods, ISptLogger<DogSettingsFile> logger) : IOnLoad
{
    // Other mods' configs ask for trader IDs, and Dr. Yang has no base.json (he's built in code), so his is noted here.
    private static readonly string TraderLine = $"// Dr. Yang's trader ID, for other mods' configs: {DrYang.Id}";

    private static readonly string Defaults = """
        {
          // K9 Companion: dog settings for everyone on this server (with Fika, everyone in the raid).
          // Saved changes count from each player's next raid; no restart needed.
          // null = each player keeps their own setting (F12 in game). Anything else is fixed for everyone.
          {TraderLine}

          "combatMode": null,       // true: dogs fight and can be hurt. false: dogs only follow, and can't be hurt.
          "aggression": null,        // "Passive" (attacks only on Over There), "Defensive" (also whoever attacks you or it), "Aggressive" (anything it detects).
          "approach": null,          // "Auto" (straight at anything close, stalks the rest), "Stealth" (works its way in out of sight), "Direct" (runs straight at it).
          "autoRushRange": null,     // 5 to 40: under Auto, how close an enemy has to be for a straight attack, in metres.
          "defendRange": null,       // 10 to 300: with "Defensive" or "Aggressive", how far from you, in metres, a dog goes after someone shooting at you or it.
          "engageDistance": null,    // 5 to 60: how far from you, in metres, a dog goes after enemies on its own.
          "orderRange": null,        // 25 to 300: how far away you can send it with Over There, in metres.
          "bitesToKill": null,       // 1 to 10: bites to the throat that kill a scav, PMC or player, armour or not (2.5 = two or three).
          "bossBitesToKill": null,   // 1 to 20: the same for bosses.
          "biteCooldown": null,      // 0.8 to 5: seconds between bites.
          "biteStagger": null,       // 0 to 3: seconds a bitten enemy is thrown off for.
          "soloOnly": null,          // true: dogs only come on raids with one player, so none in a Fika group.
          "downedMode": null,        // true: a dog out of health collapses and can be revived with a K9 veterinary kit. false: it dies.
          "bleedOutTime": null,      // 0 to 600: seconds to revive a downed dog (0 = as long as the raid lasts).
          "carryLimit": null,        // 0 to 200: kg a dog's jacket can weigh before it slows down (0 = no limit).
          "dogHealth": null          // 30 to 1000: a dog's hit points.
        }
        """.Replace("{TraderLine}", TraderLine);

    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly (string Key, double Min, double Max)[] Numbers =
    {
        ("bleedOutTime", 0, 600), ("carryLimit", 0, 200), ("autoRushRange", 5, 40), ("defendRange", 10, 300), ("engageDistance", 5, 60), ("orderRange", 25, 300), ("bitesToKill", 1, 10), ("bossBitesToKill", 1, 20), ("biteCooldown", .8, 5), ("biteStagger", 0, 3), ("dogHealth", 30, 1000)
    };

    private string FilePath => System.IO.Path.Combine(mods.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "config.jsonc");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, Defaults);
            else AddNewSettings();
            string now = Read();
            if (now != "{}") logger.Info("[K9Companion] Dog settings fixed for everyone on this server: " + now);
        }
        catch (Exception e) { logger.Warning("[K9Companion] Couldn't set up " + FilePath + ": " + e.Message); }
        return Task.CompletedTask;
    }

    // A file from an older version gets the new lines, and keeps everything else as it is: the three approach settings
    // (0.36), and bites to kill in place of a bite's damage (0.38).
    private void AddNewSettings()
    {
        string text = File.ReadAllText(FilePath);
        int top = text.IndexOf("\"combatMode\"", StringComparison.Ordinal);
        if (!text.Contains("trader ID") && top >= 0)
        {
            int start = text.LastIndexOf('\n', top) + 1;
            text = text.Insert(start, new string(' ', top - start) + TraderLine + "\n\n");
        }
        int at = text.IndexOf("\"engageDistance\"", StringComparison.Ordinal);
        if (!text.Contains("\"approach\"") && at >= 0)
        {
            string indent = new string(' ', at - (text.LastIndexOf('\n', at) + 1));
            text = text.Insert(at,
                "\"approach\": null,          // \"Auto\" (straight at anything close, stalks the rest), \"Stealth\" (works its way in out of sight), \"Direct\" (runs straight at it).\n" + indent +
                "\"autoRushRange\": null,     // 5 to 40: under Auto, how close an enemy has to be for a straight attack, in metres.\n" + indent +
                "\"defendRange\": null,       // 10 to 300: with \"Defensive\" or \"Aggressive\", how far from you, in metres, a dog goes after someone shooting at you or it.\n" + indent);
        }
        int old = text.IndexOf("\"biteDamage\"", StringComparison.Ordinal);
        if (!text.Contains("\"bitesToKill\"") && old >= 0)
        {
            int start = text.LastIndexOf('\n', old) + 1, end = text.IndexOf('\n', old);
            string indent = new string(' ', old - start);
            text = text[..start] + indent +
                "\"bitesToKill\": null,       // 1 to 10: bites to the throat that kill a scav, PMC or player, armour or not (2.5 = two or three).\n" + indent +
                "\"bossBitesToKill\": null,   // 1 to 20: the same for bosses." + (end < 0 ? "" : text[end..]);
        }
        int health = text.IndexOf("\"dogHealth\"", StringComparison.Ordinal);
        if (!text.Contains("\"soloOnly\"") && health >= 0)
        {
            string indent = new string(' ', health - (text.LastIndexOf('\n', health) + 1));
            text = text.Insert(health,
                "\"soloOnly\": null,          // true: dogs only come on raids with one player, so none in a Fika group.\n" + indent +
                "\"downedMode\": null,        // true: a dog out of health collapses and can be revived with a K9 veterinary kit. false: it dies.\n" + indent +
                "\"bleedOutTime\": null,      // 0 to 600: seconds to revive a downed dog (0 = as long as the raid lasts).\n" + indent +
                "\"carryLimit\": null,        // 0 to 200: kg a dog's jacket can weigh before it slows down (0 = no limit).\n" + indent);
        }
        File.WriteAllText(FilePath, text);
    }

    /// <summary>What's fixed, as JSON: only the settings with a usable value. A problem in the file fixes nothing rather than breaking a raid.</summary>
    public string Read()
    {
        JsonObject answer = new();
        try
        {
            if (!File.Exists(FilePath)) return "{}";
            if (JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: Reading) is not JsonObject file) return "{}";
            if (file["combatMode"] is JsonNode combat)
            {
                if (combat.GetValueKind() is JsonValueKind.True or JsonValueKind.False) answer["combatMode"] = combat.GetValue<bool>();
                else logger.Warning("[K9Companion] config.jsonc: combatMode should be true, false or null; ignored.");
            }
            if (file["approach"] is JsonNode how)
            {
                string? text = how.GetValueKind() == JsonValueKind.String ? how.GetValue<string>() : null;
                string? known = new[] { "Auto", "Stealth", "Direct" }.FirstOrDefault(a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (known != null) answer["approach"] = known;
                else logger.Warning("[K9Companion] config.jsonc: approach should be \"Auto\", \"Stealth\", \"Direct\" or null; ignored.");
            }
            // From before the approach had three settings.
            else if (file["stealthApproach"] is JsonNode old && old.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                answer["approach"] = old.GetValue<bool>() ? "Stealth" : "Direct";
            if (file["aggression"] is JsonNode level)
            {
                string? text = level.GetValueKind() == JsonValueKind.String ? level.GetValue<string>() : null;
                string? known = new[] { "Passive", "Defensive", "Aggressive" }.FirstOrDefault(a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (known != null) answer["aggression"] = known;
                else logger.Warning("[K9Companion] config.jsonc: aggression should be \"Passive\", \"Defensive\", \"Aggressive\" or null; ignored.");
            }
            foreach (string key in new[] { "soloOnly", "downedMode" })
            {
                if (file[key] is not JsonNode flag) continue;
                if (flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False) answer[key] = flag.GetValue<bool>();
                else logger.Warning($"[K9Companion] config.jsonc: {key} should be true, false or null; ignored.");
            }
            foreach (var (key, min, max) in Numbers)
            {
                JsonNode? v = file[key];
                if (v == null) continue;
                if (v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.GetValue<double>())) answer[key] = Math.Clamp(v.GetValue<double>(), min, max);
                else logger.Warning($"[K9Companion] config.jsonc: {key} should be a number or null; ignored.");
            }
        }
        catch (Exception e)
        {
            logger.Warning("[K9Companion] config.jsonc couldn't be read, so no dog settings are fixed: " + e.Message);
            return "{}";
        }
        return answer.ToJsonString();
    }
}

public sealed class VetUseRequest : IRequestData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("amount")] public double Amount { get; set; }
}

/// <summary>
/// Takes what treating the dog used off a K9 veterinary kit, out of a raid (in one, the kit is saved with the rest of your
/// gear when you get out). The last of a kit goes through the game's own discard instead, so nothing here removes one.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VetKeeper(ProfileHelper profiles, ISptLogger<VetKeeper> logger)
{
    private const double FullKit = K9Items.VetKitResource;

    public string Use(MongoId sessionId, VetUseRequest? request)
    {
        const string no = "{\"ok\":false}";
        var pmc = profiles.GetPmcProfile(sessionId);
        var item = pmc?.Inventory?.Items?.FirstOrDefault(i => i.Id.ToString() == request?.Id);
        if (request == null || item == null || item.Template != K9Ids.VetKit || !double.IsFinite(request.Amount) || request.Amount <= 0)
        {
            logger.Warning($"[K9Companion] Refused to use a veterinary kit: {request?.Id} x {request?.Amount}.");
            return no;
        }
        item.Upd ??= new Upd();
        double left = item.Upd.MedKit?.HpResource ?? FullKit;
        if (request.Amount >= left) return no;
        left -= request.Amount;
        item.Upd.MedKit = new UpdMedKit { HpResource = left };
        return "{\"ok\":true,\"left\":" + left.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
    }
}

[Injectable]
public sealed class SettingsRouter(JsonUtil json, DogSettingsFile settings, VetKeeper vet) : StaticRouter(json, [
    new RouteAction<BagEmptyRequest>("/k9companion/settings",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(settings.Read())),
    new RouteAction<VetUseRequest>("/k9companion/vet/use",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(vet.Use(sessionId, info)))
]);
