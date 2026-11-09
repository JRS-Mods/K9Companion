using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

/// <summary>A report from the client: one of the dog's events, or changed kennel settings.</summary>
public sealed class K9Request : IRequestData
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("deployed")] public bool? Deployed { get; set; }
    [JsonPropertyName("collar")] public string? Collar { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("bed")] public int? Bed { get; set; }
    [JsonPropertyName("training")] public int? Training { get; set; }
    [JsonPropertyName("presets")] public List<string>? Presets { get; set; }
    [JsonPropertyName("armour")] public Dictionary<string, ArmourRecord>? Armour { get; set; }
}

/// <summary>
/// The K9 expansion's bookkeeping, all kept in the SPT profile (quests, counters, hideout improvements, items) and in
/// K9State:
///  - The dog's objectives (K9Counter) move only through Advance, once per real event: a find, an item taken and a kill
///    are each counted once per raid, and an extraction once per raid result.
///  - After a raid the game replaces the profile's objective counters with the client's copy; Enforce puts back any
///    dog objective that copy is behind on, so progress can't be lost or rolled back.
///  - Completing Trust Is Earned gives a new handler the dog (and its gear) once; contracts reset only from completed.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class K9Keeper(ISptLogger<K9Keeper> logger, ProfileHelper profiles, K9Store store, BagKeeper bags, JsonUtil json, TemplateTable templates)
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    // (0.30 took out the dog's loot search, and with it the "locate" and "retrieve" events.)
    private static readonly HashSet<string> ClientEvents = new() { "follow", "sit", "come", "kill" };
    private static readonly HashSet<string> RaidEvents = new() { "kill" };

    // ---- client routes ----------------------------------------------------------------------------------------

    public string State(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return "{\"ready\":false}";   // no character yet: ask again later
        return json.Serialize(new Dictionary<string, object>
        {
            ["version"] = state.Version, ["owned"] = state.Owned, ["kennel"] = Kennel.CurrentLevel(pmc), ["loyalty"] = Kennel.YangLoyalty(pmc),
            ["collar"] = state.Collar, ["collars"] = Collars(pmc), ["tag"] = state.Tag, ["bed"] = state.Bed, ["training"] = state.Training, ["bedUnlocked"] = Kennel.QuestDone(pmc, K9Ids.TrustIsEarned),
            ["presets"] = state.Presets, ["armour"] = state.Armour, ["counters"] = Counters(pmc, state)
        }, false)!;
    }

    /// <summary>
    /// The collars the quests had earned before 0.31 made them items: the coyote training collar it comes with, the black
    /// one from Trust Is Earned, and the tactical buckle collar from Teeth and Steel (or with the K9 tactical collar kept
    /// in the stash). BagKeeper hands a dog from before then these as items; the client no longer picks from them.
    /// </summary>
    public static List<string> Collars(PmcData pmc)
    {
        var collars = new List<string> { "training" };
        if (Kennel.QuestDone(pmc, K9Ids.TrustIsEarned)) collars.Add("black");
        if (Kennel.QuestDone(pmc, K9Ids.TeethAndSteel) || pmc.Inventory?.Items?.Any(i => i.Template == K9Ids.TacticalCollar) == true) collars.Add("buckle");
        return collars;
    }

    public string Event(MongoId sessionId, K9Request? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        string type = request?.Type ?? "";
        if (pmc == null || state == null || !state.Owned || !ClientEvents.Contains(type)) return "{\"ok\":false}";
        if (RaidEvents.Contains(type))
        {
            // Raid events only count while the dog is out with you, and each find/item/kill only once.
            string key = request?.Key ?? "";
            if (!state.Raid.Deployed || key.Length is 0 or > 64 || state.Raid.Seen.Contains(type + ":" + key)) return Reply(pmc, state, false);
            state.Raid.Seen.Add(type + ":" + key);
        }
        bool moved = Advance(pmc, state, type);
        if (moved) SyncCompletion(pmc, null);
        store.Save(pmc, state);
        return Reply(pmc, state, moved);
    }

    public string Save(MongoId sessionId, K9Request? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null || request == null) return "{\"ok\":false}";
        if (request.Collar != null && Collars(pmc).Contains(request.Collar)) state.Collar = request.Collar;
        if (request.Tag != null) state.Tag = request.Tag;
        // The bed is only ever upgraded, one step at a time, once the dog is yours (Trust Is Earned) and the kennel exists.
        if (request.Bed is int bed && bed == state.Bed + 1 && Kennel.CurrentLevel(pmc) >= 1 && Kennel.QuestDone(pmc, K9Ids.TrustIsEarned)) state.Bed = bed;
        // Training goes up one level per kit used, likewise only with a kennel.
        if (request.Training is int trained && trained == state.Training + 1 && Kennel.CurrentLevel(pmc) >= 1) state.Training = trained;
        if (request.Presets != null) state.Presets = request.Presets;
        if (request.Armour != null)
            foreach (var (id, wear) in request.Armour) state.Armour[id] = wear;
        // Jackets that no longer exist don't need their wear remembered.
        var jackets = pmc.Inventory!.Items!.Where(i => DogGear.IsJacket(i.Template)).Select(i => i.Id.ToString()).ToHashSet();
        foreach (string gone in state.Armour.Keys.Where(k => !jackets.Contains(k)).ToList()) state.Armour.Remove(gone);
        K9Store.Sanitize(state);
        store.Save(pmc, state);
        return "{\"ok\":true}";
    }

    public string RaidStart(MongoId sessionId, K9Request? request)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return "{\"ok\":false}";
        state.Raid = new RaidRecord { Deployed = state.Owned && request?.Deployed == true, StartedAt = Now };
        CheckArmour(pmc, state);
        store.Save(pmc, state);
        return Reply(pmc, state, false);
    }

    public string DogDied(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return "{\"ok\":false}";
        if (state.Raid.Deployed) { state.Raid.DogDied = true; store.Save(pmc, state); }
        return "{\"ok\":true}";
    }

    // A downed dog is reported as dead at once, so that a raid ending or a crash counts as that; this takes it back.
    public string DogRevived(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return "{\"ok\":false}";
        if (state.Raid.DogDied) { state.Raid.DogDied = false; store.Save(pmc, state); }
        return "{\"ok\":true}";
    }

    // ---- server events ----------------------------------------------------------------------------------------

    /// <summary>Before the game checks the profile (and drops quests that no longer exist): the questline migration.</summary>
    public void BeforeGameStart(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return;
        MigrateQuestline(pmc, state);
        LockQuestline(pmc);
        store.Save(pmc, state);
    }

    public void GameStarted(MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        K9State? state = store.Load(pmc);
        if (pmc == null || state == null) return;
        // Checked every time rather than trusting the flag alone: the profile is written to disk later than this file,
        // so a server stopped in between would otherwise lose the kennel.
        if (state.Owned && state.OwnedBy == "legacy")
        {
            pmc.Hideout!.Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
            if (!pmc.Hideout.Improvements.ContainsKey(K9Ids.Kennel[0]))
                pmc.Hideout.Improvements[K9Ids.Kennel[0]] = new HideoutImprovement { Completed = true, ImproveCompleteTimestamp = Now };
            state.LegacyKennelGranted = true;
        }
        // Trust Is Earned done but the dog never handed over (e.g. completed while the server mod was out): do it now.
        if (!state.Owned && Kennel.QuestDone(pmc, K9Ids.TrustIsEarned)) Grant(pmc, state, null);
        if (state.Raid.Deployed && Now - state.Raid.StartedAt > 6 * 3600) state.Raid = new RaidRecord();   // a raid that never ended
        MigrateQuestline(pmc, state);
        ResetContracts(pmc, state);
        CheckArmour(pmc, state);
        Enforce(pmc, state);
        SyncCompletion(pmc, null);
        if (state.Owned) bags.EnsureLockers(pmc);
        store.Save(pmc, state);
    }

    // After the game has saved the raid (and replaced the profile's counters with the client's copy).
    public void RaidEnded(PmcData pmc, bool isDead, bool isTransfer, EndLocalRaidRequestData request)
    {
        K9State? state = store.Load(pmc);
        if (state == null) return;
        if (!isTransfer)
        {
            ExitStatus? result = request.Results?.Result;
            bool extracted = !isDead && result is ExitStatus.SURVIVED or ExitStatus.RUNNER;
            if (state.Raid.Deployed && !state.Raid.DogDied && extracted && Advance(pmc, state, "extract"))
                logger.Info("[K9Companion] Extracted with your dog: K9 objectives updated.");
            state.Raid = new RaidRecord();
        }
        Enforce(pmc, state);
        SyncCompletion(pmc, null);
        store.Save(pmc, state);
    }

    /// <summary>
    /// A quest was taken: dog objectives and anything carried over from a quest it took over (MigrateQuestline) show as
    /// done straight away, in the reply.
    /// </summary>
    public void QuestAccepted(PmcData pmc, ProfileChange? changes)
    {
        K9State? state = store.Load(pmc);
        if (state == null) return;
        Enforce(pmc, state);
        SyncCompletion(pmc, changes);
        store.Save(pmc, state);
    }

    /// <summary>A quest was handed in: Trust Is Earned gives the dog; a contract starts its cool-down.</summary>
    public void QuestCompleted(MongoId sessionId, PmcData pmc, MongoId questId, ItemEventRouterResponse? output)
    {
        K9State? state = store.Load(pmc);
        if (state == null) return;
        if (questId == K9Ids.Quest(K9Ids.TrustIsEarned) && !state.Owned) Grant(pmc, state, output?.ProfileChanges?.GetValueOrDefault(sessionId));
        int contract = K9Ids.Contracts.FirstOrDefault(c => K9Ids.Quest(c) == questId);
        if (contract != 0)
        {
            ContractRecord record = state.Contracts.GetValueOrDefault(questId.ToString()) ?? new ContractRecord();
            record.CompletedAt = Now; record.Times++;
            state.Contracts[questId.ToString()] = record;
        }
        store.Save(pmc, state);
    }

    /// <summary>
    /// Ticks off Dr. Yang's objectives whose counters are full (hand-ins and the dog's objectives) and, when all of a
    /// quest's objectives are done, makes it ready to hand in. The game client normally does this bookkeeping itself
    /// and only tells the server at the end of a raid, so a hand-in done in the menu, or a dog objective, could stay
    /// unticked in the saved profile; this keeps the profile right whatever the client did. Changes go into the reply
    /// when there is one, so the quest screen updates straight away.
    /// </summary>
    public void SyncCompletion(PmcData pmc, ProfileChange? changes)
    {
        HashSet<string>? carried = store.Load(pmc)?.CarriedOver;
        foreach (int n in K9Ids.Story.Concat(K9Ids.Contracts))
        {
            MongoId id = K9Ids.Quest(n);
            QuestStatus? status = pmc.Quests?.FirstOrDefault(q => q.QId == id);
            if (status == null || status.Status is not (QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish)) continue;
            if (!templates.Quests.TryGetValue(id, out Quest? quest) || quest.Conditions?.AvailableForFinish == null) continue;
            status.CompletedConditions ??= new List<string>();
            bool changed = false;
            foreach (QuestCondition c in quest.Conditions.AvailableForFinish)
            {
                if (status.CompletedConditions.Contains(c.Id.ToString())) continue;
                // Done already in a quest this one took over (MigrateQuestline).
                if (carried?.Contains(c.Id.ToString()) == true) { status.CompletedConditions.Add(c.Id.ToString()); changed = true; continue; }
                if (c.ConditionType is not ("HandoverItem" or "SellItemToTrader")) continue;
                double have = pmc.TaskConditionCounters?.GetValueOrDefault(c.Id)?.Value ?? 0;
                if (have + .001 < Convert.ToDouble(c.Value?.ToString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)) continue;
                status.CompletedConditions.Add(c.Id.ToString());
                changed = true;
            }
            if (status.Status == QuestStatusEnum.Started && quest.Conditions.AvailableForFinish.All(c => status.CompletedConditions.Contains(c.Id.ToString())))
            {
                status.Status = QuestStatusEnum.AvailableForFinish;
                status.StatusTimers ??= new Dictionary<QuestStatusEnum, double>();
                status.StatusTimers[QuestStatusEnum.AvailableForFinish] = Now;
                changed = true;
                logger.Info($"[K9Companion] Dr. Yang's {quest.QuestName}: all objectives done, ready to hand in.");
            }
            if (changed && changes != null)
            {
                changes.QuestsStatus ??= new List<QuestStatus>();
                changes.QuestsStatus.RemoveAll(q => q.QId == id);
                changes.QuestsStatus.Add(status);
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    // The dog becomes this profile's (once). Its gear holder, first jacket and kennel lockers are created now and, when
    // the hand-in's reply is at hand, sent with it so the game shows them without a restart.
    private void Grant(PmcData pmc, K9State state, ProfileChange? changes)
    {
        state.Owned = true; state.OwnedBy = "quest";
        var before = pmc.Inventory!.Items!.Select(i => i.Id).ToHashSet();
        store.Save(pmc, state);
        bags.SetUpGear(pmc);
        bags.EnsureLockers(pmc);
        if (changes != null)
        {
            changes.Items ??= new ItemChanges { NewItems = new List<Item>(), ChangedItems = new List<Item>(), DeletedItems = new List<DeletedItem>() };
            changes.Items.NewItems ??= new List<Item>();
            changes.Items.NewItems.AddRange(pmc.Inventory!.Items!.Where(i => !before.Contains(i.Id)));
        }
        logger.Info("[K9Companion] Trust Is Earned: the dog from the bunker is now yours.");
    }

    // Moves every started quest's objectives for this event on by one, up to their target. True if any moved.
    private bool Advance(PmcData pmc, K9State state, string evt)
    {
        bool moved = false;
        foreach (K9Counter k in K9Quests.Counters.Where(c => c.Event == evt))
        {
            if (!Started(pmc, k.QuestId)) continue;
            int now = Math.Max(state.Ledger.GetValueOrDefault(k.Id), ProfileValue(pmc, k));
            if (now >= k.Value) continue;
            state.Ledger[k.Id] = now + 1;
            Write(pmc, k, now + 1);
            moved = true;
        }
        return moved;
    }

    private void Enforce(PmcData pmc, K9State state)
    {
        foreach (K9Counter k in K9Quests.Counters)
        {
            if (!Started(pmc, k.QuestId) && !AvailableForFinish(pmc, k.QuestId)) continue;
            int kept = state.Ledger.GetValueOrDefault(k.Id);
            if (kept > ProfileValue(pmc, k)) Write(pmc, k, Math.Min(kept, k.Value));
        }
    }

    // Teeth and Steel: the dog wears a jacket of armour class 2 or better.
    private void CheckArmour(PmcData pmc, K9State state)
    {
        Item? holder = pmc.Inventory?.Items?.FirstOrDefault(i => i.Template == DogGear.Holder);
        Item? worn = holder == null ? null : pmc.Inventory!.Items!.FirstOrDefault(i => i.ParentId == holder.Id.ToString() && i.SlotId == DogGear.Slot);
        DogGear.Jacket? jacket = worn == null ? null : DogGear.Jackets.FirstOrDefault(j => j.Id == worn.Template);
        if (jacket?.ArmourClass >= 2 && state.Owned) Advance(pmc, state, "armour");
    }

    // 0.30 cut the story to five quests (K9Quests.Merged). A retired quest in the profile hands what was done in it to
    // the quest it became part of: its dog objectives' counts into the ledger (Enforce writes them once that quest is
    // taken), every other done objective into CarriedOver (SyncCompletion ticks it off). Then it leaves the profile,
    // with its counters. Nothing is carried into a quest already finished.
    private void MigrateQuestline(PmcData pmc, K9State state)
    {
        if (pmc.Quests == null) return;
        foreach (var (old, into, objectives) in K9Quests.Merged)
        {
            MongoId oldId = K9Ids.Quest(old);
            QuestStatus? status = pmc.Quests.FirstOrDefault(q => q.QId == oldId);
            if (status == null) continue;
            if (!Kennel.QuestDone(pmc, into))
                foreach (var (from, to) in objectives)
                {
                    string was = K9Ids.Condition(old, from), now = K9Ids.Condition(into, to);
                    bool done = status.Status == QuestStatusEnum.Success || status.CompletedConditions?.Contains(was) == true;
                    if (state.Ledger.TryGetValue(was, out int count)) state.Ledger[now] = Math.Max(state.Ledger.GetValueOrDefault(now), count);
                    if (!done) continue;
                    if (K9Quests.Counters.FirstOrDefault(k => k.Id == now) is K9Counter counter) state.Ledger[now] = counter.Value;
                    else state.CarriedOver.Add(now);
                }
            pmc.Quests.Remove(status);
            if (pmc.TaskConditionCounters != null)
                foreach (var key in pmc.TaskConditionCounters.Where(kv => kv.Value.SourceId == oldId).Select(kv => kv.Key).ToList())
                    pmc.TaskConditionCounters.Remove(key);
            foreach (string gone in state.Ledger.Keys.Where(k => k.StartsWith(K9Ids.Make(2, old, 0)[..^2])).ToList()) state.Ledger.Remove(gone);
            logger.Info($"[K9Companion] Dr. Yang's quest {old} is now part of quest {into}; what was done in it carries over.");
        }
    }

    // An SPT Developer profile starts with every quest open, whatever comes before it. Dr. Yang's open in order all the
    // same: the game closes such a quest again as soon as the one before it changes, playing the quest sound for each one
    // down the line (a burst of noise). Out of the profile, a quest appears once the one before it is done, as for anyone else.
    private static void LockQuestline(PmcData pmc)
    {
        void Lock(int quest, int after)
        {
            if (!Kennel.QuestDone(pmc, after)) pmc.Quests?.RemoveAll(q => q.QId == K9Ids.Quest(quest) && q.Status == QuestStatusEnum.AvailableForStart);
        }
        for (int i = 1; i < K9Ids.Story.Length; i++) Lock(K9Ids.Story[i], K9Ids.Story[i - 1]);
        foreach (int contract in K9Ids.Contracts) Lock(contract, K9Ids.LastHandler);
    }

    private void ResetContracts(PmcData pmc, K9State state)
    {
        foreach (int c in K9Ids.Contracts)
        {
            MongoId id = K9Ids.Quest(c);
            QuestStatus? status = pmc.Quests?.FirstOrDefault(q => q.QId == id);
            // Only a completed contract resets, and only once its cool-down has passed; one in progress keeps its progress.
            if (status?.Status != QuestStatusEnum.Success) continue;
            long completed = state.Contracts.GetValueOrDefault(id.ToString())?.CompletedAt
                             ?? (long)(status.StatusTimers?.GetValueOrDefault(QuestStatusEnum.Success) ?? 0);
            if (completed <= 0 || Now < completed + (long)K9Quests.ContractPeriod[c].TotalSeconds) continue;
            pmc.Quests!.Remove(status);
            foreach (var key in pmc.TaskConditionCounters!.Where(kv => kv.Value.SourceId == id).Select(kv => kv.Key).ToList())
                pmc.TaskConditionCounters!.Remove(key);
            foreach (K9Counter k in K9Quests.Counters.Where(k => k.Quest == c)) state.Ledger.Remove(k.Id);
            logger.Info($"[K9Companion] Dr. Yang's contract {id} is available again.");
        }
    }

    private static bool Started(PmcData pmc, MongoId quest) => pmc.Quests?.Any(q => q.QId == quest && q.Status == QuestStatusEnum.Started) == true;
    private static bool AvailableForFinish(PmcData pmc, MongoId quest) => pmc.Quests?.Any(q => q.QId == quest && q.Status == QuestStatusEnum.AvailableForFinish) == true;

    private static int ProfileValue(PmcData pmc, K9Counter k)
        => pmc.TaskConditionCounters != null && pmc.TaskConditionCounters.TryGetValue(new MongoId(k.Id), out var c) ? (int)(c.Value ?? 0) : 0;

    private static void Write(PmcData pmc, K9Counter k, int value)
    {
        pmc.TaskConditionCounters ??= new Dictionary<MongoId, TaskConditionCounter>();
        var id = new MongoId(k.Id);
        if (pmc.TaskConditionCounters.TryGetValue(id, out var counter)) counter.Value = value;
        else pmc.TaskConditionCounters[id] = new TaskConditionCounter { Id = id, SourceId = k.QuestId, Type = "SellItemToTrader", Value = value };
    }

    private static Dictionary<string, int> Counters(PmcData pmc, K9State state)
        => K9Quests.Counters.Where(k => Started(pmc, k.QuestId) || AvailableForFinish(pmc, k.QuestId))
            .ToDictionary(k => k.Id, k => Math.Max(ProfileValue(pmc, k), state.Ledger.GetValueOrDefault(k.Id)));

    private string Reply(PmcData pmc, K9State state, bool moved)
        => json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["moved"] = moved, ["counters"] = Counters(pmc, state) }, false)!;
}

