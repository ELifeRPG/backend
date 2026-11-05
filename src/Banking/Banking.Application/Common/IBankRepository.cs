namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Read-only: banks are precooked by the infrastructure's seed, and nothing in the game opens one.
/// </summary>
public interface IBankRepository
{
    ValueTask<Bank?> FindByIdAsync(BankId bankId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Bank>> FindAllAsync(CancellationToken cancellationToken);
}
