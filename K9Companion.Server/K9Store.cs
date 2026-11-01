using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

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
