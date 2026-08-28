using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Contacts;

public union ContactsResult(ContactsResult.Contacts, ContactsResult.AccessDenied)
{
    public record Contacts(IReadOnlyList<Contact> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ContactsQuery(PhoneDeviceId PhoneId, PhoneActor Actor) : IRequest<ContactsResult>;

public sealed class ContactsHandler(
    IPhoneDeviceRepository phoneRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<ContactsQuery, ContactsResult>
{
    public async ValueTask<ContactsResult> Handle(ContactsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, request.Actor, AppKey.Contacts, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new ContactsResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindByPhoneAsync(request.PhoneId, cancellationToken);

        // A phone with no book yet simply has no contacts — not an error.
        return new ContactsResult.Contacts(book?.Contacts ?? []);
    }
}
