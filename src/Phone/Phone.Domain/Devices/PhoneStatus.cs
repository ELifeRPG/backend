namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// Append only, with its ordinals written out — they are persisted in Marten event and document
/// payloads, so reordering members renumbers every stored row. This enum predates that sweep as
/// <c>SimCardStatus</c> and was missed by it; renaming it here is the moment to close the gap.
/// </summary>
public enum PhoneStatus
{
    Active = 0,

    /// <summary>
    /// Locked from outside the owning character's control — staff today, an in-game Police/State
    /// faction later. Reversible, and nothing is lost: contacts, threads and the blocklist all
    /// survive a suspend/restore cycle.
    /// </summary>
    Suspended = 1,

    /// <summary>Terminal. A deactivated number is retired and cannot be restored.</summary>
    Deactivated = 2,
}
