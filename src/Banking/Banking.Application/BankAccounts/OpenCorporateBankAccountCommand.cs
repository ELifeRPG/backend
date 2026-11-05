using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Companies.Application.Companies;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union OpenCorporateBankAccountResult(
    OpenCorporateBankAccountResult.Opened,
    OpenCorporateBankAccountResult.BankNotFound,
    OpenCorporateBankAccountResult.CompanyNotFound,
    OpenCorporateBankAccountResult.NumberExhausted)
{
    public record Opened(BankAccountId BankAccountId, string Number);

    public record BankNotFound;

    public record CompanyNotFound;

    /// <summary>Every generated number collided. Practically unreachable; surfaced rather than swallowed.</summary>
    public record NumberExhausted;
}

public sealed record OpenCorporateBankAccountCommand(BankId BankId, CompanyId CompanyId) : IRequest<OpenCorporateBankAccountResult>;

public sealed class OpenCorporateBankAccountHandler(
    IBankRepository bankRepository,
    IBankAccountRepository bankAccountRepository,
    IMediator mediator) : IRequestHandler<OpenCorporateBankAccountCommand, OpenCorporateBankAccountResult>
{
    public async ValueTask<OpenCorporateBankAccountResult> Handle(OpenCorporateBankAccountCommand request, CancellationToken cancellationToken)
    {
        var bank = await bankRepository.FindByIdAsync(request.BankId, cancellationToken);
        if (bank is null)
        {
            return new OpenCorporateBankAccountResult.BankNotFound();
        }

        var companyLookup = await mediator.Send(new CompanyLookupQuery(request.CompanyId), cancellationToken);
        if (companyLookup is CompanyLookupResult.NotFound)
        {
            return new OpenCorporateBankAccountResult.CompanyNotFound();
        }

        for (var attempt = 0; attempt < BankAccountNumberGenerator.MaxAttempts; attempt++)
        {
            var bankAccountId = new BankAccountId(Guid.NewGuid());
            var number = BankAccountNumberGenerator.Generate(bank.Code);
            var domainEvent = new BankAccountOpened(
                bankAccountId,
                bank.Id,
                BankAccountType.Corporate,
                null,
                request.CompanyId,
                number,
                bank.TransactionFeeBase,
                bank.TransactionFeeMultiplier);
            var bankAccount = BankAccount.Create(domainEvent);

            bankAccountRepository.StartStream(bankAccount, domainEvent);

            try
            {
                await bankAccountRepository.SaveChangesAsync(cancellationToken);
                return new OpenCorporateBankAccountResult.Opened(bankAccountId, number);
            }
            catch (BankAccountNumberTakenException)
            {
                // The unique index on the number arbitrates; try again with a fresh one.
            }
        }

        return new OpenCorporateBankAccountResult.NumberExhausted();
    }
}
