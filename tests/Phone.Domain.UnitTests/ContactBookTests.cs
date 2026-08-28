using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class ContactBookTests
{
    private static readonly PhoneNumber Dispatcher = PhoneNumber.Parse("55009911");
    private static readonly PhoneNumber Mechanic = PhoneNumber.Parse("55009912");

    private const int ContactLimit = 50;

    private static ContactBook Book() =>
        ContactBook.Create(new ContactBookOpened(new ContactBookId(Guid.NewGuid()), new PhoneDeviceId(Guid.NewGuid())));

    [Fact]
    public void Create_OpensAnEmptyBookForThePhone()
    {
        var phoneId = new PhoneDeviceId(Guid.NewGuid());

        var book = ContactBook.Create(new ContactBookOpened(new ContactBookId(Guid.NewGuid()), phoneId));

        Assert.Equal(phoneId, book.PhoneId);
        Assert.Empty(book.Contacts);
    }

    [Fact]
    public void SaveContact_AddsIt()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());

        book.SaveContact(contactId, Dispatcher, "Dispatcher", ContactLimit);

        var contact = Assert.Single(book.Contacts);
        Assert.Equal(contactId, contact.Id);
        Assert.Equal(Dispatcher, contact.Number);
        Assert.Equal("Dispatcher", contact.DisplayName);
    }

    [Fact]
    public void SaveContact_TrimsTheDisplayName()
    {
        var book = Book();

        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "  Dispatcher  ", ContactLimit);

        Assert.Equal("Dispatcher", book.Contacts[0].DisplayName);
    }

    [Fact]
    public void SaveContact_WithBlankDisplayName_ThrowsArgumentException()
    {
        var book = Book();

        Assert.Throws<ArgumentException>(() => book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "   ", ContactLimit));
    }

    [Fact]
    public void SaveContact_WithANumberAlreadySaved_ThrowsContactAlreadyExists()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", ContactLimit);

        Assert.Throws<ContactAlreadyExistsException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher again", ContactLimit));
    }

    [Fact]
    public void SaveContact_MatchesExistingNumbersRegardlessOfFormatting()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), PhoneNumber.Parse("5500-9911"), "Dispatcher", ContactLimit);

        Assert.Throws<ContactAlreadyExistsException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), PhoneNumber.Parse("+55009911"), "Dispatcher", ContactLimit));
    }

    [Fact]
    public void SaveContact_AtTheContactLimit_ThrowsContactLimitReached()
    {
        // The cap is hive-wide and handed in by the caller: every phone holds the same number, so
        // there is no better handset to buy for a longer address book.
        const int limit = 1;
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", limit);

        Assert.Throws<ContactLimitReachedException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), Mechanic, "Mechanic", limit));
    }

    [Fact]
    public void RenameContact_ChangesTheDisplayName()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", ContactLimit);

        book.RenameContact(contactId, "Night dispatcher");

        Assert.Equal("Night dispatcher", book.Contacts[0].DisplayName);
    }

    [Fact]
    public void RenameContact_ThatIsNotSaved_ThrowsContactNotFound()
    {
        Assert.Throws<ContactNotFoundException>(() => Book().RenameContact(new ContactId(Guid.NewGuid()), "Nobody"));
    }

    [Fact]
    public void RenameContact_WithBlankDisplayName_ThrowsArgumentException()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", ContactLimit);

        Assert.Throws<ArgumentException>(() => book.RenameContact(contactId, " "));
    }

    [Fact]
    public void DeleteContact_RemovesIt()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", ContactLimit);

        book.DeleteContact(contactId);

        Assert.Empty(book.Contacts);
    }

    [Fact]
    public void DeleteContact_ThatIsNotSaved_ThrowsContactNotFound()
    {
        Assert.Throws<ContactNotFoundException>(() => Book().DeleteContact(new ContactId(Guid.NewGuid())));
    }

    [Fact]
    public void DeleteContact_FreesUpRoomAgainstTheLimit()
    {
        const int limit = 1;
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", limit);
        book.DeleteContact(contactId);

        book.SaveContact(new ContactId(Guid.NewGuid()), Mechanic, "Mechanic", limit);

        Assert.Single(book.Contacts);
    }

    [Fact]
    public void Find_ReturnsTheContactForANumber()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", ContactLimit);

        Assert.Equal("Dispatcher", book.Find(Dispatcher)?.DisplayName);
        Assert.Null(book.Find(Mechanic));
    }

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var bookId = new ContactBookId(Guid.NewGuid());
        var contactId = new ContactId(Guid.NewGuid());
        var book = new ContactBook();

        book.Apply(new ContactBookOpened(bookId, new PhoneDeviceId(Guid.NewGuid())));
        book.Apply(new ContactSaved(bookId, contactId, Dispatcher, "Dispatcher"));
        book.Apply(new ContactRenamed(bookId, contactId, "Night dispatcher"));

        Assert.Equal("Night dispatcher", Assert.Single(book.Contacts).DisplayName);
    }
}
