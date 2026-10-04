using Npgsql;

namespace YHDE.Server.Persistence;

// Connection factory for the PostgreSQL database.
// A single NpgsqlDataSource is registered as a singleton and shared across all
// repositories; Npgsql manages the connection pool internally.
public sealed class Database(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    // Open and return a new connection from the pool.
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = await DataSource.OpenConnectionAsync(ct);
        return conn;
    }
}
