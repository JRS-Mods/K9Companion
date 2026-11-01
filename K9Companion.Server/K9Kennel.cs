using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

/// <summary>
/// The K9 Kennel. The game can't be given a new hideout area (the client builds its areas from its own asset list), so
/// the kennel is three improvements of the Rest Space, where the kennel stands: the hideout shows them in that area's
/// screen with their materials and build timers, takes the materials itself and keeps them in the profile. An
/// improvement can only ask for items, so the quest and loyalty requirements are checked here (and shown and enforced in
/// the client's Rest Space screen as well).
/// </summary>
public static class Kennel
{
    public sealed record Level(int Number, string Name, int Seconds, (string Template, int Count)[] Materials);

    public static readonly Level[] Levels =
    {
        new(1, "Makeshift Kennel", 3600, new[] { (K9Ids.Roubles.ToString(), 50_000), (K9Ids.CorrugatedHose, 2), (K9Ids.Bolts, 4), (K9Ids.ScrewNuts, 4) }),
        new(2, "Working Dog Station", 3 * 3600, new[] { (K9Ids.Roubles.ToString(), 175_000), (K9Ids.Toolset, 2), (K9Ids.MetalParts, 3), (K9Ids.Bolts, 5) }),
        new(3, "Tactical K9 Facility", 8 * 3600, new[] { (K9Ids.Roubles.ToString(), 450_000), (K9Ids.MilitaryCable, 2), (K9Ids.Ledx, 1), (K9Ids.Toolset, 2) }),
    };
    private static readonly MongoId Yang = new("6a5d0c0c4fb9a3c5e1d0d0a0");

    // SPT's hideout.json overrideBuildTimeSeconds (-1 = off) speeds the kennel up like the game's own areas.
    public static int Override = -1;
    public static int Seconds(Level level) => Override >= 0 ? Override : level.Seconds;

