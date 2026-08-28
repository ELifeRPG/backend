namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
/// </summary>
public enum AppKey
{
    Messages = 0,
    Contacts = 1,
}
