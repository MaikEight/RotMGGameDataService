namespace RotMGGameDataService.Configuration;

public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public int SchemaVersion { get; set; } = 1;
    public string ExtractorVersion { get; set; } =
        "2cb715a74c34b823c3f6cde046af420131a5038d";
    public string RendererVersion { get; set; } = "eam-40px-v1";
}
