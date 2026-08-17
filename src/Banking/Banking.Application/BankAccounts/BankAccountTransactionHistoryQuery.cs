using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union BankAccountTransactionHistoryResult(BankAccountTransactionHistoryResult.Found, BankAccountTransactionHistoryResult.BankAccountNotFound)
{
    public record Found(IReadOnlyList<BankAccountTransactionRecord> Transactions);

    public record BankAccountNotFound;
}

/// <summary>Matches legacy's own hardcoded cap (BankAccountDetailsQuery took the 30 most recent bookings).</summary>
public sealed record BankAccountTransactionHistoryQuery(BankAccountId BankAccountId) : IRequest<BankAccountTransactionHistoryResult>
{
    public const int HistoryLimit = 30;
}

public sealed class BankAccountTransactionHistoryHandler(IBankAccountRepository bankAccountRepository)
    : IRequestHandler<BankAccountTransactionHistoryQuery, BankAccountTransactionHistoryResult>
{
    public async ValueTask<BankAccountTransactionHistoryResult> Handle(BankAccountTransactionHistoryQuery request, CancellationToken cancellationToken)
    {
        var bankAccount = await bankAccountRepository.FindByIdAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new BankAccountTransactionHistoryResult.BankAccountNotFound();
        }

        var history = await bankAccountRepository.GetHistoryAsync(
            request.BankAccountId,
            BankAccountTransactionHistoryQuery.HistoryLimit,
            cancellationToken);

        return new BankAccountTransactionHistoryResult.Found(history);
    }
}
