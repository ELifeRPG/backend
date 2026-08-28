namespace ELifeRPG.Banking.Domain;

/// <summary>
/// Matches the legacy app's BankAccountType (Personal/Corporate) — see ARCHITECTURE.md §9e.
///
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value. It
/// rides on the <c>BankAccountOpened</c> event, which is immutable and replayed forever.
///
/// The 1-based numbering is inherited from the legacy app, not chosen here, and is now frozen by the
/// stored data. Worth knowing that it is the inverse of <c>ItemPersistence</c>'s deliberate choice:
/// there is no member at <c>0</c>, so anything that ever deserialized without a <c>Type</c> would
/// bind to an undefined value rather than a safe default. Nothing writes such an event today — both
/// factory paths always set it — so this is a constraint on future edits, not a live defect.
/// </summary>
public enum BankAccountType
{
    Personal = 1,
    Corporate = 2,
}
