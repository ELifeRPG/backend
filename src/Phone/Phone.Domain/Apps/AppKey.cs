namespace ELifeRPG.Phone.Domain.Apps;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured), so inserting a member mid-list remaps every stored value.
/// Three carriers: the <c>AppInstalled</c>/<c>AppUninstalled</c> events, <c>InstalledApp</c> on the
/// phone document, and <c>PhoneNotification.AppKey</c> — which is a plain document with no stream
/// behind it, and which <c>MartenPhoneNotificationRepository</c> both filters and <i>deletes</i> on
/// by ordinal, so a remap there changes which rows go away.
///
/// Nothing sits at 0, and that is a departure worth naming: ARCHITECTURE.md §9e asks that member 0
/// be the safest real default, because a payload missing the field binds to it. <c>ItemPersistence</c>
/// has such a default and takes it. This does not — "which app does a notification belong to" has no
/// safe wrong answer, and 0 meaning <c>Messages</c> would let a defaulted payload quietly attribute
/// a banner to the texting app. So the undefined value is preferred to a plausible one: it fails
/// <c>Enum.IsDefined</c> and <c>AppCatalog.Contains</c>, and <c>AppCatalog.Get</c> throws on it. The
/// cost is that the failure is a throw rather than a graceful default, which is the same cost
/// <c>BankAccountType</c> and <c>CompanyPermissions</c> describe for their own empty 0 — accepted
/// here rather than inherited.
///
/// 1-5 were the system apps and 6-7 the store's, matching the table in docs/phone.md. That is how
/// the numbering started, not a rule to keep: append-only wins, so a new app goes on the end
/// whichever half it belongs to.
/// </summary>
public enum AppKey
{
    Settings = 1,
    Store = 2,
    Phone = 3,
    Messages = 4,
    Contacts = 5,
    Bank = 6,
    Map = 7,
}
