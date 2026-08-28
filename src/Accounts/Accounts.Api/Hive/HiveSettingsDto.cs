using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Hive;

public sealed record HiveSettingsDto(
    bool WhitelistEnabled,
    int SmsPerMinutePerPhone,
    int SmsMaxBodyLength,
    int PhoneContactLimit,
    int PhoneThreadMessageLimit,
    int PhoneMaxGroupParticipants)
{
    public static HiveSettingsDto Create(HiveSettings source) =>
        new(
            source.WhitelistEnabled,
            source.SmsPerMinutePerPhone,
            source.SmsMaxBodyLength,
            source.PhoneContactLimit,
            source.PhoneThreadMessageLimit,
            source.PhoneMaxGroupParticipants);
}

public sealed record UpdateHiveSettingsRequestDto(
    bool? WhitelistEnabled,
    int? SmsPerMinutePerPhone,
    int? SmsMaxBodyLength,
    int? PhoneContactLimit,
    int? PhoneThreadMessageLimit,
    int? PhoneMaxGroupParticipants)
{
    public UpdateHiveSettingsCommand ToCommand() =>
        new(
            WhitelistEnabled,
            SmsPerMinutePerPhone,
            SmsMaxBodyLength,
            PhoneContactLimit,
            PhoneThreadMessageLimit,
            PhoneMaxGroupParticipants);
}
