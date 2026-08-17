using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Characters.Application.Characters;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union OpenBankAccountResult(OpenBankAccountResult.Opened, OpenBankAccountResult.BankNotFound, OpenBankAccountResult.CharacterNotFound)
{
    public record Opened(BankAccountId BankAccountId, string Number);

    public record BankNotFound;

    public record CharacterNotFound;
}

public sealed record OpenBankAccountCommand(BankId BankId, CharacterId CharacterId) : IRequest<OpenBankAccountResult>;

public sealed class OpenBankAccountHandler(
    IBankRepository bankRepository,
    IBankAccountRepository bankAccountRepository,
    IMediator mediator) : IRequestHandler<OpenBankAccountCommand, OpenBankAccountResult>
{
    public async ValueTask<OpenBankAccountResult> Handle(OpenBankAccountCommand request, CancellationToken cancellationToken)
    {
        var bank = await bankRepository.FindByIdAsync(request.BankId, cancellationToken);
        if (bank is null)
        {
            return new OpenBankAccountResult.BankNotFound();
        }

        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.CharacterId), cancellationToken);
        if (characterLookup is CharacterLookupResult.NotFound)
        {
            return new OpenBankAccountResult.CharacterNotFound();
        }

        var bankAccountId = new BankAccountId(Guid.NewGuid());
        var number = BankAccountNumberGenerator.Generate();
        var domainEvent = new BankAccountOpened(
            bankAccountId,
            bank.Id,
            BankAccountType.Personal,
            request.CharacterId,
            null,
            number,
            bank.TransactionFeeBase,
            bank.TransactionFeeMultiplier);
        var bankAccount = BankAccount.Create(domainEvent);

        bankAccountRepository.StartStream(bankAccount, domainEvent);
        await bankAccountRepository.SaveChangesAsync(cancellationToken);

        return new OpenBankAccountResult.Opened(bankAccountId, number);
    }
}
