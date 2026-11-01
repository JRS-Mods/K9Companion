using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
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
