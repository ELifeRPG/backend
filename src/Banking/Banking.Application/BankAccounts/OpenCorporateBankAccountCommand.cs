using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Companies.Application.Companies;

namespace ELifeRPG.Banking.Application.BankAccounts;

public union OpenCorporateBankAccountResult(
    OpenCorporateBankAccountResult.Opened,
    OpenCorporateBankAccountResult.BankNotFound,
    OpenCorporateBankAccountResult.CompanyNotFound)
{
    public record Opened(BankAccountId BankAccountId, string Number);

    public record BankNotFound;

    public record CompanyNotFound;
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

        var bankAccountId = new BankAccountId(Guid.NewGuid());
        var number = BankAccountNumberGenerator.Generate();
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
        await bankAccountRepository.SaveChangesAsync(cancellationToken);

        return new OpenCorporateBankAccountResult.Opened(bankAccountId, number);
    }
}
