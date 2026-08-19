using Npgsql;

namespace RotMGGameDataService.Persistence;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
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

        CREATE TABLE IF NOT EXISTS sprites (
            sprite_hash char(64) PRIMARY KEY,
            png_bytes bytea NOT NULL,
            width integer NOT NULL,
            height integer NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS build_sprites (
            build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            sprite_hash char(64) NOT NULL REFERENCES sprites(sprite_hash),
            PRIMARY KEY (build_id, sprite_hash)
        );

        CREATE TABLE IF NOT EXISTS build_diffs (
            from_build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            to_build_id char(64) NOT NULL REFERENCES game_data_builds(build_id) ON DELETE CASCADE,
            diff_json bytea NOT NULL,
            diff_hash char(64) NOT NULL,
            PRIMARY KEY (from_build_id, to_build_id)
        );

        CREATE TABLE IF NOT EXISTS update_hints (
            observed_build_hash char(32) PRIMARY KEY,
            first_seen_at timestamptz NOT NULL,
            last_seen_at timestamptz NOT NULL,
            report_count bigint NOT NULL,
            processed_at timestamptz NULL
        );

        CREATE TABLE IF NOT EXISTS refresh_state (
            id smallint PRIMARY KEY CHECK (id = 1),
            last_checked_at timestamptz NULL,
            last_successful_at timestamptz NULL,
            last_official_check_at timestamptz NULL,
            pending_hint_at timestamptz NULL,
            last_error_at timestamptz NULL,
            last_error text NULL
        );

        INSERT INTO refresh_state (id) VALUES (1)
        ON CONFLICT (id) DO NOTHING;
        """;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 12;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await using var command = dataSource.CreateCommand(SchemaSql);
                await command.ExecuteNonQueryAsync(cancellationToken);
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
}
