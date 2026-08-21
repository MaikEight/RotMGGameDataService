using Npgsql;

namespace RotMGGameDataService.Persistence;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
    private const long SchemaCreationLockId = 0x524F544D47444444;

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

        CREATE TABLE IF NOT EXISTS game_data_migrations (
            name text PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 12;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await CreateSchemaAsync(cancellationToken);
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

        await ApplyPayloadHashRepairAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Rewrites payload hashes that were computed over the JSON encoding of the
    /// stored bytes rather than over the bytes themselves.
    /// </summary>
    /// <remarks>
    /// Databases written before the hashing fix carry values a consumer cannot
    /// verify, because it hashes the bytes it received. This runs once per
    /// database, recorded in <c>game_data_migrations</c>, rather than on every
    /// start: it used to read every manifest and diff blob into the process on
    /// each launch, which is a full scan of the largest tables repeated by every
    /// replica. PostgreSQL computes the hashes here, so no payload is
    /// transferred at all. Requires PostgreSQL 11 or newer for <c>sha256()</c>.
    /// </remarks>
    private async Task ApplyPayloadHashRepairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string migrationName = "payload-hash-repair";

        await using (var appliedCommand = new NpgsqlCommand(
                         "SELECT 1 FROM game_data_migrations WHERE name = $1",
                         connection,
                         transaction))
        {
            appliedCommand.Parameters.AddWithValue(migrationName);
            if (await appliedCommand.ExecuteScalarAsync(cancellationToken) is not null)
                return;
        }

        int repairedManifests;
        await using (var manifestCommand = new NpgsqlCommand("""
            UPDATE game_data_builds
            SET manifest_hash = encode(sha256(manifest_json), 'hex')
            WHERE manifest_hash IS DISTINCT FROM encode(sha256(manifest_json), 'hex')
            """, connection, transaction))
        {
            repairedManifests = await manifestCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int repairedDiffs;
        await using (var diffCommand = new NpgsqlCommand("""
            UPDATE game_data_build_diffs
            SET diff_hash = encode(sha256(diff_json), 'hex')
            WHERE diff_hash IS DISTINCT FROM encode(sha256(diff_json), 'hex')
            """, connection, transaction))
        {
            repairedDiffs = await diffCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var recordCommand = new NpgsqlCommand(
                         "INSERT INTO game_data_migrations (name) VALUES ($1)",
                         connection,
                         transaction))
        {
            recordCommand.Parameters.AddWithValue(migrationName);
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (repairedManifests > 0 || repairedDiffs > 0)
        {
            logger.LogInformation(
                "Repaired raw-byte hashes for {ManifestCount} manifests and {DiffCount} diffs",
                repairedManifests,
                repairedDiffs);
        }
    }
}
