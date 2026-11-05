namespace ELifeRPG.Banking.Api.BankAccounts;

/// <summary>
/// What resolving an account number reveals. Deliberately no balance and no owner: the bridge
/// relays this to whoever typed the number, which need not be the account holder.
/// </summary>
public sealed record BankAccountLookupDto
{
    public required Guid BankAccountId { get; init; }

    public required Guid BankId { get; init; }

    public required string Number { get; init; }

    public required string Type { get; init; }

    public static BankAccountLookupDto Create(BankAccount source) => new()
    {
        BankAccountId = source.Id.Value,
        BankId = source.BankId.Value,
        Number = source.Number,
        Type = source.Type.ToString(),
    };
}
