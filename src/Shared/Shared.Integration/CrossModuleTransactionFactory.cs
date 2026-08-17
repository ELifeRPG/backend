using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Shared.Integration;

public sealed class CrossModuleTransactionFactory(string connectionString) : ICrossModuleTransactionFactory
{
    public async Task<ICrossModuleTransaction> BeginAsync(CancellationToken cancellationToken)
        => await NpgsqlCrossModuleTransaction.BeginAsync(connectionString, cancellationToken);
}
