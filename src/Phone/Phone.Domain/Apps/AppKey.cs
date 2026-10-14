namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured); inserting a member mid-list remaps every stored value.
/// </summary>
public enum AppKey
{
    Messages = 0,
    Contacts = 1,
    Settings = 2,
    Store = 3,
    Phone = 4,
    Bank = 5,
    Map = 6,
}
