namespace ELifeRPG.Phone.Domain.Sims;

public enum SimCardStatus
{
    Active,

    /// <summary>
    /// Locked from outside the owning character's control — staff today, an in-game Police/State
    /// faction later. Reversible, and nothing is lost: installation, contacts, threads and the
    /// blocklist all survive a suspend/restore cycle.
    /// </summary>
    Suspended,

    /// <summary>Terminal. A deactivated number is retired and cannot be restored.</summary>
    Deactivated,
}
