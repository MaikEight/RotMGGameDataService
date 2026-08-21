using Npgsql;
using RotMGGameDataService.Data;

namespace RotMGGameDataService.Persistence;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
    private const long SchemaCreationLockId = 0x524F544D47444444;
    private const long PayloadHashRepairLockId = 0x524F544D47444853;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS game_data_builds (
            build_id char(64) PRIMARY KEY,
            realm_build_hash char(32) NOT NULL,
            source_checksum char(32) NOT NULL,
            schema_version integer NOT NULL,
            generated_at timestamptz NOT NULL,
            manifest_json bytea NOT NULL,
            manifest_hash char(64) NOT NULL,
            is_latest boolean NOT NULL DEFAULT false
        );

        CREATE UNIQUE INDEX IF NOT EXISTS game_data_builds_one_latest
            ON game_data_builds (is_latest)
            WHERE is_latest;
        CREATE INDEX IF NOT EXISTS game_data_builds_realm_hash
            ON game_data_builds (realm_build_hash, generated_at DESC);

        CREATE TABLE IF NOT EXISTS game_data_sprites (
            sprite_hash char(64) PRIMARY KEY,
            png_bytes bytea NOT NULL,
            width integer NOT NULL,
            height integer NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS game_data_build_sprites (
            build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            sprite_hash char(64) NOT NULL REFERENCES game_data_sprites(sprite_hash),
            PRIMARY KEY (build_id, sprite_hash)
        );

        CREATE TABLE IF NOT EXISTS game_data_build_diffs (
            from_build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            to_build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            diff_json bytea NOT NULL,
            diff_hash char(64) NOT NULL,
            PRIMARY KEY (from_build_id, to_build_id)
        );

        CREATE TABLE IF NOT EXISTS game_data_update_hints (
            observed_build_hash char(32) PRIMARY KEY,
            first_seen_at timestamptz NOT NULL,
            last_seen_at timestamptz NOT NULL,
            report_count bigint NOT NULL,
            processed_at timestamptz NULL
        );

        CREATE TABLE IF NOT EXISTS game_data_refresh_state (
            id smallint PRIMARY KEY CHECK (id = 1),
            last_checked_at timestamptz NULL,
            last_successful_at timestamptz NULL,
            last_official_check_at timestamptz NULL,
            pending_hint_at timestamptz NULL,
            last_error_at timestamptz NULL,
            last_error text NULL
        );

        INSERT INTO game_data_refresh_state (id) VALUES (1)
        ON CONFLICT (id) DO NOTHING;
        """;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 12;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await CreateSchemaAsync(cancellationToken);
                await RepairStoredPayloadHashesAsync(cancellationToken);
                logger.LogInformation("PostgreSQL schema is ready");
                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts
                && exception is NpgsqlException or TimeoutException)
            {
                logger.LogWarning(
                    exception,
                    "PostgreSQL is not ready (attempt {Attempt}/{MaximumAttempts})",
                    attempt,
                    maximumAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Creates the service's own tables if they are absent.
    /// </summary>
    /// <remarks>
    /// The statements only ever add objects, so they never affect tables owned
    /// by the other EAM APIs sharing this database. A transaction-scoped
    /// advisory lock serialises the DDL because <c>CREATE TABLE IF NOT
    /// EXISTS</c> is not atomic against a concurrent <c>CREATE</c>: without it,
    /// replicas starting at the same time can fail on the
    /// <c>pg_type_typname_nsp_index</c> unique constraint.
    /// </remarks>
    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock($1)",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue(SchemaCreationLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var schemaCommand = new NpgsqlCommand(SchemaSql, connection, transaction))
        {
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RepairStoredPayloadHashesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock($1)",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue(PayloadHashRepairLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var repairedManifests = await RepairManifestHashesAsync(
            connection,
            transaction,
            cancellationToken);
        var repairedDiffs = await RepairDiffHashesAsync(
            connection,
            transaction,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (repairedManifests > 0 || repairedDiffs > 0)
        {
            logger.LogInformation(
                "Repaired raw-byte hashes for {ManifestCount} manifests and {DiffCount} diffs",
                repairedManifests,
                repairedDiffs);
        }
    }

    private static async Task<int> RepairManifestHashesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var repairs = new List<(string BuildId, string Hash)>();
        await using (var selectCommand = new NpgsqlCommand(
                         "SELECT build_id, manifest_json, manifest_hash FROM game_data_builds",
                         connection,
                         transaction))
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bytes = reader.GetFieldValue<byte[]>(1);
                var hash = GameDataJson.HashBytes(bytes);
                if (!string.Equals(hash, reader.GetString(2).Trim(), StringComparison.Ordinal))
                    repairs.Add((reader.GetString(0).Trim(), hash));
            }
        }

        foreach (var repair in repairs)
        {
            await using var updateCommand = new NpgsqlCommand(
                "UPDATE game_data_builds SET manifest_hash = $1 WHERE build_id = $2",
                connection,
                transaction);
            updateCommand.Parameters.AddWithValue(repair.Hash);
            updateCommand.Parameters.AddWithValue(repair.BuildId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return repairs.Count;
    }

    private static async Task<int> RepairDiffHashesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var repairs = new List<(string FromBuildId, string ToBuildId, string Hash)>();
        await using (var selectCommand = new NpgsqlCommand(
                         "SELECT from_build_id, to_build_id, diff_json, diff_hash FROM game_data_build_diffs",
                         connection,
                         transaction))
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bytes = reader.GetFieldValue<byte[]>(2);
                var hash = GameDataJson.HashBytes(bytes);
                if (!string.Equals(hash, reader.GetString(3).Trim(), StringComparison.Ordinal))
                {
                    repairs.Add((
                        reader.GetString(0).Trim(),
                        reader.GetString(1).Trim(),
                        hash));
                }
            }
        }

        foreach (var repair in repairs)
        {
            await using var updateCommand = new NpgsqlCommand("""
                UPDATE game_data_build_diffs SET diff_hash = $1
                WHERE from_build_id = $2 AND to_build_id = $3
                """, connection, transaction);
            updateCommand.Parameters.AddWithValue(repair.Hash);
            updateCommand.Parameters.AddWithValue(repair.FromBuildId);
            updateCommand.Parameters.AddWithValue(repair.ToBuildId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return repairs.Count;
    }
}
