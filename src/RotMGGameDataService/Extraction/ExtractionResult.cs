using RotMGGameDataService.Data;

namespace RotMGGameDataService.Extraction;

public sealed record ExtractionResult(
    GameDataManifest Manifest,
    IReadOnlyDictionary<string, byte[]> Sprites,
    ExtractionProbeReport Report);
