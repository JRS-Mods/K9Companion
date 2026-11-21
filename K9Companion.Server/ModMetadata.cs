using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace K9Companion.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "jrs.k9companion";
    public string Name { get; init; } = "K9Companion";
    public string Author { get; init; } = "JRS";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("1.5.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    // The dogs live in a room of Hideout Extensions' (hideoutextensions/k9_kennels.json).
    public Dictionary<string, Range>? ModDependencies { get; init; } = new() { ["jrs.hideoutextensions"] = new Range(">=1.3.0") };
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";
}
