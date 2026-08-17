using ELifeRPG.Banking.Domain.Events;

namespace ELifeRPG.Banking.Application.Common;

public interface IBankRepository
{
    ValueTask<Bank?> FindByIdAsync(BankId bankId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Bank>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(Bank bank, BankOpened domainEvent);

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
