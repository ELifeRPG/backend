using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Api.Apps.Contacts;

public sealed record ContactDto(Guid Id, string Number, string DisplayName)
{
    public static ContactDto Create(Contact source) => new(source.Id.Value, source.Number.Value, source.DisplayName);
}

public sealed record SaveContactRequestDto(Guid CharacterId, string Number, string DisplayName, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

public sealed record RenameContactRequestDto(Guid CharacterId, string DisplayName, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}
