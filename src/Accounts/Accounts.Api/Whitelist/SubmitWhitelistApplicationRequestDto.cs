namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record SubmitWhitelistApplicationRequestDto
{
    // No AccountId: the account is derived from the caller's own token. Accepting one here is what
    // previously let any holder of the gameserver whitelist scope apply on someone else's behalf.
    public required string ApplicationText { get; init; }

    public SubmitWhitelistApplicationCommand ToCommand() => new(ApplicationText);
}
