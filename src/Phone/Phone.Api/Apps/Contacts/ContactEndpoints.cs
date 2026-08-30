using ELifeRPG.Phone.Api.Apps.Contacts;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Application.Apps.Contacts;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    /// <summary>
    /// Rooted under the app that owns them, mirroring the Apps/<Name>/ folders in Domain,
    /// Application and Api. A new app owns /apps/{its key}/ outright, so two apps can never race
    /// each other for the same noun at the phone level.
    /// </summary>
    private static void MapContacts(RouteGroupBuilder group)
    {
        group.MapGet("phones/{phoneId:guid}/apps/contacts/entries", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ContactsQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    ContactsResult.Contacts contacts => Results.Ok(contacts.Entries.Select(ContactDto.Create)),
                    ContactsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<ContactDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("ListContacts")
            .WithDescription("Lists a phone's saved contacts.");

        group.MapPost("phones/{phoneId:guid}/apps/contacts/entries", async (
                Guid phoneId,
                [FromBody] SaveContactRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(request.Number, out var number, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new SaveContactCommand(new PhoneDeviceId(phoneId), number, request.DisplayName),
                    cancellationToken);

                return result switch
                {
                    SaveContactResult.Saved saved => Results.Ok(new SaveContactResponseDto(saved.ContactId.Value)),
                    SaveContactResult.AlreadySaved => Results.Problem(
                        title: "That number is already saved", statusCode: StatusCodes.Status409Conflict),
                    SaveContactResult.ContactLimitReached limit => Results.Problem(
                        title: $"A phone holds at most {limit.Limit} contacts", statusCode: StatusCodes.Status409Conflict),
                    SaveContactResult.InvalidDisplayName invalid => Results.Problem(
                        title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                    SaveContactResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .Produces<SaveContactResponseDto>()
            .WithName("SaveContact")
            .WithDescription("Saves a number to a phone's address book.");

        group.MapPatch("phones/{phoneId:guid}/apps/contacts/entries/{contactId:guid}", async (
                Guid phoneId,
                Guid contactId,
                [FromBody] RenameContactRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RenameContactCommand(
                        new PhoneDeviceId(phoneId), new ContactId(contactId), request.DisplayName),
                    cancellationToken);

                return result switch
                {
                    RenameContactResult.Renamed => Results.NoContent(),
                    RenameContactResult.ContactNotFound => Results.Problem(
                        title: "Contact not found", statusCode: StatusCodes.Status404NotFound),
                    RenameContactResult.InvalidDisplayName invalid => Results.Problem(
                        title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                    RenameContactResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("RenameContact")
            .WithDescription("Renames a saved contact.");

        group.MapDelete("phones/{phoneId:guid}/apps/contacts/entries/{contactId:guid}", async (
                Guid phoneId,
                Guid contactId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new DeleteContactCommand(new PhoneDeviceId(phoneId), new ContactId(contactId)),
                    cancellationToken);

                return result switch
                {
                    DeleteContactResult.Deleted => Results.NoContent(),
                    DeleteContactResult.ContactNotFound => Results.Problem(
                        title: "Contact not found", statusCode: StatusCodes.Status404NotFound),
                    DeleteContactResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("DeleteContact")
            .WithDescription("Removes a saved contact.");
    }
}
