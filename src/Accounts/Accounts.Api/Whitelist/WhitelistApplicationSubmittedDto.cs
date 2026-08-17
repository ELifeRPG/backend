namespace ELifeRPG.Accounts.Api.Whitelist;

public sealed record WhitelistApplicationSubmittedDto
{
    public required Guid WhitelistApplicationId { get; init; }

    public required string Status { get; init; }

    public static WhitelistApplicationSubmittedDto Create(SubmitWhitelistApplicationResult.Submitted source) => new()
    {
        WhitelistApplicationId = source.WhitelistApplicationId.Value,
        Status = nameof(WhitelistApplicationStatus.Open),
    };
}
