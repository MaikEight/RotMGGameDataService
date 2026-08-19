namespace RotMGGameDataService.Configuration;

public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    public bool TrustForwardedHeaders { get; set; }
}
