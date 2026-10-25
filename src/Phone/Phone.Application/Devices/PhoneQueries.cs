using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

/// <summary>
/// A phone with its preferences, which live in their own document — every read that hands a phone
/// to a client carries both, so restoring one after a game server restart is a single call.
/// </summary>
public sealed record PhoneDetails(PhoneDevice Phone, PhonePreferences Preferences);

public union PhoneDeviceLookupResult(PhoneDeviceLookupResult.Found, PhoneDeviceLookupResult.NotFound)
{
    public record Found(PhoneDevice Phone, PhonePreferences Preferences);

    public record NotFound;
}

public sealed record PhoneDeviceLookupQuery(PhoneDeviceId PhoneId) : IRequest<PhoneDeviceLookupResult>;

public sealed class PhoneDeviceLookupHandler(IPhoneDeviceRepository phoneRepository, IPhonePreferencesRepository preferencesRepository)
    : IRequestHandler<PhoneDeviceLookupQuery, PhoneDeviceLookupResult>
{
    public async ValueTask<PhoneDeviceLookupResult> Handle(PhoneDeviceLookupQuery request, CancellationToken cancellationToken)
    {
        if (await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken) is not { } phone)
        {
            return new PhoneDeviceLookupResult.NotFound();
        }

        var preferences = await preferencesRepository.FindAsync(request.PhoneId, cancellationToken)
            ?? PhonePreferences.Default(request.PhoneId);

        return new PhoneDeviceLookupResult.Found(phone, preferences);
    }
}

public sealed record CharacterPhonesQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<PhoneDetails>>;

public sealed class CharacterPhonesHandler(IPhoneDeviceRepository phoneRepository, IPhonePreferencesRepository preferencesRepository)
    : IRequestHandler<CharacterPhonesQuery, IReadOnlyList<PhoneDetails>>
{
    public async ValueTask<IReadOnlyList<PhoneDetails>> Handle(CharacterPhonesQuery request, CancellationToken cancellationToken)
    {
        var phones = await phoneRepository.FindByCharacterAsync(request.CharacterId, cancellationToken);
        return await preferencesRepository.PairAsync(phones, cancellationToken);
    }
}

internal static class PhonePreferencesPairing
{
    /// <summary>One batched load for the whole list rather than one per phone.</summary>
    public static async ValueTask<IReadOnlyList<PhoneDetails>> PairAsync(
        this IPhonePreferencesRepository repository, IReadOnlyList<PhoneDevice> phones, CancellationToken cancellationToken)
    {
        if (phones.Count == 0)
        {
            return [];
        }

        var stored = (await repository.FindManyAsync([.. phones.Select(phone => phone.Id)], cancellationToken))
            .ToDictionary(preferences => preferences.Id);

        return [.. phones.Select(phone => new PhoneDetails(
            phone, stored.GetValueOrDefault(phone.Id.Value) ?? PhonePreferences.Default(phone.Id)))];
    }
}
