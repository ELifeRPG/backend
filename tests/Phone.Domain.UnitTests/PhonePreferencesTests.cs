using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhonePreferencesTests
{
    private static PhonePreferences Preferences() => PhonePreferences.Default(new PhoneDeviceId(Guid.NewGuid()));

    [Fact]
    public void Default_IsEmpty_MeaningTheModsDefaults()
    {
        var preferences = Preferences();

        Assert.Equal(0, preferences.LayoutVersion);
        Assert.Equal(string.Empty, preferences.HomeLayout);
        Assert.Equal(string.Empty, preferences.Wallpaper);
        Assert.False(preferences.DoNotDisturb);
    }

    [Fact]
    public void Update_ChangesOnlyTheFieldsItIsSent()
    {
        var preferences = Preferences();
        preferences.Update(2, "layout", "Night", false);

        preferences.Update(null, null, null, true);

        Assert.Equal(2, preferences.LayoutVersion);
        Assert.Equal("layout", preferences.HomeLayout);
        Assert.Equal("Night", preferences.Wallpaper);
        Assert.True(preferences.DoNotDisturb);
    }

    [Fact]
    public void Update_WithAnEmptyString_ClearsBackToTheDefault()
    {
        var preferences = Preferences();
        preferences.Update(null, null, "Night", null);

        preferences.Update(null, null, string.Empty, null);

        Assert.Equal(string.Empty, preferences.Wallpaper);
    }

    [Fact]
    public void Update_AcceptsAHomeLayoutAtTheLimit()
    {
        var preferences = Preferences();

        preferences.Update(1, new string('x', PhonePreferences.MaxHomeLayoutLength), null, null);

        Assert.Equal(PhonePreferences.MaxHomeLayoutLength, preferences.HomeLayout.Length);
    }

    [Fact]
    public void Update_RejectsAHomeLayoutOverTheLimit()
    {
        var preferences = Preferences();

        Assert.Throws<InvalidPhonePreferencesException>(() =>
            preferences.Update(1, new string('x', PhonePreferences.MaxHomeLayoutLength + 1), null, null));
        Assert.Equal(string.Empty, preferences.HomeLayout);
    }

    [Fact]
    public void Update_RejectsAWallpaperOverTheLimit()
    {
        var preferences = Preferences();

        Assert.Throws<InvalidPhonePreferencesException>(() =>
            preferences.Update(null, null, new string('x', PhonePreferences.MaxWallpaperLength + 1), null));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, "layout")]
    public void Update_RejectsALayoutAndVersionSentApart(int? layoutVersion, string? homeLayout)
    {
        // A layout stored under whatever version happened to be there could be migrated as the
        // wrong format.
        var preferences = Preferences();

        Assert.Throws<InvalidPhonePreferencesException>(() => preferences.Update(layoutVersion, homeLayout, null, null));
    }

    [Fact]
    public void Update_RejectsANegativeLayoutVersion()
    {
        var preferences = Preferences();

        Assert.Throws<InvalidPhonePreferencesException>(() => preferences.Update(-1, "layout", null, null));
    }

    [Fact]
    public void Update_ThatIsRejected_ChangesNothing()
    {
        // Validated in full before anything is assigned, so a bad wallpaper can not let a good
        // Do Not Disturb through on the same call.
        var preferences = Preferences();

        Assert.Throws<InvalidPhonePreferencesException>(() =>
            preferences.Update(null, null, new string('x', PhonePreferences.MaxWallpaperLength + 1), true));
        Assert.False(preferences.DoNotDisturb);
    }
}
