using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Contacts;

/// <summary>
/// The Contacts app's state: one address book per SIM, so it travels with the number rather than
/// with the handset.
///
/// Its own aggregate rather than a list on <see cref="SimCard"/> — that is what makes the app
/// boundary real, and it keeps the SIM stream from churning every time someone saves a number.
///
/// The Messages app deliberately does not read this: threads store bare numbers, and the client
/// resolves display names here. Keeping the two apps decoupled is the point of the split.
/// </summary>
public class ContactBook
{
    [JsonInclude]
    public ContactBookId Id { get; private set; }

    [JsonInclude]
    public SimCardId SimCardId { get; private set; }

    [JsonInclude]
    public List<Contact> Contacts { get; private set; } = [];

    public static ContactBook Create(ContactBookOpened domainEvent)
    {
        var book = new ContactBook();
        book.Apply(domainEvent);
        return book;
    }

    /// <summary>
    /// The model is the device the SIM currently sits in — a better handset holds more contacts.
    /// </summary>
    public ContactSaved SaveContact(ContactId contactId, PhoneNumber number, string displayName, PhoneModel model)
    {
        var trimmed = EnsureDisplayName(displayName);

        if (Find(number) is not null)
        {
            throw new ContactAlreadyExistsException($"{number} is already saved in contact book {Id}.");
        }

        if (Contacts.Count >= model.ContactLimit)
        {
            throw new ContactLimitReachedException($"Contact book {Id} is full ({model.ContactLimit} contacts).");
        }

        var domainEvent = new ContactSaved(Id, contactId, number, trimmed);
        Apply(domainEvent);
        return domainEvent;
    }

    public ContactRenamed RenameContact(ContactId contactId, string displayName)
    {
        var trimmed = EnsureDisplayName(displayName);
        EnsureExists(contactId);

        var domainEvent = new ContactRenamed(Id, contactId, trimmed);
        Apply(domainEvent);
        return domainEvent;
    }

    public ContactDeleted DeleteContact(ContactId contactId)
    {
        EnsureExists(contactId);

        var domainEvent = new ContactDeleted(Id, contactId);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>Matches on the canonical number, so punctuation in either spelling is irrelevant.</summary>
    public Contact? Find(PhoneNumber number) => Contacts.FirstOrDefault(contact => contact.Number == number);

    public void Apply(ContactBookOpened domainEvent)
    {
        Id = domainEvent.Id;
        SimCardId = domainEvent.SimCardId;
    }

    public void Apply(ContactSaved domainEvent) =>
        Contacts.Add(new Contact(domainEvent.ContactId, domainEvent.Number, domainEvent.DisplayName));

    public void Apply(ContactRenamed domainEvent)
    {
        var index = Contacts.FindIndex(contact => contact.Id == domainEvent.ContactId);
        if (index >= 0)
        {
            Contacts[index] = Contacts[index] with { DisplayName = domainEvent.DisplayName };
        }
    }

    public void Apply(ContactDeleted domainEvent) => Contacts.RemoveAll(contact => contact.Id == domainEvent.ContactId);

    private void EnsureExists(ContactId contactId)
    {
        if (Contacts.All(contact => contact.Id != contactId))
        {
            throw new ContactNotFoundException($"Contact {contactId} is not in contact book {Id}.");
        }
    }

    private static string EnsureDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Contact display name is required.", nameof(displayName));
        }

        return displayName.Trim();
    }
}
