using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// The phone's personal settings — home screen layout, wallpaper, Do Not Disturb — kept so a phone
/// rebuilt after a game server restart looks and behaves the way it was left. They belong to the
/// phone rather than the player: everyone looking at it sees the same layout, and Do Not Disturb
/// silences it for everyone nearby.
///
/// Opaque to the backend. <see cref="HomeLayout"/> and <see cref="Wallpaper"/> are defined by the
/// mod and stored as-is; empty means "use the mod's default", and the mod falls back to that for a
/// wallpaper id it does not know. <see cref="LayoutVersion"/> is what lets the mod migrate its own
/// layout format later.
///
/// A plain mutable document rather than events on <see cref="PhoneDevice"/>, the same call as
/// <see cref="PhoneSendWindow"/>: last-write-wins state with no history worth replaying, and an event
/// per write would re-record a kilobyte of layout every time. Concurrent writes are last-write-wins
/// on purpose — the mod debounces them and only the game server holding the phone sends any.
///
/// A missing document is the default, so provisioning writes nothing and phones that predate this
/// need no backfill.
/// </summary>
public class PhonePreferences
{
    /// <summary>The same limit the mod enforces on its side.</summary>
    public const int MaxHomeLayoutLength = 1024;

    public const int MaxWallpaperLength = 64;

    /// <summary>
    /// The phone's id as a plain Guid rather than a <see cref="PhoneDeviceId"/>: Marten loads a
    /// strongly-typed id one at a time but refuses one in a batch, and the phone lists load every
    /// phone's preferences in one go.
    /// </summary>
    public Guid Id { get; private set; }

    public int LayoutVersion { get; private set; }

    public string HomeLayout { get; private set; } = string.Empty;

    public string Wallpaper { get; private set; } = string.Empty;

    public bool DoNotDisturb { get; private set; }

    public static PhonePreferences Default(PhoneDeviceId phoneId) => new() { Id = phoneId.Value };

    /// <summary>
    /// Null leaves a field unchanged. <paramref name="homeLayout"/> and
    /// <paramref name="layoutVersion"/> travel together: a layout stored under whatever version
    /// happened to be there before could be migrated as the wrong format.
    /// </summary>
    public void Update(int? layoutVersion, string? homeLayout, string? wallpaper, bool? doNotDisturb)
    {
        if (layoutVersion.HasValue != (homeLayout is not null))
        {
            throw new InvalidPhonePreferencesException("homeLayout and layoutVersion must be sent together.");
        }

        if (layoutVersion < 0)
        {
            throw new InvalidPhonePreferencesException("layoutVersion can not be negative.");
        }

        if (homeLayout?.Length > MaxHomeLayoutLength)
        {
            throw new InvalidPhonePreferencesException($"homeLayout can be at most {MaxHomeLayoutLength} characters.");
        }

        if (wallpaper?.Length > MaxWallpaperLength)
        {
            throw new InvalidPhonePreferencesException($"wallpaper can be at most {MaxWallpaperLength} characters.");
        }

        if (layoutVersion is { } version && homeLayout is not null)
        {
            LayoutVersion = version;
            HomeLayout = homeLayout;
        }

        if (wallpaper is not null)
        {
            Wallpaper = wallpaper;
        }

        if (doNotDisturb is { } silenced)
        {
            DoNotDisturb = silenced;
        }
    }
}
