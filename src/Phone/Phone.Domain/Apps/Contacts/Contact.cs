using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Contacts;

public sealed record Contact(ContactId Id, PhoneNumber Number, string DisplayName);
