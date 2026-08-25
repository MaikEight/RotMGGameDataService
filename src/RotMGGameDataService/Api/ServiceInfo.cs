using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RotMGGameDataService.Api;

/// <summary>
/// The service's identity, in the shape every EAM API publishes at
/// <c>/info</c>.
/// </summary>
/// <remarks>
/// The response is built once, when the instance is created during startup, so
/// <c>lastRestart</c> reports when this process came up rather than when the
/// route was called. Everything else is fixed for a given build.
/// </remarks>
public sealed class ServiceInfo
{
    public const string ServiceName = "EAM Game Assets API";
    public const string ServiceAuthor = "TadusPro & MaikEight";
    public const string ServiceDescription =
        "Publishes versioned Realm of the Mad God game data and rendered item "
        + "sprites extracted from the official client.";

    /// <summary>
    /// Serializer settings for this response alone.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes <c>&amp;</c> to <c>\u0026</c>, which is
    /// correct JSON but not what the other EAM services emit, and the author
    /// field contains one. Relaxed escaping is safe for this payload
    /// specifically: every field is a compile-time constant or a formatted
    /// timestamp, so nothing a caller controls can reach it.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ServiceInfoResponse _response;

    public ServiceInfo()
        : this(DateTimeOffset.UtcNow, typeof(ServiceInfo).Assembly)
    {
    }

    public ServiceInfo(DateTimeOffset startedAt, Assembly assembly)
    {
        _response = new ServiceInfoResponse(
            ServiceName,
            ResolveVersion(assembly),
            ServiceAuthor,
            ServiceDescription,
            FormatTimestamp(startedAt));
    }

    public ServiceInfoResponse Describe() => _response;

    /// <summary>
    /// Formats a timestamp the way the other EAM services do, which build
    /// theirs with JavaScript's <c>Date.toISOString()</c>: UTC, exactly three
    /// fractional digits, and a literal <c>Z</c>.
    /// </summary>
    /// <remarks>
    /// .NET's round-trip format writes seven fractional digits and a numeric
    /// offset, so it is spelled out here rather than left to the default.
    /// </remarks>
    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);

    internal static string ResolveVersion(Assembly assembly) => NormalizeVersion(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version);

    /// <summary>
    /// Reduces the assembly's version to the three-part number the release
    /// workflow tags the image with.
    /// </summary>
    /// <remarks>
    /// A deterministic build appends the commit to the informational version,
    /// as in <c>1.2.0+ffcf5029…</c>, so the build metadata is dropped. The
    /// assembly version is the fallback because it is always present, but it
    /// carries a fourth component that has to go.
    /// </remarks>
    internal static string NormalizeVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var buildMetadata = informationalVersion.IndexOf('+');
            var trimmed = buildMetadata < 0
                ? informationalVersion
                : informationalVersion[..buildMetadata];
            if (trimmed.Length > 0)
                return trimmed;
        }

        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}

public sealed record ServiceInfoResponse(
    string Name,
    string Version,
    string Author,
    string Description,
    string LastRestart);
