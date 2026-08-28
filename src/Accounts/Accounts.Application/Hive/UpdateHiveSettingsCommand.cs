using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.Hive;

/// <summary>Omitted fields are left unchanged, so the new knobs default to null rather than to a value.</summary>
public sealed record UpdateHiveSettingsCommand(
    bool? WhitelistEnabled,
    int? SmsPerMinutePerPhone = null,
    int? SmsMaxBodyLength = null,
    int? PhoneContactLimit = null,
    int? PhoneThreadMessageLimit = null,
    int? PhoneMaxGroupParticipants = null) : IRequest<HiveSettings>;

public sealed class UpdateHiveSettingsHandler(IHiveSettingsRepository repository)
    : IRequestHandler<UpdateHiveSettingsCommand, HiveSettings>
{
    public async ValueTask<HiveSettings> Handle(UpdateHiveSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);

        if (request.WhitelistEnabled is { } whitelistEnabled)
        {
            settings.WhitelistEnabled = whitelistEnabled;
        }

        // Rejected rather than clamped: a zero or negative limit would silently switch messaging off
        // hive-wide, and an admin who typed it almost certainly meant something else.
        if (request.SmsPerMinutePerPhone is { } perMinute)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1, nameof(request.SmsPerMinutePerPhone));
            settings.SmsPerMinutePerPhone = perMinute;
        }

        if (request.SmsMaxBodyLength is { } maxBodyLength)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBodyLength, 1, nameof(request.SmsMaxBodyLength));
            settings.SmsMaxBodyLength = maxBodyLength;
        }

        if (request.PhoneContactLimit is { } contactLimit)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(contactLimit, 1, nameof(request.PhoneContactLimit));
            settings.PhoneContactLimit = contactLimit;
        }

        if (request.PhoneThreadMessageLimit is { } threadMessageLimit)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threadMessageLimit, 1, nameof(request.PhoneThreadMessageLimit));
            settings.PhoneThreadMessageLimit = threadMessageLimit;
        }

        // Two, not one: a thread with a single participant is an ordinary conversation, so a cap
        // below that would mean "no messages at all" rather than "no groups".
        if (request.PhoneMaxGroupParticipants is { } maxGroupParticipants)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxGroupParticipants, 2, nameof(request.PhoneMaxGroupParticipants));
            settings.PhoneMaxGroupParticipants = maxGroupParticipants;
        }

        await repository.UpsertAsync(settings, cancellationToken);
        return settings;
    }
}
