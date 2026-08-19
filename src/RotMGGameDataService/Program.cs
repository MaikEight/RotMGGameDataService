using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using RotMGGameDataService.Api;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Data;
using RotMGGameDataService.Extraction;
using RotMGGameDataService.Persistence;
using RotMGGameDataService.Realm;
using RotMGGameDataService.Refresh;

var mode = args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal)
    ? args[0].ToLowerInvariant()
    : "serve";
var hostArgs = mode == "serve" ? args : args.Skip(1).ToArray();

if (mode == "healthcheck")
    return await RunHealthcheckAsync(args.Skip(1).FirstOrDefault());

if (mode is not ("serve" or "worker" or "refresh"))
{
    Console.Error.WriteLine("Usage: RotMGGameDataService [serve|worker|refresh|healthcheck <url>]");
    return 2;
}

var builder = WebApplication.CreateBuilder(hostArgs);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 16 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.AddRateLimiter(ConfigureRateLimiting);

builder.Services.AddOptions<RealmOptions>()
    .Bind(builder.Configuration.GetSection(RealmOptions.SectionName))
    .Validate(options => options.MaxCompressedBytes > 0 && options.MaxUncompressedBytes > 0)
    .ValidateOnStart();
builder.Services.AddOptions<ServiceOptions>()
    .Bind(builder.Configuration.GetSection(ServiceOptions.SectionName))
    .Validate(options => options.SchemaVersion > 0)
    .Validate(options => !string.IsNullOrWhiteSpace(options.ExtractorVersion))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RendererVersion))
    .ValidateOnStart();
builder.Services.AddOptions<UpdateOptions>()
    .Bind(builder.Configuration.GetSection(UpdateOptions.SectionName))
    .Validate(options => options.OfficialCheckCooldown > TimeSpan.Zero)
    .Validate(options => options.ScheduledCheckInterval > TimeSpan.Zero)
    .Validate(options => options.WorkerPollInterval >= TimeSpan.FromSeconds(1))
    .ValidateOnStart();
builder.Services.AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName));

builder.Services.AddHttpClient<RealmBuildClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(15);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
        "RotMGGameDataService",
        "1.0"));
});
builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("GameData");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("ConnectionStrings:GameData must be configured.");
    return new NpgsqlDataSourceBuilder(connectionString).Build();
});
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<GameDataStore>();
builder.Services.AddSingleton<BuildIdentity>();
builder.Services.AddSingleton<ExtractionProbe>();
builder.Services.AddTransient<RefreshCommand>();
builder.Services.AddTransient<WorkerCommand>();

await using var app = builder.Build();
var proxyOptions = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
if (proxyOptions.TrustForwardedHeaders)
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    forwardedOptions.KnownNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    await next();
});
app.UseResponseCompression();
app.UseRateLimiter();

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

try
{
    await app.Services.GetRequiredService<DatabaseInitializer>()
        .EnsureCreatedAsync(cancellationSource.Token);

    if (mode == "refresh")
    {
        return await app.Services.GetRequiredService<RefreshCommand>()
            .RunAsync(cancellationSource.Token);
    }

    if (mode == "worker")
    {
        await app.Services.GetRequiredService<WorkerCommand>()
            .RunAsync(cancellationSource.Token);
        return 0;
    }

    app.MapGameDataApi();
    await app.RunAsync(cancellationSource.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
{
    return 130;
}
catch (Exception exception)
{
    app.Logger.LogCritical(exception, "Service stopped due to an unrecoverable error");
    return 1;
}

static void ConfigureRateLimiting(RateLimiterOptions options)
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://httpstatuses.com/429",
            title = "Too many requests",
            status = StatusCodes.Status429TooManyRequests,
        }, cancellationToken);
    };
    options.AddPolicy(RateLimitPolicies.Metadata, context =>
        RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 180,
            TokensPerPeriod = 180,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));
    options.AddPolicy(RateLimitPolicies.UpdateHints, context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 6,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));
    options.AddPolicy(RateLimitPolicies.Sprites, context =>
        RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 600,
            TokensPerPeriod = 600,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));
}

static string ClientKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static async Task<int> RunHealthcheckAsync(string? url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        Console.Error.WriteLine("A valid HTTP healthcheck URL is required.");
        return 2;
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var response = await client.GetAsync(uri);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

public partial class Program;
