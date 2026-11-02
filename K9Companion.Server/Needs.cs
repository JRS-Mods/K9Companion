using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

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
/// rations; they are sold by Dr. Yang (see DrYang.cs) and found in raids (DogFoodInRaids).
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
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = full, ShortName = shortName, Description = description } }
        });
        if (!result.Success) logger.Error($"[K9Companion] Couldn't create {full}: " + string.Join("; ", result.Errors ?? new List<string>()));
    }
}
