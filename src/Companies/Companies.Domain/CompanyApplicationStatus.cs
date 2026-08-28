namespace ELifeRPG.Companies.Domain;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
///
/// No event payload carries this enum — the <c>Apply</c> handlers derive it from which event arrived
/// (<c>ApplicationConfirmed</c> → <see cref="InProgress"/>, and so on). What persists it is
/// <c>CompanyProjection</c>, registered <c>Inline</c>, which materializes the whole <c>Company</c>
/// document — <c>Applications[].Status</c> included — into JSONB on every append.
/// </summary>
public enum CompanyApplicationStatus
{
    Pending = 0,
    InProgress = 1,
    Accepted = 2,
    Denied = 3,
}
