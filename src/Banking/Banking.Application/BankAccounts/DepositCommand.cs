using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union DepositResult(DepositResult.Deposited, DepositResult.InvalidAmount, DepositResult.InvalidSource, DepositResult.MissingCharacter, DepositResult.BankAccountNotFound, DepositResult.ConcurrentModification, DepositResult.KeyReused)
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

    /// <summary>The Idempotency-Key was already used, within its retention, for a request with a different body.</summary>
    public record KeyReused;
}

/// <summary>
/// CharacterId is the character handing over the cash. It's recorded on the ledger, not checked against
/// the account's owner. With an IdempotencyKey, the same request sent again within
/// <see cref="BookingReceipt.Retention"/> returns the first answer and books nothing; see
/// <see cref="IdempotentBooking"/>.
/// </summary>
public sealed record DepositCommand(BankAccountId BankAccountId, CharacterId CharacterId, decimal Amount, string? Source = null, string? IdempotencyKey = null)
    : IRequest<DepositResult>;

public sealed class DepositHandler(IBankAccountRepository bankAccountRepository, ICurrentGameServer currentGameServer, TimeProvider timeProvider)
    : IRequestHandler<DepositCommand, DepositResult>
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

        var gameServerId = await currentGameServer.GetIdAsync(cancellationToken);
        var idempotent = IdempotentBooking.For(
            request.IdempotencyKey, gameServerId, "deposit",
            request.BankAccountId.Value, request.CharacterId.Value, request.Amount, request.Source);
        var expiredReceiptExists = false;
        if (idempotent is not null)
        {
            (var prior, expiredReceiptExists) = await idempotent.LookUpAsync(bankAccountRepository, timeProvider, cancellationToken);
            if (prior is not null)
            {
                return Replay(idempotent, prior);
            }
        }

        var bankAccount = await bankAccountRepository.FetchForUpdateAsync(request.BankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new DepositResult.BankAccountNotFound();
        }

        var booking = Booking.New(gameServerId, request.Source);
        var domainEvent = bankAccount.Deposit(booking, request.CharacterId, request.Amount);
        bankAccountRepository.Append(request.BankAccountId, domainEvent);
        idempotent?.Record(bankAccountRepository, timeProvider, expiredReceiptExists, booking.TransactionId, domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);

        try
        {
            await bankAccountRepository.SaveChangesAsync(cancellationToken);
        }
        catch (BankAccountConcurrencyException)
        {
            if (idempotent is not null
                && await idempotent.LookUpAsync(bankAccountRepository, timeProvider, cancellationToken) is ({ } racer, _))
            {
                return Replay(idempotent, racer);
            }

            return new DepositResult.ConcurrentModification();
        }

        return new DepositResult.Deposited(booking.TransactionId, domainEvent.Amount, domainEvent.Fee, bankAccount.Balance);
    }

    private static DepositResult Replay(IdempotentBooking idempotent, BookingReceipt receipt)
        => idempotent.Matches(receipt)
            ? new DepositResult.Deposited(receipt.TransactionId, receipt.Amount, receipt.Fee, receipt.NewBalance)
            : new DepositResult.KeyReused();
}
