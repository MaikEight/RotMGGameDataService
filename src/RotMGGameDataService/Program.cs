using System.Net.Http.Headers;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Extraction;
using RotMGGameDataService.Realm;
using RotMGGameDataService.Refresh;

var mode = args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal)
    ? args[0].ToLowerInvariant()
    : "serve";
var hostArgs = mode == "serve" ? args : args.Skip(1).ToArray();

if (mode is not ("serve" or "refresh"))
{
    Console.Error.WriteLine("Usage: RotMGGameDataService [serve|refresh]");
    return 2;
}

var builder = WebApplication.CreateBuilder(hostArgs);
builder.Services
    .AddOptions<RealmOptions>()
    .Bind(builder.Configuration.GetSection(RealmOptions.SectionName));
builder.Services.AddHttpClient<RealmBuildClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(15);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
        "RotMGGameDataService",
        "0.1"));
});
builder.Services.AddSingleton<ExtractionProbe>();
builder.Services.AddTransient<RefreshCommand>();

await using var app = builder.Build();
if (mode == "refresh")
{
    using var cancellationSource = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };

    try
    {
        return await app.Services
            .GetRequiredService<RefreshCommand>()
            .RunAsync(cancellationSource.Token);
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
        Console.Error.WriteLine("Refresh cancelled.");
        return 130;
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Refresh failed");
        return 1;
    }
}

app.MapGet("/", () => Results.Ok(new
{
    service = "RotMGGameDataService",
    status = "proof-of-concept",
}));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));

await app.RunAsync();
return 0;

public partial class Program;
