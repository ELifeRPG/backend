using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Contacts;

public union ContactsResult(ContactsResult.Contacts, ContactsResult.AccessDenied)
{
    public record Contacts(IReadOnlyList<Contact> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

public sealed record ContactsQuery(SimCardId SimCardId, CharacterId ActingCharacterId) : IRequest<ContactsResult>;

public sealed class ContactsHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IContactBookRepository contactBookRepository)
    : IRequestHandler<ContactsQuery, ContactsResult>
{
    public async ValueTask<ContactsResult> Handle(ContactsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.SimCardId, request.ActingCharacterId, AppKey.Contacts,
            simCardRepository, deviceRepository, modelRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted)
        {
            return new ContactsResult.AccessDenied(access);
        }

        var book = await contactBookRepository.FindBySimAsync(request.SimCardId, cancellationToken);

        // A SIM with no book yet simply has no contacts — not an error.
        return new ContactsResult.Contacts(book?.Contacts ?? []);
    }
}
