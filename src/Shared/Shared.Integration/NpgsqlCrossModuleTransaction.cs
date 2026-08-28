using ELifeRPG.Shared.Integration.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ELifeRPG.Shared.Integration;

internal sealed class NpgsqlCrossModuleTransaction : ICrossModuleTransaction
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly IServiceProvider _services;

    private NpgsqlCrossModuleTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, IServiceProvider services)
    {
        _connection = connection;
        _transaction = transaction;
        _services = services;
        Handle = new CrossModuleSessionHandle(transaction);
    }

    public static async Task<NpgsqlCrossModuleTransaction> BeginAsync(
        string connectionString,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new NpgsqlCrossModuleTransaction(connection, transaction, services);
    }

    public CrossModuleSessionHandle Handle { get; }

    public TRepository Enlist<TRepository>() where TRepository : notnull
    {
        var participant = _services.GetService<ITransactionParticipant<TRepository>>()
            ?? throw new InvalidOperationException(
                $"No {nameof(ITransactionParticipant<TRepository>)}<{typeof(TRepository).Name}> is registered. " +
                "A repository can only take part in a cross-module transaction if its own module's " +
                "Infrastructure registered a participant for it.");

        return participant.EnlistIn(Handle);
    }

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