/// <summary>Everything the K9 expansion remembers about one PMC, in user/profileData next to the dog's other files.</summary>
public sealed class K9State
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; }
    // Whether this profile has the dog. Profiles that had it before 0.16 keep it ("legacy"); new ones get it from
    // Trust Is Earned ("quest").
    [JsonPropertyName("owned")] public bool Owned { get; set; }
    [JsonPropertyName("ownedBy")] public string OwnedBy { get; set; } = "";
    // A legacy owner already had the bed and bowls, so their kennel starts at level 1 (applied once).
    [JsonPropertyName("legacyKennelGranted")] public bool LegacyKennelGranted { get; set; }
    // The highest value each dog objective has reached, so a counter can never go backwards (see K9Keeper.Enforce).
    [JsonPropertyName("ledger")] public Dictionary<string, int> Ledger { get; set; } = new();
    // Objectives already done in a quest that 0.30 merged into a later one: ticked off there once it's taken
    // (K9Keeper.MigrateQuestline, SyncCompletion).
    [JsonPropertyName("carriedOver")] public HashSet<string> CarriedOver { get; set; } = new();
    // This raid: whether the dog went along and whether it died, and which finds/kills have been counted already.
    [JsonPropertyName("raid")] public RaidRecord Raid { get; set; } = new();
    // When each contract was last completed (unix seconds) and how many times.
    [JsonPropertyName("contracts")] public Dictionary<string, ContractRecord> Contracts { get; set; } = new();
    [JsonPropertyName("collar")] public string Collar { get; set; } = "training";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "steel";
    [JsonPropertyName("bed")] public int Bed { get; set; }
    // Levels of training from K9 training kits (0-3): each lets the dog smell valuables 2 m further.
    [JsonPropertyName("training")] public int Training { get; set; }
    [JsonPropertyName("presets")] public List<string> Presets { get; set; } = new();
    [JsonPropertyName("armour")] public Dictionary<string, ArmourRecord> Armour { get; set; } = new();
}