    public static bool Done(PmcData pmc, int level)
    {
        if (pmc.Hideout?.Improvements == null || !pmc.Hideout.Improvements.TryGetValue(K9Ids.Kennel[level - 1], out HideoutImprovement? i)) return false;
        return i.Completed == true || (i.ImproveCompleteTimestamp is long at && at > 0 && at <= DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static int CurrentLevel(PmcData pmc)
    {
        int level = 0;
        while (level < 3 && Done(pmc, level + 1)) level++;
        return level;
    }

    public static int YangLoyalty(PmcData pmc)
        => pmc.TradersInfo != null && pmc.TradersInfo.TryGetValue(Yang, out var info) ? info.LoyaltyLevel ?? 1 : 1;

    public static bool QuestDone(PmcData pmc, int quest)
        => pmc.Quests?.Any(q => q.QId == K9Ids.Quest(quest) && q.Status == QuestStatusEnum.Success) == true;

    /// <summary>Why a level can't be started yet, or null.</summary>
    public static string? Locked(PmcData pmc, int level, bool owned)
    {
        if (pmc.Hideout?.Improvements?.ContainsKey(K9Ids.Kennel[level - 1]) == true) return "already built or under construction";
        if (!owned) return "you have no dog yet (Trust Is Earned)";
        return level switch
        {
            1 => QuestDone(pmc, K9Ids.TrustIsEarned) ? null : "complete Dr. Yang's quest Trust Is Earned",
            2 => !Done(pmc, 1) ? "build the Makeshift Kennel first" : YangLoyalty(pmc) < 2 ? "Dr. Yang loyalty level 2" : null,
            _ => !Done(pmc, 2) ? "build the Working Dog Station first" : YangLoyalty(pmc) < 4 ? "Dr. Yang loyalty level 4" : null,
        };
    }
}

[Injectable(TypePriority = OnLoadOrder.Preload + 4)]
public sealed class KennelArea(HideoutTable hideout, HideoutConfig hideoutConfig, ISptLogger<KennelArea> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Kennel.Override = (int)hideoutConfig.OverrideBuildTimeSeconds;
        if (Kennel.Override >= 0) logger.Info($"[K9Companion] K9 Kennel levels take {Kennel.Override} s to build (hideout.json overrideBuildTimeSeconds).");
        HideoutArea? rest = hideout.Areas.FirstOrDefault(a => a.Type == HideoutAreas.RestSpace);
        if (rest?.Stages == null) { logger.Error("[K9Companion] The Rest Space wasn't found; the K9 Kennel can't be built."); return Task.CompletedTask; }
        // Every level is listed at every Rest Space level, so the kennel shows whichever level the Rest Space is at;
        // its state is per profile, by improvement id, so listing it more than once is harmless.
        int stages = 0;
        foreach (var (level, stage) in rest.Stages)
        {
            if (level == "0") continue;
            stage.Improvements ??= new List<StageImprovement>();
            stage.Improvements.RemoveAll(i => K9Ids.Kennel.Contains(i.Id));
            stage.Improvements.AddRange(Kennel.Levels.Select(l => new StageImprovement
            {
                Id = K9Ids.Kennel[l.Number - 1], ImprovementTime = Kennel.Seconds(l), Bonuses = new List<StageImprovementBonus>(),
                Requirements = l.Materials.Select(m => new StageImprovementRequirement
                {
                    Type = "Item", TemplateId = new MongoId(m.Template), Count = m.Count,
                    IsFunctional = false, IsEncoded = false, IsSpawnedInSession = false
                }).ToList()
            }));
            stages++;
        }
        logger.Info($"[K9Companion] K9 Kennel: 3 levels added to the Rest Space ({stages} stages).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The game's improvement handler starts every improvement listed for the area's current stage at once, which would
/// start all three kennel levels for the price of one. For the kennel's ids this does what the client asked instead:
/// checks the level may be built, takes the chosen items exactly as the game's handler does, and starts that one level.
/// </summary>
[Injectable]
public sealed class KennelImprovePatch : AbstractPatch
{
    private static K9Store? store;
    private static EventOutputHolder? outputs;
    private static HttpResponseUtil? responses;
    private static InventoryHelper? inventory;
    private static PaymentHelper? payments;
    private static ISptLogger<KennelImprovePatch>? log;

    public KennelImprovePatch(K9Store k9, EventOutputHolder eventOutputs, HttpResponseUtil http, InventoryHelper inventoryHelper,
        PaymentHelper paymentHelper, ISptLogger<KennelImprovePatch> logger)
    { store = k9; outputs = eventOutputs; responses = http; inventory = inventoryHelper; payments = paymentHelper; log = logger; }

    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(HideoutController), nameof(HideoutController.ImproveArea));

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, PmcData pmcData, HideoutImproveAreaRequestData request, ref ItemEventRouterResponse __result)
    {
        int index = Array.IndexOf(K9Ids.Kennel, request.AreaId);
        if (index < 0 || store == null || outputs == null || responses == null || inventory == null || payments == null) return true;
        Kennel.Level level = Kennel.Levels[index];
        ItemEventRouterResponse output = outputs.GetOutput(sessionId);
        string? locked = Kennel.Locked(pmcData, level.Number, store.Owned(pmcData));
        if (locked != null)
        {
            log?.Warning($"[K9Companion] Refused to start the kennel's {level.Name}: {locked}.");
            __result = responses.AppendErrorToOutput(output, "K9 Kennel: " + locked);
            return false;
        }
        foreach (var handed in request.Items ?? new List<HideoutItem>())
        {
            Item? item = pmcData.Inventory?.Items?.FirstOrDefault(i => i.Id == handed.Id);
            if (item == null) { __result = responses.AppendErrorToOutput(output, "K9 Kennel: an item you chose is gone."); return false; }
            if (payments.IsMoneyTpl(item.Template) && item.Upd?.StackObjectsCount is double stack && stack > handed.Count)
            {
                item.Upd.StackObjectsCount -= handed.Count;
                continue;
            }
            inventory.RemoveItem(pmcData, item.Id, sessionId, output);
        }
        var started = new HideoutImprovement { Completed = false, ImproveCompleteTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Kennel.Seconds(level) };
        pmcData.Hideout!.Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        pmcData.Hideout.Improvements[K9Ids.Kennel[index]] = started;
        output.ProfileChanges[sessionId].Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        output.ProfileChanges[sessionId].Improvements![K9Ids.Kennel[index]] = started;
        log?.Info($"[K9Companion] Building the kennel's {level.Name} (ready in {Kennel.Seconds(level) / 3600.0:0.##} h).");
        __result = output;
        return false;
    }
}
