using System.Data;
using Microsoft.Data.Sqlite;

namespace Meduza.Infrastructure.Data;

public class SqliteConnectionFactory : IDbConnectionFactory, IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _anchorConnection;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
    }

    public IDbConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    public void Dispose()
    {
        _anchorConnection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return _anchorConnection.DisposeAsync();
    }
}
