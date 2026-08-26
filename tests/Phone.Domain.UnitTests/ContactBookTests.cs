using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class ContactBookTests
{
    private static readonly PhoneNumber Dispatcher = PhoneNumber.Parse("55009911");
    private static readonly PhoneNumber Mechanic = PhoneNumber.Parse("55009912");

    private static PhoneModel Model(int contactLimit = 50) =>
        PhoneModel.Create(PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()), "Burner", 1, null, 1,
            [AppKey.Messages, AppKey.Contacts], contactLimit, 30, 5));

    private static ContactBook Book() =>
        ContactBook.Create(new ContactBookOpened(new ContactBookId(Guid.NewGuid()), new SimCardId(Guid.NewGuid())));

    [Fact]
    public void Create_OpensAnEmptyBookForTheSim()
    {
        var simId = new SimCardId(Guid.NewGuid());

        var book = ContactBook.Create(new ContactBookOpened(new ContactBookId(Guid.NewGuid()), simId));

        Assert.Equal(simId, book.SimCardId);
        Assert.Empty(book.Contacts);
    }

    [Fact]
    public void SaveContact_AddsIt()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());

        book.SaveContact(contactId, Dispatcher, "Dispatcher", Model());

        var contact = Assert.Single(book.Contacts);
        Assert.Equal(contactId, contact.Id);
        Assert.Equal(Dispatcher, contact.Number);
        Assert.Equal("Dispatcher", contact.DisplayName);
    }

    [Fact]
    public void SaveContact_TrimsTheDisplayName()
    {
        var book = Book();

        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "  Dispatcher  ", Model());

        Assert.Equal("Dispatcher", book.Contacts[0].DisplayName);
    }

    [Fact]
    public void SaveContact_WithBlankDisplayName_ThrowsArgumentException()
    {
        var book = Book();

        Assert.Throws<ArgumentException>(() => book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "   ", Model()));
    }

    [Fact]
    public void SaveContact_WithANumberAlreadySaved_ThrowsContactAlreadyExists()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", Model());

        Assert.Throws<ContactAlreadyExistsException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher again", Model()));
    }

    [Fact]
    public void SaveContact_MatchesExistingNumbersRegardlessOfFormatting()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), PhoneNumber.Parse("5500-9911"), "Dispatcher", Model());

        Assert.Throws<ContactAlreadyExistsException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), PhoneNumber.Parse("+55009911"), "Dispatcher", Model()));
    }

    [Fact]
    public void SaveContact_AtTheModelContactLimit_ThrowsContactLimitReached()
    {
        // The device the SIM currently sits in decides how many contacts fit — that is what buying a
        // better handset gets you.
        var model = Model(contactLimit: 1);
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", model);

        Assert.Throws<ContactLimitReachedException>(() =>
            book.SaveContact(new ContactId(Guid.NewGuid()), Mechanic, "Mechanic", model));
    }

    [Fact]
    public void RenameContact_ChangesTheDisplayName()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", Model());

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
        book.SaveContact(contactId, Dispatcher, "Dispatcher", Model());

        Assert.Throws<ArgumentException>(() => book.RenameContact(contactId, " "));
    }

    [Fact]
    public void DeleteContact_RemovesIt()
    {
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", Model());

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
        var model = Model(contactLimit: 1);
        var book = Book();
        var contactId = new ContactId(Guid.NewGuid());
        book.SaveContact(contactId, Dispatcher, "Dispatcher", model);
        book.DeleteContact(contactId);

        book.SaveContact(new ContactId(Guid.NewGuid()), Mechanic, "Mechanic", model);

        Assert.Single(book.Contacts);
    }

    [Fact]
    public void Find_ReturnsTheContactForANumber()
    {
        var book = Book();
        book.SaveContact(new ContactId(Guid.NewGuid()), Dispatcher, "Dispatcher", Model());

        Assert.Equal("Dispatcher", book.Find(Dispatcher)?.DisplayName);
        Assert.Null(book.Find(Mechanic));
    }

    [Fact]
    public void Apply_ReplayingEventsRebuildsTheSameState()
    {
        var bookId = new ContactBookId(Guid.NewGuid());
        var contactId = new ContactId(Guid.NewGuid());
        var book = new ContactBook();

        book.Apply(new ContactBookOpened(bookId, new SimCardId(Guid.NewGuid())));
        book.Apply(new ContactSaved(bookId, contactId, Dispatcher, "Dispatcher"));
        book.Apply(new ContactRenamed(bookId, contactId, "Night dispatcher"));

        Assert.Equal("Night dispatcher", Assert.Single(book.Contacts).DisplayName);
    }
}
