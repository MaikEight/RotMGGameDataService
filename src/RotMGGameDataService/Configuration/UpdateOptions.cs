namespace RotMGGameDataService.Configuration;

public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    public TimeSpan OfficialCheckCooldown { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ScheduledCheckInterval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan WorkerPollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public bool CheckOnWorkerStartup { get; set; } = true;
}
