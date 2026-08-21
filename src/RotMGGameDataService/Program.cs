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

// The other EAM APIs are configured with PORT, so the same variable is honoured
// here and wins when set. It cannot defer to ASPNETCORE_HTTP_PORTS, because the
// runtime image already defines that, which would leave PORT permanently inert.
var listenPort = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(listenPort))
{
    if (!int.TryParse(listenPort, out var listenPortNumber)
        || listenPortNumber is < 1 or > 65535)
    {
        Console.Error.WriteLine($"PORT must be a number between 1 and 65535, but was '{listenPort}'.");
        return 2;
    }

    builder.WebHost.UseUrls($"http://+:{listenPortNumber}");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 16 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

if (DatabaseConnection.IsSqlLoggingEnabled(builder.Configuration))
    builder.Logging.AddFilter("Npgsql", LogLevel.Information);

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
    // Sprite bundles are tar archives of many small PNGs. The PNG payloads are
    // already compressed, but tar pads every entry to a 512-byte boundary, and
    // compressing the archive removes that padding from the wire.
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/x-tar"]);
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
    return new NpgsqlDataSourceBuilder(DatabaseConnection.Resolve(configuration)).Build();
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
    // A consumer needs one bundle per build it has not seen, so a small budget
    // is generous. Each response is far larger than a single sprite, which is
    // the other reason to keep this tight.
    options.AddPolicy(RateLimitPolicies.SpriteBundles, context =>
        RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 12,
            TokensPerPeriod = 12,
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
