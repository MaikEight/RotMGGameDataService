using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Data;
using Xunit;

namespace RotMGGameDataService.Tests.Data;

public sealed class BuildIdentityTests
{
    [Fact]
    public void Calculate_IsDeterministicAndIncludesRendererVersion()
    {
        var first = Create("renderer-1").Calculate("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var repeated = Create("renderer-1").Calculate("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var changed = Create("renderer-2").Calculate("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
        Assert.Equal(64, first.Length);
    }

    private static BuildIdentity Create(string rendererVersion) => new(Options.Create(
        new ServiceOptions
        {
            SchemaVersion = 1,
            ExtractorVersion = "extractor-commit",
            RendererVersion = rendererVersion,
        }));
}
