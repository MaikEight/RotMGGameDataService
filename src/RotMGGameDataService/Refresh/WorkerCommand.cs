using Microsoft.Extensions.Options;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Persistence;

namespace RotMGGameDataService.Refresh;

public sealed class WorkerCommand(
    RefreshCommand refreshCommand,
    GameDataStore gameDataStore,
    IOptions<UpdateOptions> options,
    ILogger<WorkerCommand> logger)
{
    private readonly UpdateOptions _options = options.Value;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Updater worker started; scheduled interval {ScheduledInterval}, hint poll {PollInterval}",
            _options.ScheduledCheckInterval,
            _options.WorkerPollInterval);
        var firstIteration = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var latest = await gameDataStore.GetLatestBuildAsync(cancellationToken);
                var status = await gameDataStore.GetRefreshStatusAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var scheduledDue = status.LastCheckedAt is null
                    || now - status.LastCheckedAt >= _options.ScheduledCheckInterval;
                var hintDue = status.PendingHintAt is not null
                    && (status.LastOfficialCheckAt is null
                        || now - status.LastOfficialCheckAt >= _options.OfficialCheckCooldown);
                var startupDue = firstIteration
                    && _options.CheckOnWorkerStartup
                    && latest is null;

                if (scheduledDue || hintDue || startupDue)
                {
                    var result = await refreshCommand.ExecuteAsync(cancellationToken);
                    logger.LogInformation(
                        "Updater check finished with status {RefreshStatus} and build {BuildId}",
                        result.Status,
                        result.BuildId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Updater check failed; the last published build remains active");
            }

            firstIteration = false;
            await Task.Delay(_options.WorkerPollInterval, cancellationToken);
        }
    }
}
