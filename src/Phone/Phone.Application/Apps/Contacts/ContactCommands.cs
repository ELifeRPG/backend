using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Apps.Contacts;

public union SaveContactResult(
    SaveContactResult.Saved,
    SaveContactResult.AlreadySaved,
    SaveContactResult.ContactLimitReached,
    SaveContactResult.InvalidDisplayName,
    SaveContactResult.AccessDenied)
{
    public record Saved(ContactId ContactId);

    public record AlreadySaved;

    public record ContactLimitReached(int Limit);

    public record InvalidDisplayName(string Reason);

    /// <summary>
    /// Carries the guard chain's own verdict rather than restating all eleven cases per command.
    /// One mapping in the Api layer turns it into the right status for every app.
    /// </summary>
    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record SaveContactCommand(
    SimCardId SimCardId,
    CharacterId ActingCharacterId,
    PhoneNumber Number,
    string DisplayName) : IRequest<SaveContactResult>;

public sealed class SaveContactHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<SaveContactCommand, SaveContactResult>
{
    public async ValueTask<SaveContactResult> Handle(SaveContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Contacts,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new SaveContactResult.AccessDenied(access);
        }

        // Opened on first use rather than at SIM issue: a SIM that never saves a contact never needs
        // a book, and this keeps provisioning to a single stream.
        var book = await contactBookRepository.FindBySimAsync(request.SimCardId, cancellationToken);
        if (book is null)
        {
            var opened = new ContactBookOpened(new ContactBookId(Guid.NewGuid()), request.SimCardId);
            book = ContactBook.Create(opened);
            contactBookRepository.StartStream(book, opened);
        }

        if (book.Find(request.Number) is not null)
        {
            return new SaveContactResult.AlreadySaved();
        }

        if (book.Contacts.Count >= granted.Model.ContactLimit)
        {
            return new SaveContactResult.ContactLimitReached(granted.Model.ContactLimit);
        }

        var contactId = new ContactId(Guid.NewGuid());
        try
        {
            contactBookRepository.Append(book.Id, book.SaveContact(contactId, request.Number, request.DisplayName, granted.Model));
        }
        catch (ArgumentException exception)
        {
            return new SaveContactResult.InvalidDisplayName(exception.Message);
        }

        await contactBookRepository.SaveChangesAsync(cancellationToken);

        return new SaveContactResult.Saved(contactId);
    }
}

public union RenameContactResult(
    RenameContactResult.Renamed,
    RenameContactResult.ContactNotFound,
    RenameContactResult.InvalidDisplayName,
    RenameContactResult.AccessDenied)
{
    public record Renamed;

    public record ContactNotFound;

    public record InvalidDisplayName(string Reason);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record RenameContactCommand(
    SimCardId SimCardId,
    CharacterId ActingCharacterId,
    ContactId ContactId,
    string DisplayName) : IRequest<RenameContactResult>;

public sealed class RenameContactHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<RenameContactCommand, RenameContactResult>
{
    public async ValueTask<RenameContactResult> Handle(RenameContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Contacts,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new RenameContactResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindBySimAsync(request.SimCardId, cancellationToken);
        if (book?.Contacts.All(contact => contact.Id != request.ContactId) != false)
        {
            return new RenameContactResult.ContactNotFound();
        }

        try
        {
            contactBookRepository.Append(book.Id, book.RenameContact(request.ContactId, request.DisplayName));
        }
        catch (ArgumentException exception)
        {
            return new RenameContactResult.InvalidDisplayName(exception.Message);
        }

        await contactBookRepository.SaveChangesAsync(cancellationToken);

        return new RenameContactResult.Renamed();
    }
}

public union DeleteContactResult(
    DeleteContactResult.Deleted,
    DeleteContactResult.ContactNotFound,
    DeleteContactResult.AccessDenied)
{
    public record Deleted;

    public record ContactNotFound;

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record DeleteContactCommand(SimCardId SimCardId, CharacterId ActingCharacterId, ContactId ContactId)
    : IRequest<DeleteContactResult>;

public sealed class DeleteContactHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<DeleteContactCommand, DeleteContactResult>
{
    public async ValueTask<DeleteContactResult> Handle(DeleteContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Contacts,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new DeleteContactResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindBySimAsync(request.SimCardId, cancellationToken);
        if (book?.Contacts.All(contact => contact.Id != request.ContactId) != false)
        {
            return new DeleteContactResult.ContactNotFound();
        }

        contactBookRepository.Append(book.Id, book.DeleteContact(request.ContactId));
        await contactBookRepository.SaveChangesAsync(cancellationToken);

        return new DeleteContactResult.Deleted();
    }
}
