namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
///
/// Sharper here than for most: <c>MartenWhitelistApplicationRepository</c> filters on this enum
/// inside Marten LINQ (<c>x.Status == Open</c>, <c>== InReview</c>, <c>== Approved</c>), which
/// compiles to a comparison against the ordinal in JSONB. A reorder would therefore not merely
/// misread stored rows, it would silently change which rows the pending and approved queries return.
/// </summary>
public enum WhitelistApplicationStatus
{
    Open = 0,
    InReview = 1,
    Approved = 2,
    Rejected = 3,
}
