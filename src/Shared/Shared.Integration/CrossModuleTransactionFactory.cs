using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Shared.Integration;

public sealed class CrossModuleTransactionFactory(string connectionString, IServiceProvider services) : ICrossModuleTransactionFactory
{
    public async Task<ICrossModuleTransaction> BeginAsync(CancellationToken cancellationToken)
        => await NpgsqlCrossModuleTransaction.BeginAsync(connectionString, services, cancellationToken);
}
