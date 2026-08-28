namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// Sliding-window send counter, one per phone — rate limiting is per number, and since the number
/// is minted with the handset those are now the same thing.
///
/// A plain mutable document rather than an event-sourced aggregate: it is throwaway throttling
/// state, not history worth replaying. Same call as <c>GameServer</c> and <c>HiveSettings</c>.
/// </summary>
public class PhoneSendWindow
{
    public PhoneDeviceId Id { get; set; }

    public DateTimeOffset WindowStartedAt { get; set; }

    public int Count { get; set; }
}
