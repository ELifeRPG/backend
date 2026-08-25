namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record CurrentAccountDto
{
    public required Guid AccountId { get; init; }

    /// <summary>True only on the call that actually created the account — useful for first-run onboarding.</summary>
    public required bool Created { get; init; }
}
