using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;

namespace ELifeRPG.Banking.Application.Banks;

/// <summary>
/// Only ever one materially different outcome, so this is a plain record, not a union — see the
/// "when to use union" convention in ARCHITECTURE.md §9e.
/// </summary>
public sealed record OpenBankResult(BankId Id);

public sealed record OpenBankCommand(string Name, decimal TransactionFeeBase, decimal TransactionFeeMultiplier) : IRequest<OpenBankResult>;

public sealed class OpenBankHandler(IBankRepository bankRepository) : IRequestHandler<OpenBankCommand, OpenBankResult>
{
    public async ValueTask<OpenBankResult> Handle(OpenBankCommand request, CancellationToken cancellationToken)
    {
        var bankId = new BankId(Guid.NewGuid());
        var domainEvent = new BankOpened(bankId, request.Name, request.TransactionFeeBase, request.TransactionFeeMultiplier);
        var bank = Bank.Create(domainEvent);

        bankRepository.StartStream(bank, domainEvent);
        await bankRepository.SaveChangesAsync(cancellationToken);

        return new OpenBankResult(bankId);
    }
}
