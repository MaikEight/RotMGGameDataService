using RotMGGameDataService.Realm;
using System.Xml;
using Xunit;

namespace RotMGGameDataService.Tests.Realm;

public sealed class RealmBuildParserTests
{
    private static readonly string[] AllowedHosts = ["rotmg-build.decagames.com"];

    [Fact]
    public void ParseAppInit_ReturnsValidatedBuildMetadata()
    {
        var appInit = RealmBuildParser.ParseAppInit(
            ReadFixture("app-init.xml"),
            AllowedHosts);

        Assert.Equal("rotmg-exalt-win-64", appInit.BuildId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", appInit.BuildHash);
        Assert.Equal(
            "https://rotmg-build.decagames.com/build-release/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/rotmg-exalt-win-64/checksum.json",
            appInit.ChecksumUri.AbsoluteUri);
    }

    [Fact]
    public void ParseAppInit_RejectsUntrustedCdnHost()
    {
        var xml = ReadFixture("app-init.xml")
            .Replace("rotmg-build.decagames.com", "example.invalid", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() =>
            RealmBuildParser.ParseAppInit(xml, AllowedHosts));

        Assert.Contains("untrusted CDN host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAppInit_RejectsDocumentTypeDeclarations()
    {
        const string xml = """
            <!DOCTYPE AppSettings [<!ENTITY injected "value">]>
            <AppSettings>
              <BuildId>&injected;</BuildId>
              <BuildCDN>https://rotmg-build.decagames.com/build-release/</BuildCDN>
              <BuildHash>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</BuildHash>
            </AppSettings>
            """;

        Assert.Throws<XmlException>(() => RealmBuildParser.ParseAppInit(xml, AllowedHosts));
    }

    [Fact]
    public void ParseResourceFile_RequiresExactExpectedPath()
    {
        var appInit = RealmBuildParser.ParseAppInit(
            ReadFixture("app-init.xml"),
            AllowedHosts);

        var resource = RealmBuildParser.ParseResourceFile(
            ReadFixture("checksum.json"),
            appInit,
            "RotMG Exalt_Data/resources.assets",
            256 * 1024 * 1024,
            AllowedHosts);

        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resource.Checksum);
        Assert.Equal(47_184_550, resource.CompressedSize);
        Assert.Equal(
            "https://rotmg-build.decagames.com/build-release/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/rotmg-exalt-win-64/RotMG%20Exalt_Data/resources.assets.gz",
            resource.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public void ParseResourceFile_DoesNotAcceptSuffixMatches()
    {
        var appInit = RealmBuildParser.ParseAppInit(
            ReadFixture("app-init.xml"),
            AllowedHosts);

        var error = Assert.Throws<InvalidDataException>(() =>
            RealmBuildParser.ParseResourceFile(
                ReadFixture("checksum.json"),
                appInit,
                "resources.assets",
                256 * 1024 * 1024,
                AllowedHosts));

        Assert.Contains("0 exact matches", error.Message, StringComparison.Ordinal);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
