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
    string VisualCatalogHash,
    string PngCatalogHash,
    string OutputDirectory);
