using System.Text.Json;
using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Extraction;
using RotMGGameDataService.Persistence;
using RotMGGameDataService.Realm;

namespace RotMGGameDataService.Refresh;

public sealed class RefreshCommand(
    RealmBuildClient realmBuildClient,
    ExtractionProbe extractionProbe,
    GameDataStore gameDataStore,
    IOptions<RealmOptions> realmOptions)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(cancellationToken);

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        }));
        return 0;
    }

    public async Task<RefreshOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        await using var lease = await gameDataStore.TryAcquireRefreshLeaseAsync(cancellationToken);
        if (lease is null)
            return new RefreshOutcome("busy", null, null, false, null);

        await gameDataStore.MarkOfficialCheckStartedAsync(cancellationToken);
        try
        {
            var build = await realmBuildClient.DiscoverAsync(cancellationToken);
            var latest = await gameDataStore.GetLatestBuildAsync(cancellationToken);
            if (latest is not null
                && string.Equals(
                    latest.SourceChecksum,
                    build.Resource.Checksum,
                    StringComparison.Ordinal))
            {
                await gameDataStore.MarkRefreshSuccessfulAsync(cancellationToken);
                return new RefreshOutcome(
                    "unchanged",
                    latest.BuildId,
                    build.BuildHash,
                    false,
                    null);
            }

            var resourcesPath = await realmBuildClient.EnsureResourceFileAsync(
                build,
                cancellationToken);
            var extraction = await extractionProbe.RunAsync(
                resourcesPath,
                build,
                realmOptions.Value.WorkDirectory,
                cancellationToken);
            var published = await gameDataStore.PublishAsync(extraction, cancellationToken);
            await gameDataStore.MarkRefreshSuccessfulAsync(cancellationToken);
            return new RefreshOutcome(
                published ? "published" : "already-published",
                extraction.Manifest.BuildId,
                build.BuildHash,
                published,
                extraction.Report);
        }
        catch (Exception exception)
        {
            try
            {
                await gameDataStore.MarkRefreshFailedAsync(exception, cancellationToken);
            }
            catch (Exception stateException)
            {
                Console.Error.WriteLine($"Could not record refresh failure: {stateException.Message}");
            }

            throw;
        }
    }
}

public sealed record RefreshOutcome(
    string Status,
    string? BuildId,
    string? RealmBuildHash,
    bool Published,
    ExtractionProbeReport? Report);
