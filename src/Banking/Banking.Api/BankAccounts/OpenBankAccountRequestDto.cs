namespace ELifeRPG.Banking.Api.BankAccounts;

/// <summary>
/// Exactly one of CharacterId/CompanyId must be set — mirrors the legacy app's single open-account
/// endpoint accepting either a characterId or companyId (there it was a query parameter; here it's
/// the request body). The endpoint validates exactly-one-set before dispatching to
/// OpenBankAccountCommand (Personal) or OpenCorporateBankAccountCommand (Corporate).
/// </summary>
public sealed record OpenBankAccountRequestDto
{
    public Guid? CharacterId { get; init; }

    public Guid? CompanyId { get; init; }

    public OpenBankAccountCommand ToPersonalCommand(Guid bankId) => new(new BankId(bankId), new CharacterId(CharacterId!.Value));

    public OpenCorporateBankAccountCommand ToCorporateCommand(Guid bankId) => new(new BankId(bankId), new CompanyId(CompanyId!.Value));
}
