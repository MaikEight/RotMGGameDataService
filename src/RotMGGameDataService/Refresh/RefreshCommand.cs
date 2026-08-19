using System.Text.Json;
using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Extraction;
using RotMGGameDataService.Realm;

namespace RotMGGameDataService.Refresh;

public sealed class RefreshCommand(
    RealmBuildClient realmBuildClient,
    ExtractionProbe extractionProbe,
    IOptions<RealmOptions> realmOptions)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var build = await realmBuildClient.DiscoverAsync(cancellationToken);
        var resourcesPath = await realmBuildClient.EnsureResourceFileAsync(
            build,
            cancellationToken);
        var report = await extractionProbe.RunAsync(
            resourcesPath,
            build,
            realmOptions.Value.WorkDirectory,
            cancellationToken);

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        }));
        return 0;
    }
}
