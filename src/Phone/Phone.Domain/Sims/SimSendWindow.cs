namespace ELifeRPG.Phone.Domain.Sims;

/// <summary>
/// Sliding-window send counter, one per SIM — rate limiting is per number, not per handset, because
/// the number is the sending identity.
///
/// A plain mutable document rather than an event-sourced aggregate: it is throwaway throttling
/// state, not history worth replaying. Same call as <c>GameServer</c> and <c>HiveSettings</c>.
/// </summary>
public class SimSendWindow
{
    public SimCardId Id { get; set; }

    public DateTimeOffset WindowStartedAt { get; set; }

    public int Count { get; set; }
}
