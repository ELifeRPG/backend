using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Contacts.Events;

public sealed record ContactBookOpened(ContactBookId Id, SimCardId SimCardId);

public sealed record ContactSaved(ContactBookId Id, ContactId ContactId, PhoneNumber Number, string DisplayName);

public sealed record ContactRenamed(ContactBookId Id, ContactId ContactId, string DisplayName);

public sealed record ContactDeleted(ContactBookId Id, ContactId ContactId);
