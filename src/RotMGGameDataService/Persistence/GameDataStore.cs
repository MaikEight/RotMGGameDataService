using Microsoft.Extensions.Options;
using Npgsql;
using RotMGGameDataService.Configuration;
using RotMGGameDataService.Data;
using RotMGGameDataService.Extraction;

namespace RotMGGameDataService.Persistence;

public sealed class GameDataStore(
    NpgsqlDataSource dataSource,
    IOptions<UpdateOptions> updateOptions,
    ILogger<GameDataStore> logger)
{
    private const long RefreshAdvisoryLockId = 0x524F544D47444154;
    private readonly UpdateOptions _updateOptions = updateOptions.Value;

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogWarning(exception, "PostgreSQL readiness check failed");
            return false;
        }
    }

    public async Task<RefreshLease?> TryAcquireRefreshLeaseAsync(
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock($1)",
                connection);
            command.Parameters.AddWithValue(RefreshAdvisoryLockId);
            var acquired = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new RefreshLease(connection, RefreshAdvisoryLockId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<PublishedBuild?> GetLatestBuildAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT build_id, realm_build_hash, source_checksum, schema_version,
                   generated_at, manifest_hash
            FROM game_data_builds
            WHERE is_latest
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBuild(reader) : null;
    }

    public async Task<PublishedBuild?> GetBuildAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT build_id, realm_build_hash, source_checksum, schema_version,
                   generated_at, manifest_hash
            FROM game_data_builds
            WHERE build_id = $1 OR realm_build_hash = $1
            ORDER BY is_latest DESC, generated_at DESC
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(identifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBuild(reader) : null;
    }

    public async Task<StoredPayload?> GetManifestAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT manifest_json, manifest_hash
            FROM game_data_builds
            WHERE build_id = $1 OR realm_build_hash = $1
            ORDER BY is_latest DESC, generated_at DESC
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(identifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredPayload(reader.GetFieldValue<byte[]>(0), reader.GetString(1).Trim())
            : null;
    }

    public async Task<StoredPayload?> GetDiffAsync(
        string fromIdentifier,
        string toIdentifier,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH from_build AS (
                SELECT build_id FROM game_data_builds
                WHERE build_id = $1 OR realm_build_hash = $1
                ORDER BY is_latest DESC, generated_at DESC LIMIT 1
            ), to_build AS (
                SELECT build_id FROM game_data_builds
                WHERE build_id = $2 OR realm_build_hash = $2
                ORDER BY is_latest DESC, generated_at DESC LIMIT 1
            )
            SELECT diff_json, diff_hash
            FROM build_diffs
            WHERE from_build_id = (SELECT build_id FROM from_build)
              AND to_build_id = (SELECT build_id FROM to_build)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(fromIdentifier);
        command.Parameters.AddWithValue(toIdentifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredPayload(reader.GetFieldValue<byte[]>(0), reader.GetString(1).Trim())
            : null;
    }

    public async Task<byte[]?> GetSpriteAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT png_bytes FROM sprites WHERE sprite_hash = $1");
        command.Parameters.AddWithValue(hash);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    public async Task<bool> PublishAsync(
        ExtractionResult extraction,
        CancellationToken cancellationToken)
    {
        ValidateExtraction(extraction);
        var manifestBytes = GameDataJson.Serialize(extraction.Manifest);
        var manifestHash = GameDataJson.Hash(manifestBytes);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existingHash = await GetExistingManifestHashAsync(
            connection,
            transaction,
            extraction.Manifest.BuildId,
            cancellationToken);
        if (existingHash is not null)
        {
            if (!string.Equals(existingHash, manifestHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The deterministic build ID already exists with different manifest bytes.");
            }

            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var previous = await GetLatestManifestForUpdateAsync(
            connection,
            transaction,
            cancellationToken);
        await InsertSpritesAsync(connection, transaction, extraction.Sprites, cancellationToken);
        await InsertBuildAsync(
            connection,
            transaction,
            extraction.Manifest,
            manifestBytes,
            manifestHash,
            cancellationToken);
        await InsertBuildSpritesAsync(
            connection,
            transaction,
            extraction.Manifest.BuildId,
            extraction.Sprites.Keys,
            cancellationToken);

        if (previous is not null)
        {
            var diff = GameDataDiffBuilder.Create(previous, extraction.Manifest);
            await InsertDiffAsync(connection, transaction, diff, cancellationToken);
        }

        await using (var latestCommand = new NpgsqlCommand("""
            UPDATE game_data_builds SET is_latest = false WHERE is_latest;
            UPDATE game_data_builds SET is_latest = true WHERE build_id = $1;
            """, connection, transaction))
        {
            latestCommand.Parameters.AddWithValue(extraction.Manifest.BuildId);
            await latestCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Published build {BuildId} with manifest {ManifestHash}",
            extraction.Manifest.BuildId,
            manifestHash);
        return true;
    }

    public async Task<HintResult> RecordHintAsync(
        string observedBuildHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? latestRealmHash;
        await using (var latestCommand = new NpgsqlCommand(
                         "SELECT realm_build_hash FROM game_data_builds WHERE is_latest",
                         connection,
                         transaction))
        {
            latestRealmHash = (await latestCommand.ExecuteScalarAsync(cancellationToken) as string)?.Trim();
        }

        if (string.Equals(latestRealmHash, observedBuildHash, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new HintResult(false, true);
        }

        var alreadyPending = false;
        await using (var pendingCommand = new NpgsqlCommand(
                         "SELECT processed_at IS NULL FROM update_hints WHERE observed_build_hash = $1",
                         connection,
                         transaction))
        {
            pendingCommand.Parameters.AddWithValue(observedBuildHash);
            var value = await pendingCommand.ExecuteScalarAsync(cancellationToken);
            alreadyPending = value is bool pending && pending;
        }

        var now = DateTime.UtcNow;
        await using (var hintCommand = new NpgsqlCommand("""
            INSERT INTO update_hints (
                observed_build_hash, first_seen_at, last_seen_at, report_count, processed_at)
            VALUES ($1, $2, $2, 1, NULL)
            ON CONFLICT (observed_build_hash) DO UPDATE SET
                last_seen_at = EXCLUDED.last_seen_at,
                report_count = update_hints.report_count + 1,
                processed_at = NULL;

            UPDATE refresh_state
            SET pending_hint_at = COALESCE(pending_hint_at, $2)
            WHERE id = 1;

            SELECT pg_notify('rotmg_update_hints', $1);
            """, connection, transaction))
        {
            hintCommand.Parameters.AddWithValue(observedBuildHash);
            hintCommand.Parameters.AddWithValue(now);
            await hintCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTime? lastOfficialCheck;
        await using (var stateCommand = new NpgsqlCommand(
                         "SELECT last_official_check_at FROM refresh_state WHERE id = 1",
                         connection,
                         transaction))
        {
            var value = await stateCommand.ExecuteScalarAsync(cancellationToken);
            lastOfficialCheck = value is DateTime dateTime ? dateTime : null;
        }

        await transaction.CommitAsync(cancellationToken);
        var cooldownElapsed = lastOfficialCheck is null
                              || DateTime.UtcNow - DateTime.SpecifyKind(
                                  lastOfficialCheck.Value,
                                  DateTimeKind.Utc) >= _updateOptions.OfficialCheckCooldown;
        return new HintResult(!alreadyPending && cooldownElapsed, false);
    }

    public async Task<RefreshStatus> GetRefreshStatusAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT last_checked_at, last_successful_at, last_official_check_at,
                   pending_hint_at, last_error_at, last_error
            FROM refresh_state WHERE id = 1
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new RefreshStatus(null, null, null, null, null, null);

        return new RefreshStatus(
            ReadNullableTimestamp(reader, 0),
            ReadNullableTimestamp(reader, 1),
            ReadNullableTimestamp(reader, 2),
            ReadNullableTimestamp(reader, 3),
            ReadNullableTimestamp(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task MarkOfficialCheckStartedAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE refresh_state
            SET last_checked_at = now(), last_official_check_at = now()
            WHERE id = 1
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRefreshSuccessfulAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE refresh_state
            SET last_successful_at = now(), pending_hint_at = NULL,
                last_error_at = NULL, last_error = NULL
            WHERE id = 1;

            UPDATE update_hints SET processed_at = now() WHERE processed_at IS NULL;
            DELETE FROM update_hints WHERE processed_at < now() - interval '7 days';
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkRefreshFailedAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var message = exception.Message;
        if (message.Length > 2_048)
            message = message[..2_048];

        await using var command = dataSource.CreateCommand("""
            UPDATE refresh_state
            SET last_error_at = now(), last_error = $1
            WHERE id = 1
            """);
        command.Parameters.AddWithValue(message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateExtraction(ExtractionResult extraction)
    {
        if (extraction.Manifest.Objects.Count < 10_000)
            throw new InvalidDataException("Extraction produced fewer than 10,000 renderable objects.");
        if (extraction.Manifest.PlayerStats.Count < 50)
            throw new InvalidDataException("Extraction produced fewer than 50 player-stat definitions.");
        if (extraction.Manifest.FameBonuses.Count < 100)
            throw new InvalidDataException("Extraction produced fewer than 100 fame bonuses.");
        if (extraction.Report.FailedImageCount != 0)
            throw new InvalidDataException("Extraction reported failed sprite images.");
        if (extraction.Report.MissingTextureCount > 100)
            throw new InvalidDataException("Extraction reported an implausible number of missing textures.");
        if (extraction.Sprites.Count != extraction.Report.UniquePngCount)
            throw new InvalidDataException("The unique sprite count did not match the extraction report.");
    }

    private static async Task<string?> GetExistingManifestHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string buildId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT manifest_hash FROM game_data_builds WHERE build_id = $1",
            connection,
            transaction);
        command.Parameters.AddWithValue(buildId);
        return (await command.ExecuteScalarAsync(cancellationToken) as string)?.Trim();
    }

    private static async Task<GameDataManifest?> GetLatestManifestForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT manifest_json FROM game_data_builds WHERE is_latest FOR UPDATE",
            connection,
            transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is byte[] bytes ? GameDataJson.Deserialize<GameDataManifest>(bytes) : null;
    }

    private static async Task InsertSpritesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, byte[]> sprites,
        CancellationToken cancellationToken)
    {
        var hashes = sprites.Keys.ToArray();
        var bytes = hashes.Select(hash => sprites[hash]).ToArray();
        await using var command = new NpgsqlCommand("""
            INSERT INTO sprites (sprite_hash, png_bytes, width, height)
            SELECT value_hash, value_bytes, 40, 40
            FROM unnest($1::text[], $2::bytea[]) AS value(value_hash, value_bytes)
            ON CONFLICT (sprite_hash) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue(hashes);
        command.Parameters.AddWithValue(bytes);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertBuildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GameDataManifest manifest,
        byte[] manifestBytes,
        string manifestHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO game_data_builds (
                build_id, realm_build_hash, source_checksum, schema_version,
                generated_at, manifest_json, manifest_hash, is_latest)
            VALUES ($1, $2, $3, $4, $5, $6, $7, false)
            """, connection, transaction);
        command.Parameters.AddWithValue(manifest.BuildId);
        command.Parameters.AddWithValue(manifest.RealmBuildHash);
        command.Parameters.AddWithValue(manifest.SourceChecksum);
        command.Parameters.AddWithValue(manifest.SchemaVersion);
        command.Parameters.AddWithValue(manifest.GeneratedAt.UtcDateTime);
        command.Parameters.AddWithValue(manifestBytes);
        command.Parameters.AddWithValue(manifestHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertBuildSpritesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string buildId,
        IEnumerable<string> spriteHashes,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO build_sprites (build_id, sprite_hash)
            SELECT $1, value_hash FROM unnest($2::text[]) AS value(value_hash)
            """, connection, transaction);
        command.Parameters.AddWithValue(buildId);
        command.Parameters.AddWithValue(spriteHashes.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDiffAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GameDataDiff diff,
        CancellationToken cancellationToken)
    {
        var bytes = GameDataJson.Serialize(diff);
        await using var command = new NpgsqlCommand("""
            INSERT INTO build_diffs (
                from_build_id, to_build_id, diff_json, diff_hash)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (from_build_id, to_build_id) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue(diff.FromBuildId);
        command.Parameters.AddWithValue(diff.ToBuildId);
        command.Parameters.AddWithValue(bytes);
        command.Parameters.AddWithValue(GameDataJson.Hash(bytes));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PublishedBuild ReadBuild(NpgsqlDataReader reader) => new(
        reader.GetString(0).Trim(),
        reader.GetString(1).Trim(),
        reader.GetString(2).Trim(),
        reader.GetInt32(3),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
        reader.GetString(5).Trim());

    private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
