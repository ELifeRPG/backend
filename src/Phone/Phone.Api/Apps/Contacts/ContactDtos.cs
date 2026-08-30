using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Api.Apps.Contacts;

public sealed record ContactDto(Guid Id, string Number, string DisplayName)
{
    public static ContactDto Create(Contact source) => new(source.Id.Value, source.Number.Value, source.DisplayName);
}

public sealed record SaveContactRequestDto(string Number, string DisplayName);

public sealed record RenameContactRequestDto(string DisplayName);

/// <inheritdoc cref="ELifeRPG.Phone.Api.Devices.PhoneAppDto"/>
public sealed record SaveContactResponseDto(Guid ContactId);
