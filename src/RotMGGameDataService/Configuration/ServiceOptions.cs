namespace RotMGGameDataService.Configuration;

public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public int SchemaVersion { get; set; } = 1;
    public string ExtractorVersion { get; set; } =
        "2cb715a74c34b823c3f6cde046af420131a5038d";
    public string RendererVersion { get; set; } = "eam-40px-v1";

    /// <summary>
    /// How many builds to keep. Zero retains everything.
    /// </summary>
    /// <remarks>
    /// Each build costs its manifest plus whatever sprites it introduced, and
    /// sprites are shared, so the marginal cost of a build is small but unbounded
    /// over time. Retained builds are what the diff route can serve from: a
    /// consumer older than the window falls back to a full manifest, which is
    /// correct but larger.
    /// </remarks>
    public int RetainedBuildCount { get; set; } = 10;
}
