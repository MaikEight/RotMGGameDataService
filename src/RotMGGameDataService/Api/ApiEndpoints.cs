using RotMGGameDataService.Data;
using RotMGGameDataService.Persistence;

namespace RotMGGameDataService.Api;

public static class ApiEndpoints
{
    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static void MapGameDataApi(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            service = "RotMGGameDataService",
            status = "operational",
            apiVersion = 1,
            latest = "/api/v1/builds/latest",
            health = "/health/ready",
        })).RequireRateLimiting(RateLimitPolicies.Metadata);

        app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/health/ready", async (
            GameDataStore store,
            CancellationToken cancellationToken) =>
        {
            var ready = await store.CanConnectAsync(cancellationToken);
            return ready
                ? Results.Ok(new { status = "ready" })
                : Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Service is not ready");
        });

        var api = app.MapGroup("/api/v1");
        api.MapGet("/builds/latest", GetLatestAsync)
            .RequireRateLimiting(RateLimitPolicies.Metadata);
        api.MapGet("/builds/{buildIdentifier}/manifest", GetManifestAsync)
            .RequireRateLimiting(RateLimitPolicies.Metadata);
        api.MapGet("/builds/{toBuildIdentifier}/diff", GetDiffAsync)
            .RequireRateLimiting(RateLimitPolicies.Metadata);
        api.MapGet("/sprites/{spriteHash}.png", GetSpriteAsync)
            .RequireRateLimiting(RateLimitPolicies.Sprites);
        api.MapGet("/builds/{toBuildIdentifier}/sprites", GetSpriteBundleAsync)
            .RequireRateLimiting(RateLimitPolicies.SpriteBundles);
        api.MapPost("/update-hints", SubmitUpdateHintAsync)
            .RequireRateLimiting(RateLimitPolicies.UpdateHints);
        api.MapGet("/status", GetStatusAsync)
            .RequireRateLimiting(RateLimitPolicies.Metadata);
    }

    private static async Task<IResult> GetLatestAsync(
        HttpContext context,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        var build = await store.GetLatestBuildAsync(cancellationToken);
        if (build is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No game-data build has been published yet");
        }

        var response = new
        {
            schemaVersion = build.SchemaVersion,
            buildId = build.BuildId,
            realmBuildHash = build.RealmBuildHash,
            sourceChecksum = build.SourceChecksum,
            generatedAt = build.GeneratedAt,
            manifestUrl = $"/api/v1/builds/{build.BuildId}/manifest",
            manifestSha256 = build.ManifestHash,
        };
        var bytes = GameDataJson.Serialize(response);
        var etag = GameDataJson.HashBytes(bytes);
        return JsonPayload(context, bytes, etag, "public, max-age=300, stale-while-revalidate=300");
    }

    private static async Task<IResult> GetManifestAsync(
        string buildIdentifier,
        HttpContext context,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        if (!IsBuildIdentifier(buildIdentifier))
            return InvalidIdentifier("buildIdentifier");

        var payload = await store.GetManifestAsync(
            buildIdentifier.ToLowerInvariant(),
            cancellationToken);
        return payload is null
            ? Results.NotFound()
            : JsonPayload(context, payload.Bytes, payload.Hash, ImmutableCacheControl);
    }

    private static async Task<IResult> GetDiffAsync(
        string toBuildIdentifier,
        string? from,
        HttpContext context,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        if (!IsBuildIdentifier(toBuildIdentifier))
            return InvalidIdentifier("toBuildIdentifier");
        if (!IsBuildIdentifier(from))
            return InvalidIdentifier("from");

        var payload = await store.GetDiffAsync(
            from!.ToLowerInvariant(),
            toBuildIdentifier.ToLowerInvariant(),
            cancellationToken);
        return payload is null
            ? Results.NotFound()
            : JsonPayload(context, payload.Bytes, payload.Hash, ImmutableCacheControl);
    }

    private static async Task<IResult> GetSpriteAsync(
        string spriteHash,
        HttpContext context,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        if (!IsHex(spriteHash, 64))
            return InvalidIdentifier("spriteHash");

        var normalizedHash = spriteHash.ToLowerInvariant();
        var png = await store.GetSpriteAsync(normalizedHash, cancellationToken);
        if (png is null)
            return Results.NotFound();
        if (HasMatchingEtag(context, normalizedHash))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        SetCacheHeaders(context, normalizedHash, ImmutableCacheControl);
        return Results.Bytes(png, "image/png");
    }

    /// <summary>
    /// Streams every sprite for a build, or only those added since another
    /// build, as a single tar archive.
    /// </summary>
    /// <remarks>
    /// A cold consumer needs thousands of sprites at once, and requesting them
    /// individually costs far more in per-request overhead than the roughly
    /// 400 bytes each one contains. Entries are named by content hash, so a
    /// consumer verifies them exactly as it would a single sprite response.
    /// </remarks>
    private static async Task<IResult> GetSpriteBundleAsync(
        string toBuildIdentifier,
        string? from,
        HttpContext context,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        if (!IsBuildIdentifier(toBuildIdentifier))
            return InvalidIdentifier("toBuildIdentifier");
        if (from is not null && !IsBuildIdentifier(from))
            return InvalidIdentifier("from");

        var bundle = await store.ResolveSpriteBundleAsync(
            from?.ToLowerInvariant(),
            toBuildIdentifier.ToLowerInvariant(),
            cancellationToken);
        if (bundle is null)
            return Results.NotFound();

        if (HasMatchingEtag(context, bundle.ETag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        SetCacheHeaders(context, bundle.ETag, ImmutableCacheControl);
        return Results.Stream(
            stream => store.WriteSpriteBundleAsync(bundle, stream, cancellationToken),
            "application/x-tar");
    }

    private static async Task<IResult> SubmitUpdateHintAsync(
        UpdateHintRequest request,
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        if (!IsHex(request.ObservedBuildHash, 32))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["observedBuildHash"] =
                    ["The observed Realm build hash must contain exactly 32 hexadecimal characters."],
            });
        }

        var result = await store.RecordHintAsync(
            request.ObservedBuildHash.ToLowerInvariant(),
            cancellationToken);
        return Results.Accepted(value: new
        {
            status = "accepted",
            checkQueued = result.CheckQueued,
            alreadyCurrent = result.AlreadyCurrent,
        });
    }

    private static async Task<IResult> GetStatusAsync(
        GameDataStore store,
        CancellationToken cancellationToken)
    {
        var latest = await store.GetLatestBuildAsync(cancellationToken);
        var refresh = await store.GetRefreshStatusAsync(cancellationToken);
        return Results.Ok(new
        {
            latestBuildId = latest?.BuildId,
            latestRealmBuildHash = latest?.RealmBuildHash,
            refresh.LastCheckedAt,
            refresh.LastSuccessfulAt,
            refresh.LastOfficialCheckAt,
            updateHintPending = refresh.PendingHintAt is not null,
            refresh.LastErrorAt,
            hasRefreshError = refresh.LastError is not null,
        });
    }

    private static IResult JsonPayload(
        HttpContext context,
        byte[] bytes,
        string etag,
        string cacheControl)
    {
        if (HasMatchingEtag(context, etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        SetCacheHeaders(context, etag, cacheControl);
        return Results.Bytes(bytes, "application/json; charset=utf-8");
    }

    private static bool HasMatchingEtag(HttpContext context, string etag)
    {
        var quoted = $"\"{etag}\"";
        return context.Request.Headers.IfNoneMatch.Any(value =>
            string.Equals(value, quoted, StringComparison.Ordinal)
            || string.Equals(value, "*", StringComparison.Ordinal));
    }

    private static void SetCacheHeaders(
        HttpContext context,
        string etag,
        string cacheControl)
    {
        context.Response.Headers.ETag = $"\"{etag}\"";
        context.Response.Headers.CacheControl = cacheControl;
    }

    private static IResult InvalidIdentifier(string name) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [name] = ["The identifier has an invalid hexadecimal format."],
        });

    private static bool IsBuildIdentifier(string? value) =>
        IsHex(value, 32) || IsHex(value, 64);

    private static bool IsHex(string? value, int length)
    {
        if (value is null || value.Length != length)
            return false;
        return value.All(Uri.IsHexDigit);
    }
}
