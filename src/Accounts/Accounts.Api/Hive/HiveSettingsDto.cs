using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Hive;

public sealed record HiveSettingsDto(bool WhitelistEnabled, int SmsPerMinutePerSim, int SmsMaxBodyLength)
{
    public static HiveSettingsDto Create(HiveSettings source) =>
        new(source.WhitelistEnabled, source.SmsPerMinutePerSim, source.SmsMaxBodyLength);
}

public sealed record UpdateHiveSettingsRequestDto(bool? WhitelistEnabled, int? SmsPerMinutePerSim, int? SmsMaxBodyLength)
{
    public UpdateHiveSettingsCommand ToCommand() => new(WhitelistEnabled, SmsPerMinutePerSim, SmsMaxBodyLength);
}
