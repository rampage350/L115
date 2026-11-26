using SPTarkov.Server.Core.Models.Spt.Mod;

namespace L115AWM;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.l115awm.rifle";
    public string Name { get; init; } = "L115 AWM";
    public string Author { get; init; } = "L115";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? LoadBefore { get; init; }
    public List<string>? LoadAfter { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("*") }
    };
    public string? Url { get; init; }
    public bool? IsBundleMod { get; init; } = true;
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}
