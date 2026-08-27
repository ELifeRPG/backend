using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Banking.Domain.Events;
using ELifeRPG.Banking.Domain.Exceptions;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Banking.Application.Companies;

public union PurchaseCompanySharesResult(
    PurchaseCompanySharesResult.Purchased,
    PurchaseCompanySharesResult.BankAccountNotFound,
    PurchaseCompanySharesResult.CompanyNotFound,
    PurchaseCompanySharesResult.NotAuthorized,
    PurchaseCompanySharesResult.InsufficientBalance,
    PurchaseCompanySharesResult.InvalidQuantity,
    PurchaseCompanySharesResult.InvalidPrice)
{
    public record Purchased(int Quantity, decimal TotalPaid, decimal Fee, decimal NewBalance);

    public record BankAccountNotFound;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record InsufficientBalance;

    public record InvalidQuantity;

    public record InvalidPrice;
}

public sealed record PurchaseCompanySharesCommand(
    BankAccountId PayerBankAccountId,
    CharacterId Buyer,
    CompanyId CompanyId,
    int Quantity,
    decimal PricePerShare) : IRequest<PurchaseCompanySharesResult>;

public sealed class PurchaseCompanySharesHandler(
    ICrossModuleTransactionFactory transactionFactory,
    IBankAccountRepositoryFactory bankAccountRepositoryFactory,
    ICompanyRepositoryFactory companyRepositoryFactory,
    IMediator mediator)
    : IRequestHandler<PurchaseCompanySharesCommand, PurchaseCompanySharesResult>
{
    public async ValueTask<PurchaseCompanySharesResult> Handle(PurchaseCompanySharesCommand request, CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0)
        {
            return new PurchaseCompanySharesResult.InvalidQuantity();
        }

        if (request.PricePerShare <= 0)
        {
            return new PurchaseCompanySharesResult.InvalidPrice();
        }

        await using var transaction = await transactionFactory.BeginAsync(cancellationToken);

        // Repositories obtained from a cross-module transaction handle are intentionally never
        // disposed here — only `transaction` owns the underlying connection/transaction.
        var bankAccountRepository = bankAccountRepositoryFactory.CreateFor(transaction.Handle);
        var bankAccount = await bankAccountRepository.FetchForUpdateAsync(request.PayerBankAccountId, cancellationToken);
        if (bankAccount is null)
        {
            return new PurchaseCompanySharesResult.BankAccountNotFound();
        }

        var companyRepository = companyRepositoryFactory.CreateFor(transaction.Handle);
        var company = await companyRepository.FetchForUpdateAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new PurchaseCompanySharesResult.CompanyNotFound();
        }

        var isAuthorized = await BankAccountAuthorization.IsAuthorizedAsync(bankAccount, request.Buyer, mediator, cancellationToken);
        var totalPrice = request.Quantity * request.PricePerShare;

        BankAccountWithdrawn withdrawnEvent;
        try
        {
            withdrawnEvent = bankAccount.Withdraw(request.Buyer, isAuthorized, totalPrice);
        }
        catch (BankAccountAuthorizationException)
        {
            return new PurchaseCompanySharesResult.NotAuthorized();
        }
        catch (InsufficientBalanceException)
        {
            return new PurchaseCompanySharesResult.InsufficientBalance();
        }

        var issuedEvent = company.IssueShares(request.Buyer, request.Quantity);

        bankAccountRepository.Append(request.PayerBankAccountId, withdrawnEvent);
        companyRepository.Append(request.CompanyId, issuedEvent);

        await bankAccountRepository.SaveChangesAsync(cancellationToken);
        await companyRepository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PurchaseCompanySharesResult.Purchased(request.Quantity, totalPrice, withdrawnEvent.Fee, bankAccount.Balance);
    }
}
