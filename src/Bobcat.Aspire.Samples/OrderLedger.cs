using Npgsql;

namespace Bobcat.Aspire.Samples;

/// <summary>
/// The system under test: a little Postgres-backed ledger that knows nothing about Aspire, Docker or
/// Bobcat. It is handed a whole connection string, the way an application reads
/// <c>ConnectionStrings:orders</c> from its configuration.
/// </summary>
public sealed class OrderLedger(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task Reset()
    {
        await using var command = _dataSource.CreateCommand(
            "create table if not exists orders (customer text not null, total numeric not null); truncate orders;");
        await command.ExecuteNonQueryAsync();
    }

    public async Task Record(string customer, decimal total)
    {
        await using var command = _dataSource.CreateCommand("insert into orders (customer, total) values ($1, $2)");
        command.Parameters.AddWithValue(customer);
        command.Parameters.AddWithValue(total);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<decimal> TotalFor(string customer)
    {
        await using var command = _dataSource.CreateCommand("select coalesce(sum(total), 0) from orders where customer = $1");
        command.Parameters.AddWithValue(customer);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
