using ELifeRPG.Accounts.Application.Hive;
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
    /// Carries the guard chain's own verdict rather than restating every case per command.
    /// One mapping in the Api layer turns it into the right status for every app.
    /// </summary>
    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record SaveContactCommand(
    PhoneDeviceId PhoneId,
    PhoneNumber Number,
    string DisplayName) : IRequest<SaveContactResult>;

public sealed class SaveContactHandler(
    IPhoneDeviceRepository phoneRepository,
    IContactBookRepository contactBookRepository,
    IMediator mediator)
    : IRequestHandler<SaveContactCommand, SaveContactResult>
{
    public async ValueTask<SaveContactResult> Handle(SaveContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Contacts, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new SaveContactResult.AccessDenied(access);
        }

        // The cap is a hive-wide knob now, not a number on the handset's model — every phone holds
        // the same. Read per call so a staff edit takes effect without a redeploy.
        var contactLimit = (await mediator.Send(new HiveSettingsQuery(), cancellationToken)).PhoneContactLimit;

        // Opened on first use rather than at provisioning: a phone whose owner never saves a number
        // never needs a book, and this keeps provisioning to a single stream.
        var book = await contactBookRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);
        if (book is null)
        {
            var opened = new ContactBookOpened(new ContactBookId(Guid.NewGuid()), request.PhoneId);
            book = ContactBook.Create(opened);
            contactBookRepository.StartStream(book, opened);
        }

        if (book.Find(request.Number) is not null)
        {
            return new SaveContactResult.AlreadySaved();
        }

        if (book.Contacts.Count >= contactLimit)
        {
            return new SaveContactResult.ContactLimitReached(contactLimit);
        }

        var contactId = new ContactId(Guid.NewGuid());
        try
        {
            contactBookRepository.Append(book.Id, book.SaveContact(contactId, request.Number, request.DisplayName, contactLimit));
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
    PhoneDeviceId PhoneId,
    ContactId ContactId,
    string DisplayName) : IRequest<RenameContactResult>;

public sealed class RenameContactHandler(
    IPhoneDeviceRepository phoneRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<RenameContactCommand, RenameContactResult>
{
    public async ValueTask<RenameContactResult> Handle(RenameContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Contacts, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new RenameContactResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);
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

public sealed record DeleteContactCommand(PhoneDeviceId PhoneId, ContactId ContactId)
    : IRequest<DeleteContactResult>;

public sealed class DeleteContactHandler(
    IPhoneDeviceRepository phoneRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<DeleteContactCommand, DeleteContactResult>
{
    public async ValueTask<DeleteContactResult> Handle(DeleteContactCommand request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Contacts, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new DeleteContactResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);
        if (book?.Contacts.All(contact => contact.Id != request.ContactId) != false)
        {
            return new DeleteContactResult.ContactNotFound();
        }

        contactBookRepository.Append(book.Id, book.DeleteContact(request.ContactId));
        await contactBookRepository.SaveChangesAsync(cancellationToken);

        return new DeleteContactResult.Deleted();
    }
}
