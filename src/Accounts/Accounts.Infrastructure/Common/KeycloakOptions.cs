namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class KeycloakOptions
{
    public required string BaseUrl { get; init; }

    public required string Realm { get; init; }

    public required string ProvisioningClientId { get; init; }

    public required string ProvisioningClientSecret { get; init; }
}
