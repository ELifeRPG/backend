namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
/// Lives on <c>Account.Status</c> and is written by <c>Account.Apply(AccountLocked)</c>/
/// <c>Apply(AccountUnlocked)</c>, so every account document ever stored carries one of these numbers.
/// </summary>
public enum AccountStatus
{
    Active = 0,
    Locked = 1,
}
