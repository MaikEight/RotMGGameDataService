using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RotMGGameDataService.Realm;

internal static partial class RealmBuildParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
    };

    public static RealmAppInit ParseAppInit(string xml, IReadOnlyCollection<string> allowedCdnHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        if (allowedCdnHosts.Count == 0)
            throw new InvalidDataException("At least one Realm CDN host must be configured.");

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        var document = XDocument.Load(xmlReader, LoadOptions.None);

        var buildId = GetRequiredElement(document, "BuildId");
        var buildHash = GetRequiredElement(document, "BuildHash").ToLowerInvariant();
        var buildCdnText = GetRequiredElement(document, "BuildCDN");

        if (!BuildIdPattern().IsMatch(buildId))
            throw new InvalidDataException("Realm returned an invalid build ID.");
        if (!Md5Pattern().IsMatch(buildHash))
            throw new InvalidDataException("Realm returned an invalid build hash.");
        if (!Uri.TryCreate(buildCdnText, UriKind.Absolute, out var buildCdn)
            || buildCdn.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Realm returned an invalid build CDN URL.");
        }

        EnsureAllowedHost(buildCdn, allowedCdnHosts);
        buildCdn = EnsureTrailingSlash(buildCdn);

        var checksumUri = new Uri(
            buildCdn,
            $"{buildHash}/{Uri.EscapeDataString(buildId)}/checksum.json");
        EnsureAllowedHost(checksumUri, allowedCdnHosts);

        return new RealmAppInit(buildId, buildHash, buildCdn, checksumUri);
    }

    public static RealmResourceFile ParseResourceFile(
        string json,
        RealmAppInit appInit,
        string expectedResourcePath,
        long maxCompressedBytes,
        IReadOnlyCollection<string> allowedCdnHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedResourcePath);

        var response = JsonSerializer.Deserialize<ChecksumResponse>(json, JsonOptions)
            ?? throw new InvalidDataException("Realm returned an empty checksum document.");
        var normalizedExpectedPath = expectedResourcePath.Replace('\\', '/');
        var matches = response.Files
            .Where(file => string.Equals(
                file.File.Replace('\\', '/'),
                normalizedExpectedPath,
                StringComparison.Ordinal))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Realm checksum data contained {matches.Length} exact matches for '{normalizedExpectedPath}'.");
        }

        var match = matches[0];
        var checksum = match.Checksum.ToLowerInvariant();
        if (!Md5Pattern().IsMatch(checksum))
            throw new InvalidDataException("Realm returned an invalid resources.assets checksum.");
        if (match.Size <= 0 || match.Size > maxCompressedBytes)
            throw new InvalidDataException("Realm returned an invalid compressed resources.assets size.");

        var escapedPath = string.Join(
            '/',
            normalizedExpectedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        var downloadUri = new Uri(
            appInit.BuildCdn,
            $"{appInit.BuildHash}/{Uri.EscapeDataString(appInit.BuildId)}/{escapedPath}.gz");
        EnsureAllowedHost(downloadUri, allowedCdnHosts);

        return new RealmResourceFile(
            normalizedExpectedPath,
            checksum,
            match.Size,
            downloadUri);
    }

    private static string GetRequiredElement(XContainer document, string localName)
    {
        var value = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)
            ?.Value
            .Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Realm app-init data did not contain {localName}.")
            : value;
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/");

    private static void EnsureAllowedHost(Uri uri, IReadOnlyCollection<string> allowedHosts)
    {
        if (!allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Realm returned untrusted CDN host '{uri.IdnHost}'.");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdPattern();

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Md5Pattern();

    private sealed class ChecksumResponse
    {
        [JsonPropertyName("files")]
        public ChecksumFile[] Files { get; init; } = [];
    }

    private sealed class ChecksumFile
    {
        [JsonPropertyName("file")]
        public string File { get; init; } = string.Empty;

        [JsonPropertyName("checksum")]
        public string Checksum { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