public sealed class RaidRecord
{
    [JsonPropertyName("deployed")] public bool Deployed { get; set; }
    [JsonPropertyName("dogDied")] public bool DogDied { get; set; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; set; }
    [JsonPropertyName("seen")] public List<string> Seen { get; set; } = new();
}

public sealed class ContractRecord
{
    [JsonPropertyName("completedAt")] public long CompletedAt { get; set; }
    [JsonPropertyName("times")] public int Times { get; set; }
}

public sealed class ArmourRecord
{
    [JsonPropertyName("now")] public double Now { get; set; }
    [JsonPropertyName("max")] public double Max { get; set; }
}

/// <summary>
/// Reads and writes K9State. A profile without a file is migrated once: it counts as an existing owner if it already
/// has any of the dog's data (gear holder, saddlebag record, needs file), so nobody who had the dog loses it.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class K9Store(ISptLogger<K9Store> logger, FileUtil files, JsonUtil json)
{
    public static string Folder(PmcData pmc) => $"user/profileData/{pmc.Id}";
    private static string PathFor(PmcData pmc) => $"{Folder(pmc)}/K9Companion.k9.json";

    /// <summary>The state, created (and saved) on first use. Null until the PMC exists.</summary>
    public K9State? Load(PmcData? pmc)
    {
        if (pmc?.Inventory?.Items == null || pmc.Id == null) return null;
        string path = PathFor(pmc);
        K9State? state = null;
        try { if (files.FileExists(path)) state = json.Deserialize<K9State>(files.ReadFile(path)); }
        catch (Exception e) { logger.Error($"[K9Companion] Couldn't read {path} (keeping a copy, starting from your existing data): {e.Message}"); Backup(path); }
        if (state == null)
        {
            bool legacy = pmc.Inventory.Items.Any(i => i.Template == DogGear.Holder || DogGear.IsJacket(i.Template))
                          || files.FileExists($"{Folder(pmc)}/K9Companion.json") || files.FileExists($"{Folder(pmc)}/K9Companion.needs.json");
            state = new K9State { Version = 0, Owned = legacy, OwnedBy = legacy ? "legacy" : "" };
            logger.Info(legacy ? "[K9Companion] Existing dog owner: your dog stays yours, and your kennel starts at level 1."
                               : "[K9Companion] New handler: Dr. Yang's questline leads to your first dog (Trust Is Earned).");
        }
        if (Migrate(state)) Save(pmc, state);
        return state;
    }

    // Versioned upgrades of the file. Version 0 = just created or written before versions existed.
    private static bool Migrate(K9State state)
    {
        if (state.Version >= K9State.CurrentVersion) { Sanitize(state); return false; }
        // (Future versions add their steps here, each moving Version up by one.)
        state.Version = K9State.CurrentVersion;
        Sanitize(state);
        return true;
    }

    // The collar's style (0.20: the three collar models; the old colours become the training collar, black stays black).
    private static readonly HashSet<string> Collars = new() { "training", "black", "buckle" };
    private static readonly HashSet<string> Tags = new() { "steel", "brass", "black", "gold" };

    public static void Sanitize(K9State state)
    {
        state.Ledger ??= new(); state.Contracts ??= new(); state.Presets ??= new(); state.Armour ??= new(); state.Raid ??= new(); state.CarriedOver ??= new();
        state.Raid.Seen ??= new();
        if (!Collars.Contains(state.Collar ?? "")) state.Collar = "training";
        if (!Tags.Contains(state.Tag ?? "")) state.Tag = "steel";
        state.Bed = Math.Clamp(state.Bed, 0, 2);
        state.Training = Math.Clamp(state.Training, 0, 3);
        state.Presets = state.Presets.Take(4).Select(p => IsId(p) ? p : "").ToList();
        foreach (var key in state.Armour.Keys.ToList())
        {
            ArmourRecord a = state.Armour[key];
            if (!IsId(key) || !(a.Max > 0) || double.IsNaN(a.Now) || double.IsInfinity(a.Max)) { state.Armour.Remove(key); continue; }
            a.Max = Math.Min(a.Max, 200); a.Now = Math.Clamp(a.Now, 0, a.Max);
        }
        foreach (var key in state.Ledger.Keys.ToList()) if (state.Ledger[key] < 0) state.Ledger[key] = 0;
    }

    public static bool IsId(string? v) => v != null && v.Length == 24 && v.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Save(PmcData pmc, K9State state)
    {
        string folder = Folder(pmc);
        if (!files.DirectoryExists(folder)) files.CreateDirectory(folder);
        files.WriteFile(PathFor(pmc), json.Serialize(state, true)!);
    }

    private void Backup(string path)
    {
        try { files.WriteFile(path + ".broken-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), files.ReadFile(path)); }
        catch { /* nothing more to do */ }
    }

    /// <summary>True when this profile has the dog (migrating it first if needed).</summary>
    public bool Owned(PmcData? pmc) => Load(pmc)?.Owned == true;
}

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

/// <summary>
/// The dog's hunger, thirst, health, hideout dish levels (0-100 each) and where its kennel stands in the hideout.
/// Values are read leniently: anything missing, of the wrong type, non-finite or out of range falls back to a
/// fed, healthy dog, empty dishes and "kennel not placed", so a bad request or a hand-edited file can't break anything.
/// </summary>
public sealed class NeedsData : IRequestData
{
    [JsonPropertyName("hunger")] public JsonElement? Hunger { get; set; }
    [JsonPropertyName("thirst")] public JsonElement? Thirst { get; set; }
    [JsonPropertyName("foodDish")] public JsonElement? FoodDish { get; set; }
    [JsonPropertyName("waterDish")] public JsonElement? WaterDish { get; set; }
    [JsonPropertyName("homeSet")] public JsonElement? HomeSet { get; set; }
    [JsonPropertyName("homeX")] public JsonElement? HomeX { get; set; }
    [JsonPropertyName("homeY")] public JsonElement? HomeY { get; set; }
    [JsonPropertyName("homeZ")] public JsonElement? HomeZ { get; set; }
    [JsonPropertyName("homeYaw")] public JsonElement? HomeYaw { get; set; }
    // The kennel was put there by hand (moved on the hideout's overview with Hideout Extensions), so it stays there.
    [JsonPropertyName("homeByHand")] public JsonElement? HomeByHand { get; set; }
    [JsonPropertyName("vestId")] public JsonElement? VestId { get; set; }
    // The dog's health between raids, 0-100 % (added in 0.16; a file without it means a healthy dog).
    [JsonPropertyName("health")] public JsonElement? Health { get; set; }

    public Dictionary<string, object> Validated()
    {
        double? x = Number(HomeX), y = Number(HomeY), z = Number(HomeZ), yaw = Number(HomeYaw);
        bool home = HomeSet?.ValueKind == JsonValueKind.True && x is { } && y is { } && z is { } && yaw is { }
                    && Math.Abs(x.Value) < 20000 && Math.Abs(y.Value) < 20000 && Math.Abs(z.Value) < 20000;
        return new Dictionary<string, object>
        {
            ["hunger"] = Percent(Hunger, 100), ["thirst"] = Percent(Thirst, 100),
            ["foodDish"] = Percent(FoodDish, 0), ["waterDish"] = Percent(WaterDish, 0),
            ["homeSet"] = home,
            ["homeX"] = home ? Math.Round(x!.Value, 3) : 0, ["homeY"] = home ? Math.Round(y!.Value, 3) : 0,
            ["homeZ"] = home ? Math.Round(z!.Value, 3) : 0, ["homeYaw"] = home ? Math.Round(((yaw!.Value % 360) + 360) % 360, 1) : 0,
            ["homeByHand"] = home && HomeByHand?.ValueKind == JsonValueKind.True,
            ["vestId"] = Vest(VestId),
            ["health"] = Percent(Health, 100)
        };
    }

    // "" (first bag), "none" (no vest) or a 24-character item id; anything else is reset to "".
    private static string Vest(JsonElement? e)
    {
        string v = e is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
        return v == "" || v == "none" || (v.Length == 24 && v.All(Uri.IsHexDigit) && v == v.ToLowerInvariant()) ? v : "";
    }

    private static double? Number(JsonElement? e)
        => e is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
    private static double Percent(JsonElement? e, double fallback) => Number(e) is double d ? Math.Round(Math.Clamp(d, 0, 100), 2) : fallback;
}

/// <summary>Stores the needs per PMC profile in user/profileData, next to the saddlebag record.</summary>
[Injectable(InjectionType.Singleton)]
public sealed class NeedsKeeper(ISptLogger<NeedsKeeper> logger, ProfileHelper profiles, FileUtil files, JsonUtil json)
{
    private string? PathFor(MongoId sessionId)
    {
        var pmc = profiles.GetPmcProfile(sessionId);
        return pmc == null ? null : $"user/profileData/{pmc.Id}/K9Companion.needs.json";
    }

    public string Load(MongoId sessionId)
    {
        NeedsData data = new();
        string? path = PathFor(sessionId);
        try { if (path != null && files.FileExists(path)) data = json.Deserialize<NeedsData>(files.ReadFile(path)) ?? new NeedsData(); }
        catch (Exception e) { logger.Warning($"[K9Companion] Couldn't read {path}, starting fresh: {e.Message}"); }
        return json.Serialize(data.Validated(), false)!;
    }

    public string Save(MongoId sessionId, NeedsData? data)
    {
        string? path = PathFor(sessionId);
        if (path == null || data == null) return "{\"ok\":false}";
        string folder = System.IO.Path.GetDirectoryName(path)!;
        if (!files.DirectoryExists(folder)) files.CreateDirectory(folder);
        files.WriteFile(path, json.Serialize(data.Validated(), true)!);
        return "{\"ok\":true}";
    }
}

[Injectable]
public sealed class NeedsRouter(JsonUtil json, NeedsKeeper keeper) : StaticRouter(json, [
    new RouteAction<BagEmptyRequest>("/k9companion/needs/load",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.Load(sessionId))),
    new RouteAction<NeedsData>("/k9companion/needs/save",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(keeper.Save(sessionId, info)))
]);

/// <summary>
/// The dog's two new foods: canned dog food and a pack of dog meat (raw meat for dogs). Both are clones of vanilla
/// rations; they are sold by Dr. Yang (see DrYang) and found in raids (DogFoodInRaids).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class DogFoodItems(CustomItemService items, TemplateTable templates, ISptLogger<DogFoodItems> logger) : IOnLoad
{
    public static readonly MongoId DogFood = new("6a5d0c0c4fb9a3c5e1d0d062");
    public static readonly MongoId DogMeat = new("6a5d0c0c4fb9a3c5e1d0d064");
    public const int DogFoodPrice = 4000, DogMeatPrice = 2500;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Create(new MongoId("57347d7224597744596b4e72"), DogFood, "k9companion_dog_food", DogFoodPrice,
            "Can of dog food", "Dog food", "Canned dog food (55% hunger for your dog). Feed it from the DOG group of the Y menu, or put it in the hideout food bowl. " +
            "Found wherever tinned beef is, and scavs carry it. You can eat it yourself, but it does you little good and leaves you thirsty.");
        Create(new MongoId("65815f0e647e3d7246384e14"), DogMeat, "k9companion_dog_meat", DogMeatPrice,
            "Pack of dog meat", "Dog meat", "Raw meat portions for dogs (40% hunger for your dog). Cheaper than canned food. " +
            "Found wherever dried beef is, and scavs carry it. People can eat it too, if they're desperate: very little energy, and a lot of thirst.");
        return Task.CompletedTask;
    }

    private void Create(MongoId clone, MongoId id, string name, int price, string full, string shortName, string description)
    {
        var result = items.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = clone, NewId = id, ParentId = templates.Items[clone].Parent, NewItemName = name,
            HandbookParentId = "5b47574386f77428ca22b336", HandbookPriceRoubles = price, FleaPriceRoubles = price,
            Locales = K9Lang.Locales(new LocaleDetails { Name = full, ShortName = shortName, Description = description })
        });
        if (!result.Success) logger.Error($"[K9Companion] Couldn't create {full}: " + string.Join("; ", result.Errors ?? new List<string>()));
    }
}
