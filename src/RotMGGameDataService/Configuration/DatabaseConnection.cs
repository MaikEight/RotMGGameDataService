using Npgsql;

namespace RotMGGameDataService.Configuration;

/// <summary>
/// Resolves the PostgreSQL connection string.
/// </summary>
/// <remarks>
/// The service shares one PostgreSQL instance with the other EAM APIs, which
/// receive their credentials as individual <c>DATABASE_*</c> environment
/// variables injected by the cluster. The TLS behaviour mirrors
/// <c>eam-api-commons/src/db/connection.js</c>: transport encryption is
/// required unless <c>DEBUG_MODE</c> is set, and the server certificate is not
/// verified.
/// </remarks>
public static class DatabaseConnection
{
    private const int MaximumPoolSize = 5;

    public static string Resolve(IConfiguration configuration)
    {
        // An explicit connection string stays supported for local development
        // and for the Compose stack, which runs its own PostgreSQL container.
        var explicitConnectionString = configuration.GetConnectionString("GameData");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
            return explicitConnectionString;

        var host = configuration["DATABASE_HOST"];
        var port = configuration["DATABASE_PORT"];
        var database = configuration["DATABASE_NAME"];
        var username = configuration["DATABASE_USERNAME"];
        var password = configuration["DATABASE_PASSWORD"];

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(host)) missing.Add("DATABASE_HOST");
        if (string.IsNullOrWhiteSpace(database)) missing.Add("DATABASE_NAME");
        if (string.IsNullOrWhiteSpace(username)) missing.Add("DATABASE_USERNAME");
        if (string.IsNullOrWhiteSpace(password)) missing.Add("DATABASE_PASSWORD");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The database connection is not configured. Set ConnectionStrings:GameData, "
                + $"or provide the missing environment variable(s): {string.Join(", ", missing)}.");
        }

        if (!int.TryParse(port, out var portNumber))
            portNumber = 5432;

        // A builder is used rather than string concatenation so passwords
        // containing ';' or '=' are escaped correctly.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = portNumber,
            Database = database,
            Username = username,
            Password = password,
            Pooling = true,
            // The instance is shared with every other EAM API, so each replica
            // keeps a deliberately small pool.
            MaxPoolSize = MaximumPoolSize,
            Timeout = 10,
            CommandTimeout = 30,
        };

        // SslMode.Require encrypts the connection without validating the server
        // certificate, which is what rejectUnauthorized: false gives the Node
        // services. VerifyCA or VerifyFull would be needed to validate it, and
        // TrustServerCertificate is obsolete in Npgsql 8 because Require already
        // implies it.
        builder.SslMode = IsDebugMode(configuration) ? SslMode.Disable : SslMode.Require;

        return builder.ConnectionString;
    }

    public static bool IsDebugMode(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["DEBUG_MODE"]);

    public static bool IsSqlLoggingEnabled(IConfiguration configuration) =>
        string.Equals(configuration["DATABASE_LOGGING"], "true", StringComparison.OrdinalIgnoreCase);
}
