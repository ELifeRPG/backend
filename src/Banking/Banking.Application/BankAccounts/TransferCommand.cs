using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union TransferResult(
    TransferResult.Transferred,
    TransferResult.BankAccountNotFound,
    TransferResult.TargetBankAccountNotFound,
    TransferResult.NotAuthorized,
    TransferResult.InsufficientBalance)
{
    public record Transferred(decimal Amount, decimal Fee, decimal NewBalance);

    public record BankAccountNotFound;

    public record TargetBankAccountNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;
}

public sealed record TransferCommand(BankAccountId SourceBankAccountId, BankAccountId TargetBankAccountId, CharacterId CharacterId, decimal Amount)
    : IRequest<TransferResult>;

public sealed class TransferHandler(IBankAccountRepository bankAccountRepository, IMediator mediator) : IRequestHandler<TransferCommand, TransferResult>
{
    public async ValueTask<TransferResult> Handle(TransferCommand request, CancellationToken cancellationToken)
    {
        var sourceAccount = await bankAccountRepository.FindByIdAsync(request.SourceBankAccountId, cancellationToken);
        if (sourceAccount is null)
        {
            return new TransferResult.BankAccountNotFound();
        }

        var targetAccount = await bankAccountRepository.FindByIdAsync(request.TargetBankAccountId, cancellationToken);
        if (targetAccount is null)
        {
            return new TransferResult.TargetBankAccountNotFound();
        }

        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(sourceAccount, request.CharacterId, mediator, cancellationToken);

        BankAccountTransferredOut outEvent;
        try
        {
            outEvent = sourceAccount.TransferOut(request.CharacterId, isAuthorized, request.TargetBankAccountId, request.Amount);
        }
        catch (BankAccountAuthorizationException)
        {
            return new TransferResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new TransferResult.InsufficientBalance();
        }

        var inEvent = targetAccount.ReceiveTransfer(request.SourceBankAccountId, request.Amount);

        // Both appends land in the same Marten session (MartenBankAccountRepository owns one session
        // for its whole lifetime) — SaveChangesAsync below commits both streams atomically. Verified
        // against live Postgres; see ARCHITECTURE.md §9e.
        bankAccountRepository.Append(request.SourceBankAccountId, outEvent);
        bankAccountRepository.Append(request.TargetBankAccountId, inEvent);
        await bankAccountRepository.SaveChangesAsync(cancellationToken);

        return new TransferResult.Transferred(outEvent.Amount, outEvent.Fee, sourceAccount.Balance);
    }
}
