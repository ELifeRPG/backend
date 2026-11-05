using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union BankAccountByNumberResult(
    BankAccountByNumberResult.Found,
    BankAccountByNumberResult.InvalidNumber,
    BankAccountByNumberResult.NotFound)
{
    public record Found(BankAccount BankAccount);

    /// <summary>Wrong layout or wrong check digits — rejected before any lookup.</summary>
    public record InvalidNumber;

    public record NotFound;
}

/// <summary>
/// Resolves an account number, as a player typed it, to the account. Transfers and every other
/// backend operation stay keyed on BankAccountId; the bridge uses this to turn the IBAN a player
/// enters into that id.
/// </summary>
public sealed record BankAccountByNumberQuery(string Number) : IRequest<BankAccountByNumberResult>;

public sealed class BankAccountByNumberHandler(IBankAccountRepository bankAccountRepository)
    : IRequestHandler<BankAccountByNumberQuery, BankAccountByNumberResult>
{
    public async ValueTask<BankAccountByNumberResult> Handle(BankAccountByNumberQuery request, CancellationToken cancellationToken)
    {
        if (!BankAccountNumber.TryNormalize(request.Number, out var number))
        {
            return new BankAccountByNumberResult.InvalidNumber();
        }

        var bankAccount = await bankAccountRepository.FindByNumberAsync(number, cancellationToken);
        return bankAccount is null
            ? new BankAccountByNumberResult.NotFound()
            : new BankAccountByNumberResult.Found(bankAccount);
    }
}
