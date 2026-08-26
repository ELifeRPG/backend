using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.Hive;

/// <summary>Omitted fields are left unchanged, so the new knobs default to null rather than to a value.</summary>
public sealed record UpdateHiveSettingsCommand(
    bool? WhitelistEnabled,
    int? SmsPerMinutePerSim = null,
    int? SmsMaxBodyLength = null) : IRequest<HiveSettings>;

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
        if (request.SmsPerMinutePerSim is { } perMinute)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1, nameof(request.SmsPerMinutePerSim));
            settings.SmsPerMinutePerSim = perMinute;
        }

        if (request.SmsMaxBodyLength is { } maxBodyLength)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBodyLength, 1, nameof(request.SmsMaxBodyLength));
            settings.SmsMaxBodyLength = maxBodyLength;
        }

        await repository.UpsertAsync(settings, cancellationToken);
        return settings;
    }
}
