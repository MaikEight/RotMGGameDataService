using System.Text.Json;
using System.Text.RegularExpressions;
using RotMGGameDataService.Api;
using Xunit;

namespace RotMGGameDataService.Tests.Api;

public sealed class ServiceInfoTests
{
    /// <summary>
    /// The other EAM services build this timestamp with JavaScript's
    /// <c>Date.toISOString()</c>, so a probe reading all of them sees one
    /// format. .NET's own round-trip format is not that shape.
    /// </summary>
    [Fact]
    public void FormatTimestamp_MatchesJavaScriptToIsoString()
    {
        var value = new DateTimeOffset(2026, 8, 25, 14, 5, 9, 42, TimeSpan.Zero);

        Assert.Equal("2026-08-25T14:05:09.042Z", ServiceInfo.FormatTimestamp(value));
    }

    [Fact]
    public void FormatTimestamp_ConvertsToUtcRatherThanKeepingTheOffset()
    {
        var berlinAfternoon = new DateTimeOffset(2026, 8, 25, 16, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-08-25T14:00:00.000Z", ServiceInfo.FormatTimestamp(berlinAfternoon));
    }

    /// <summary>
    /// A deterministic build appends the commit to the informational version.
    /// Publishing that would make the field disagree with the image tag.
    /// </summary>
    [Fact]
    public void NormalizeVersion_DropsTheCommitADeterministicBuildAppends()
    {
        Assert.Equal(
            "1.2.0",
            ServiceInfo.NormalizeVersion("1.2.0+ffcf5029e2d3148c7ee110bf5070137d31b0ef0d", null));
        Assert.Equal("1.2.0", ServiceInfo.NormalizeVersion("1.2.0", null));
    }

    [Fact]
    public void NormalizeVersion_FallsBackToTheThreePartAssemblyVersion()
    {
        Assert.Equal("1.2.0", ServiceInfo.NormalizeVersion(null, new Version(1, 2, 0, 0)));
        Assert.Equal("1.2.0", ServiceInfo.NormalizeVersion("   ", new Version(1, 2, 0, 0)));
        Assert.Equal("0.0.0", ServiceInfo.NormalizeVersion(null, null));

        // "+sha" with nothing before it is not a version; fall back instead.
        Assert.Equal("1.2.0", ServiceInfo.NormalizeVersion("+abc", new Version(1, 2, 0, 0)));
    }

    /// <summary>
    /// Guards the wiring end to end: whatever the build stamped has to come out
    /// as the plain three-part number the release workflow tags the image with.
    /// </summary>
    [Fact]
    public void Describe_ReportsTheBuiltVersionWithoutBuildMetadata()
    {
        var version = new ServiceInfo().Describe().Version;

        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    [Fact]
    public void Describe_ReportsTheFixedIdentityFields()
    {
        var info = new ServiceInfo(
            new DateTimeOffset(2026, 8, 25, 14, 5, 9, 42, TimeSpan.Zero),
            typeof(ServiceInfo).Assembly).Describe();

        Assert.Equal("EAM Game Assets API", info.Name);
        Assert.Equal("TadusPro & MaikEight", info.Author);
        Assert.False(string.IsNullOrWhiteSpace(info.Description));
        Assert.Equal("2026-08-25T14:05:09.042Z", info.LastRestart);
    }

    /// <summary>
    /// The timestamp records the restart, so it must not move while the process
    /// is up.
    /// </summary>
    [Fact]
    public void Describe_KeepsTheSameRestartTimestampAcrossCalls()
    {
        var info = new ServiceInfo();

        Assert.Equal(info.Describe().LastRestart, info.Describe().LastRestart);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", info.Describe().LastRestart);
    }

    /// <summary>
    /// The other EAM services publish these exact property names, so the field
    /// names and their order are part of the contract.
    /// </summary>
    [Fact]
    public void ServiceInfoResponse_SerializesInTheSharedShape()
    {
        var info = new ServiceInfo(
            new DateTimeOffset(2026, 8, 25, 14, 5, 9, 42, TimeSpan.Zero),
            typeof(ServiceInfo).Assembly).Describe();

        var json = JsonSerializer.Serialize(info, ServiceInfo.JsonOptions);

        Assert.Equal(
            ["name", "version", "author", "description", "lastRestart"],
            Regex.Matches(json, @"""(\w+)"":").Select(match => match.Groups[1].Value));
        Assert.Contains(@"""name"":""EAM Game Assets API""", json, StringComparison.Ordinal);
        Assert.Contains(@"""lastRestart"":""2026-08-25T14:05:09.042Z""", json, StringComparison.Ordinal);

        // The ampersand stays literal, as it is in the other EAM services. The
        // default encoder would write \u0026 here.
        Assert.Contains(@"""author"":""TadusPro & MaikEight""", json, StringComparison.Ordinal);
    }
}
