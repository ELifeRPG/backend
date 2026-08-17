namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record SubmitWhitelistApplicationRequestDto
{
    public required Guid AccountId { get; init; }

    public required string ApplicationText { get; init; }

    public SubmitWhitelistApplicationCommand ToCommand(string serverClientId) =>
        new(new AccountId(AccountId), serverClientId, ApplicationText);
}
