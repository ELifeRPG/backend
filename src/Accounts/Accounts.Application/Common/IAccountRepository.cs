using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IAccountRepository
{
    ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<Account?> FindByBohemiaIdAsync(GameId bohemiaId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Account>> SearchAsync(string search, CancellationToken cancellationToken);

    void StartStream(Account account, AccountCreated domainEvent);

    void Append<TEvent>(AccountId accountId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
