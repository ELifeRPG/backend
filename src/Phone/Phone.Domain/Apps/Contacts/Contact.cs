using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Contacts;

public sealed record Contact(ContactId Id, PhoneNumber Number, string DisplayName);
