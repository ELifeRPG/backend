using ELifeRPG.Phone.Api.Apps.Contacts;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Application.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Sims;
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
    /// Addressed by SIM, not by handset, because that is what owns the address book.
    /// </summary>
    private static void MapContacts(RouteGroupBuilder group)
    {
        group.MapGet("sim-cards/{simCardId:guid}/contacts", async (
                Guid simCardId, [FromQuery] Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ContactsQuery(new SimCardId(simCardId), new CharacterId(characterId)), cancellationToken);

                return result switch
                {
                    ContactsResult.Contacts contacts => Results.Ok(contacts.Entries.Select(ContactDto.Create)),
                    ContactsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<ContactDto>>()
            .WithName("ListContacts")
            .WithDescription("Lists a SIM's saved contacts.");

        group.MapPost("sim-cards/{simCardId:guid}/contacts", async (
                Guid simCardId,
                [FromBody] SaveContactRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(request.Number, out var number, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new SaveContactCommand(new SimCardId(simCardId), new CharacterId(request.CharacterId), number, request.DisplayName),
                    cancellationToken);

                return result switch
                {
                    SaveContactResult.Saved saved => Results.Ok(new { contactId = saved.ContactId.Value }),
                    SaveContactResult.AlreadySaved => Results.Problem(
                        title: "That number is already saved", statusCode: StatusCodes.Status409Conflict),
                    SaveContactResult.ContactLimitReached limit => Results.Problem(
                        title: $"This handset holds at most {limit.Limit} contacts", statusCode: StatusCodes.Status409Conflict),
                    SaveContactResult.InvalidDisplayName invalid => Results.Problem(
                        title: invalid.Reason, statusCode: StatusCodes.Status400BadRequest),
                    SaveContactResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("SaveContact")
            .WithDescription("Saves a number to a SIM's address book.");

        group.MapPatch("sim-cards/{simCardId:guid}/contacts/{contactId:guid}", async (
                Guid simCardId,
                Guid contactId,
                [FromBody] RenameContactRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RenameContactCommand(
                        new SimCardId(simCardId), new CharacterId(request.CharacterId), new ContactId(contactId), request.DisplayName),
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
            .WithName("RenameContact")
            .WithDescription("Renames a saved contact.");

        group.MapDelete("sim-cards/{simCardId:guid}/contacts/{contactId:guid}", async (
                Guid simCardId,
                Guid contactId,
                [FromQuery] Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new DeleteContactCommand(new SimCardId(simCardId), new CharacterId(characterId), new ContactId(contactId)),
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
            .WithName("DeleteContact")
            .WithDescription("Removes a saved contact.");
    }
}
