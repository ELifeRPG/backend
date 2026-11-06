using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union TransferResult(
    TransferResult.Transferred,
    TransferResult.InvalidAmount,
    TransferResult.InvalidSource,
    TransferResult.SameAccount,
    TransferResult.BankAccountNotFound,
    TransferResult.TargetBankAccountNotFound,
    TransferResult.NotAuthorized,
    TransferResult.InsufficientBalance,
    TransferResult.ConcurrentModification)
{
    public record Transferred(BankTransactionId TransactionId, decimal Amount, decimal Fee, decimal NewBalance);

    /// <summary>Amount was not strictly positive or not in whole cents.</summary>
    public record InvalidAmount;

    /// <summary>Source was empty or longer than <see cref="Booking.MaxSourceLength"/> characters.</summary>
    public record InvalidSource;

    /// <summary>The target is the source account.</summary>
    public record SameAccount;

    public record BankAccountNotFound;

    public record TargetBankAccountNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;

    public record ConcurrentModification;
}

public sealed record TransferCommand(BankAccountId SourceBankAccountId, BankAccountId TargetBankAccountId, CharacterId CharacterId, decimal Amount, string? Source = null)
    : IRequest<TransferResult>;

public sealed class TransferHandler(IBankAccountRepository bankAccountRepository, IMediator mediator, ICurrentGameServer currentGameServer) : IRequestHandler<TransferCommand, TransferResult>
{
    public async ValueTask<TransferResult> Handle(TransferCommand request, CancellationToken cancellationToken)
    {
        if (!Money.IsValidAmount(request.Amount))
        {
            return new TransferResult.InvalidAmount();
        }

        if (!Booking.IsValidSource(request.Source))
        {
            return new TransferResult.InvalidSource();
        }

        if (request.SourceBankAccountId == request.TargetBankAccountId)
        {
            return new TransferResult.SameAccount();
        }

        var sourceAccount = await bankAccountRepository.FetchForUpdateAsync(request.SourceBankAccountId, cancellationToken);
        if (sourceAccount is null)
        {
            return new TransferResult.BankAccountNotFound();
        }

        var targetAccount = await bankAccountRepository.FetchForUpdateAsync(request.TargetBankAccountId, cancellationToken);
        if (targetAccount is null)
        {
            return new TransferResult.TargetBankAccountNotFound();
        }

        var booking = Booking.New(await currentGameServer.GetIdAsync(cancellationToken), request.Source);
        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(sourceAccount, request.CharacterId, mediator, cancellationToken);

        BankAccountTransferredOut outEvent;
        try
        {
            outEvent = sourceAccount.TransferOut(booking, request.CharacterId, isAuthorized, request.TargetBankAccountId, request.Amount);
        }
        catch (BankAccountAuthorizationException)
        {
            return new TransferResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new TransferResult.InsufficientBalance();
        }

        var inEvent = targetAccount.ReceiveTransfer(booking, request.SourceBankAccountId, request.Amount);

        bankAccountRepository.Append(request.SourceBankAccountId, outEvent);
        bankAccountRepository.Append(request.TargetBankAccountId, inEvent);

        try
        {
            await bankAccountRepository.SaveChangesAsync(cancellationToken);
        }
        catch (BankAccountConcurrencyException)
        {
            return new TransferResult.ConcurrentModification();
        }

        return new TransferResult.Transferred(booking.TransactionId, outEvent.Amount, outEvent.Fee, sourceAccount.Balance);
    }
}
