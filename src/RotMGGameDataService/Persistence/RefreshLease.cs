using Npgsql;

namespace RotMGGameDataService.Persistence;

public sealed class RefreshLease(NpgsqlConnection connection, long lockId) : IAsyncDisposable
{
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock($1)",
                connection);
            command.Parameters.AddWithValue(lockId);
            await command.ExecuteScalarAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
