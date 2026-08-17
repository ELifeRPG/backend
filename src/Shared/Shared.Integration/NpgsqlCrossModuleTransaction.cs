using ELifeRPG.Shared.Integration.Abstractions;
using Npgsql;

namespace ELifeRPG.Shared.Integration;

internal sealed class NpgsqlCrossModuleTransaction : ICrossModuleTransaction
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private NpgsqlCrossModuleTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        Handle = new CrossModuleSessionHandle(transaction);
    }

    public static async Task<NpgsqlCrossModuleTransaction> BeginAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new NpgsqlCrossModuleTransaction(connection, transaction);
    }

    public CrossModuleSessionHandle Handle { get; }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
