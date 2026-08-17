namespace ELifeRPG.Shared.Integration.Abstractions;

public interface ICrossModuleTransactionFactory
{
    Task<ICrossModuleTransaction> BeginAsync(CancellationToken cancellationToken);
}
