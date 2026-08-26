namespace ELifeRPG.Phone.Api.Sims;

public sealed record SimCardDto(
    Guid Id,
    string Number,
    Guid RegisteredTo,
    Guid? InstalledInDeviceId,
    string Status,
    IReadOnlyList<string> BlockedNumbers)
{
    public static SimCardDto Create(SimCard source) => new(
        source.Id.Value,
        source.Number.Value,
        source.RegisteredTo.Value,
        source.InstalledIn?.Value,
        source.Status.ToString(),
        [.. source.BlockedNumbers.Select(number => number.Value)]);
}

public sealed record ProvisionSimRequestDto(Guid CharacterId);

public sealed record ProvisionSimResponseDto(Guid SimCardId, string Number);

public sealed record BlockNumberRequestDto(Guid CharacterId, string Number);

/// <summary>Reason is recorded on the event so an enforcement action is auditable after the fact.</summary>
public sealed record SuspendSimRequestDto(string Reason);
