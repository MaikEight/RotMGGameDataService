namespace RotMGGameDataService.Extraction;

public sealed record ExtractionProbeReport(
    string RealmBuildHash,
    string SourceChecksum,
    DateTimeOffset GeneratedAt,
    TimeSpan Duration,
    long PeakWorkingSetBytes,
    IReadOnlyDictionary<string, int> CategoryCounts,
    int TotalModelCount,
    int RenderableObjectCount,
    int DuplicateObjectIdCount,
    int MissingTextureCount,
    int FailedImageCount,
    int UniqueVisualCount,
    int UniquePngCount,
    long UniquePngBytes,
    // Fame bonuses are read from the client XML rather than from the extractor's
    // models, so these counts are the only signal that the read still works.
    // Build 974bde45c06b313b1e425bc2cb222c75 produced 612 and 817.
    int FameBonusCount,
    int FameConditionCount,
    string VisualCatalogHash,
    string PngCatalogHash,
    string OutputDirectory);
