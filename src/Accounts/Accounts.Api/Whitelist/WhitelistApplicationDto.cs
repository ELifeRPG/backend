namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record WhitelistApplicationDto
{
    public required Guid WhitelistApplicationId { get; init; }

    public required Guid AccountId { get; init; }

    public required string ServerClientId { get; init; }

    public required string ApplicationText { get; init; }

    public required string Status { get; init; }

    public static WhitelistApplicationDto Create(WhitelistApplication source) => new()
    {
        WhitelistApplicationId = source.Id.Value,
        AccountId = source.AccountId.Value,
        ServerClientId = source.ServerClientId,
        ApplicationText = source.ApplicationText,
        Status = source.Status.ToString(),
    };
}
