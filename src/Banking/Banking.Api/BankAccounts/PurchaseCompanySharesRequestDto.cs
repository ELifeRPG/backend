using ELifeRPG.Banking.Application.Companies;

namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record PurchaseCompanySharesRequestDto
{
    public required Guid CharacterId { get; init; }

    public required Guid CompanyId { get; init; }

    public required int Quantity { get; init; }

    public required decimal PricePerShare { get; init; }

    public PurchaseCompanySharesCommand ToCommand(Guid bankAccountId) =>
        new(new BankAccountId(bankAccountId), new CharacterId(CharacterId), new CompanyId(CompanyId), Quantity, PricePerShare);
}
