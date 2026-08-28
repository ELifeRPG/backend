using System.Text.Json;
using ELifeRPG.Accounts.Domain;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public class HiveSettingsTests
{
    [Fact]
    public void Defaults_HaveWhitelistDisabledAndTheSingletonId()
    {
        var settings = new HiveSettings();

        Assert.False(settings.WhitelistEnabled);
        Assert.Equal(HiveSettings.SingletonId, settings.Id);
    }

    [Fact]
    public void Defaults_GiveEveryPhoneTheSameUsableLimits()
    {
        // These three were per-model capability numbers until the SIM/tier split came out. Their
        // defaults are the burner's old numbers, so nothing got quietly more generous in the move.
        var settings = new HiveSettings();

        Assert.Equal(20, settings.SmsPerMinutePerPhone);
        Assert.Equal(480, settings.SmsMaxBodyLength);
        Assert.Equal(50, settings.PhoneContactLimit);
        Assert.Equal(30, settings.PhoneThreadMessageLimit);
        Assert.Equal(5, settings.PhoneMaxGroupParticipants);
    }

    [Fact]
    public void Deserialising_ADocumentWrittenBeforeAKnobExisted_KeepsTheDefault()
    {
        // The load-bearing claim in this type's own doc comment, and the reason every knob carries a
        // property initializer: the stored singleton predates each new setting. A zero contact limit
        // or a zero group cap would mean "nobody may save a number" / "nobody may send", neither of
        // which is a default anyone would choose.
        var stored = JsonSerializer.Deserialize<HiveSettings>("""{"WhitelistEnabled":true}""")!;

        Assert.True(stored.WhitelistEnabled);
        Assert.Equal(20, stored.SmsPerMinutePerPhone);
        Assert.Equal(480, stored.SmsMaxBodyLength);
        Assert.Equal(50, stored.PhoneContactLimit);
        Assert.Equal(30, stored.PhoneThreadMessageLimit);
        Assert.Equal(5, stored.PhoneMaxGroupParticipants);
    }
}
