using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union DepositResult(DepositResult.Deposited, DepositResult.InvalidAmount, DepositResult.InvalidSource, DepositResult.MissingCharacter, DepositResult.BankAccountNotFound, DepositResult.ConcurrentModification)
{
    public record Deposited(BankTransactionId TransactionId, decimal Amount, decimal Fee, decimal NewBalance);

    /// <summary>Amount was not strictly positive or not in whole cents.</summary>
    public record InvalidAmount;

    /// <summary>Source was empty or longer than <see cref="Booking.MaxSourceLength"/> characters.</summary>
    public record InvalidSource;

    /// <summary>CharacterId was empty, e.g. left out of the request body, which binds it to <see cref="Guid.Empty"/>.</summary>
    public record MissingCharacter;

    public record BankAccountNotFound;

    public record ConcurrentModification;
}

/// <summary>CharacterId is the character handing over the cash. It's recorded on the ledger, not checked against the account's owner.</summary>
public sealed record DepositCommand(BankAccountId BankAccountId, CharacterId CharacterId, decimal Amount, string? Source = null) : IRequest<DepositResult>;

public sealed class DepositHandler(IBankAccountRepository bankAccountRepository, ICurrentGameServer currentGameServer) : IRequestHandler<DepositCommand, DepositResult>
{
    public async ValueTask<DepositResult> Handle(DepositCommand request, CancellationToken cancellationToken)
    {
        if (!Money.IsValidAmount(request.Amount))
        {
            return new DepositResult.InvalidAmount();
        }

        if (!Booking.IsValidSource(request.Source))
        {
            return new DepositResult.InvalidSource();
        }

        if (request.CharacterId.Value == Guid.Empty)
        {
            return new DepositResult.MissingCharacter();
        }

        var bankAccount = await bankAccountRepository.FetchForUpdateAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new DepositResult.BankAccountNotFound();
        }

        var booking = Booking.New(await currentGameServer.GetIdAsync(cancellationToken), request.Source);
        var domainEvent = bankAccount.Deposit(booking, request.CharacterId, request.Amount);
        bankAccountRepository.Append(request.BankAccountId, domainEvent);

        try
        {
            await bankAccountRepository.SaveChangesAsync(cancellationToken);
        }
        catch (BankAccountConcurrencyException)
        {
            return new DepositResult.ConcurrentModification();
        }

        return new DepositResult.Deposited(booking.TransactionId, domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }
}
