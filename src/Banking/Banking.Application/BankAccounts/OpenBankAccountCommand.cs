using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Characters.Application.Characters;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union OpenBankAccountResult(OpenBankAccountResult.Opened, OpenBankAccountResult.BankNotFound, OpenBankAccountResult.CharacterNotFound, OpenBankAccountResult.NumberExhausted)
{
    public record Opened(BankAccount BankAccount)
    {
        public BankAccountId BankAccountId => BankAccount.Id;

        public string Number => BankAccount.Number;
    }

    public record BankNotFound;

    public record CharacterNotFound;

    /// <summary>Every generated number collided. Practically unreachable; surfaced rather than swallowed.</summary>
    public record NumberExhausted;
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

        for (var attempt = 0; attempt < BankAccountNumberGenerator.MaxAttempts; attempt++)
        {
            var bankAccountId = new BankAccountId(Guid.NewGuid());
            var number = BankAccountNumberGenerator.Generate(bank.Code);
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

            try
            {
                await bankAccountRepository.SaveChangesAsync(cancellationToken);
                return new OpenBankAccountResult.Opened(bankAccount);
            }
            catch (BankAccountNumberTakenException)
            {
                // The unique index on the number arbitrates; try again with a fresh one.
            }
        }

        return new OpenBankAccountResult.NumberExhausted();
    }
}
