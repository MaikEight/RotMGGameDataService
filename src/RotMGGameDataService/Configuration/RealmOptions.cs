namespace RotMGGameDataService.Configuration;

public sealed class RealmOptions
{
    public const string SectionName = "Realm";

    public required Uri AppInitUrl { get; init; }

    public string[] AllowedCdnHosts { get; init; } = [];

    public string ResourcePath { get; init; } = "RotMG Exalt_Data/resources.assets";

    public string WorkDirectory { get; init; } = "artifacts";

    public long MaxCompressedBytes { get; init; } = 128 * 1024 * 1024;

    public long MaxUncompressedBytes { get; init; } = 768 * 1024 * 1024;
}
