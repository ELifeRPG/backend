using ELifeRPG.Phone.Api;
using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Apps.Messages;
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
    /// Rooted under /apps/messages/, the app that owns them — see MapContacts for why. The blocklist
    /// is here rather than at phone level: it is the Messages app's list, and it runs the same guard
    /// chain as a send.
    /// </summary>
    private static void MapMessages(RouteGroupBuilder group)
    {
        group.MapGet("phones/{phoneId:guid}/apps/messages/threads", async (
                Guid phoneId, [FromQuery] Guid characterId, [FromQuery] string? pin, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ThreadsQuery(new PhoneDeviceId(phoneId), new PhoneActor(new CharacterId(characterId), pin)), cancellationToken);

                return result switch
                {
                    ThreadsResult.Threads threads => Results.Ok(threads.Entries.Select(MessageThreadSummaryDto.Create)),
                    ThreadsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<MessageThreadSummaryDto>>()
            .WithName("ListThreads")
            .WithDescription("Lists a phone's conversations, newest first. Bodies are omitted; open a thread for those.");

        group.MapGet("phones/{phoneId:guid}/apps/messages/threads/{threadId:guid}", async (
                Guid phoneId, Guid threadId, [FromQuery] Guid characterId, [FromQuery] string? pin, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ThreadQuery(new PhoneDeviceId(phoneId), new PhoneActor(new CharacterId(characterId), pin), new MessageThreadId(threadId)),
                    cancellationToken);

                return result switch
                {
                    ThreadResult.Found found => Results.Ok(MessageThreadDto.Create(found.Thread)),
                    ThreadResult.NotFound => Results.Problem(
                        title: "Thread not found", statusCode: StatusCodes.Status404NotFound),
                    ThreadResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<MessageThreadDto>()
            .WithName("GetThread")
            .WithDescription("Gets one conversation with its retained messages.");

        group.MapPost("phones/{phoneId:guid}/apps/messages/send", async (
                Guid phoneId,
                [FromBody] SendMessageRequestDto request,
                IMediator mediator,
                PhoneHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParseAll(request.To, out var recipients, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new SendMessageCommand(new PhoneDeviceId(phoneId), request.ToActor(), recipients, request.Body),
                    cancellationToken);

                // Sent is handled first because pushing needs an await, which a switch expression
                // over the union cannot carry.
                if (result is SendMessageResult.Sent sent)
                {
                    await NotifyRecipientsAsync(notifier, sent, request.Body, cancellationToken);

                    return Results.Ok(new SendMessageResponseDto(
                        sent.ThreadId.Value,
                        sent.MessageId.Value,
                        [.. sent.UndeliverableRecipients.Select(number => number.Value)]));
                }

                return result switch
                {
                    SendMessageResult.EmptyBody => Results.Problem(
                        title: "Message body is required", statusCode: StatusCodes.Status400BadRequest),

                    SendMessageResult.BodyTooLong tooLong => Results.Problem(
                        title: $"Message body may not exceed {tooLong.MaxLength} characters",
                        statusCode: StatusCodes.Status400BadRequest),

                    SendMessageResult.NoRecipients => Results.Problem(
                        title: "At least one recipient other than yourself is required",
                        statusCode: StatusCodes.Status400BadRequest),

                    SendMessageResult.TooManyRecipients tooMany => Results.Problem(
                        title: $"A message may address at most {tooMany.MaxParticipants} recipients",
                        statusCode: StatusCodes.Status409Conflict),

                    SendMessageResult.RateLimited limited => Results.Problem(
                        title: $"Rate limit reached ({limited.PerMinuteLimit} messages per minute)",
                        statusCode: StatusCodes.Status429TooManyRequests),

                    SendMessageResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),

                    SendMessageResult.Sent => throw new InvalidOperationException("Handled above."),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<SendMessageResponseDto>()
            .WithName("SendMessage")
            .WithDescription("Sends a message to one or more numbers. Unknown, suspended and retired numbers are reported back; blocked ones are not.");

        group.MapPost("phones/{phoneId:guid}/apps/messages/threads/{threadId:guid}/read", async (
                Guid phoneId, Guid threadId, [FromBody] PhoneActorRequestDto request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new MarkThreadReadCommand(new PhoneDeviceId(phoneId), request.ToActor(), new MessageThreadId(threadId)),
                    cancellationToken);

                return result switch
                {
                    MarkThreadReadResult.MarkedRead => Results.NoContent(),
                    MarkThreadReadResult.NotFound => Results.Problem(
                        title: "Thread not found", statusCode: StatusCodes.Status404NotFound),
                    MarkThreadReadResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("MarkThreadRead")
            .WithDescription("Clears a conversation's unread count.");
        group.MapPost("phones/{phoneId:guid}/apps/messages/blocks", async (
                Guid phoneId,
                [FromBody] BlockNumberRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(request.Number, out var number, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new BlockNumberCommand(new PhoneDeviceId(phoneId), request.ToActor(), number),
                    cancellationToken);

                return result switch
                {
                    BlockNumberResult.Blocked => Results.NoContent(),
                    BlockNumberResult.AlreadyBlocked => Results.NoContent(),
                    BlockNumberResult.CannotBlockOwnNumber => Results.Problem(
                        title: "A phone can not block its own number", statusCode: StatusCodes.Status400BadRequest),
                    BlockNumberResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("BlockNumber")
            .WithDescription("Blocks a number. Messages from it are dropped silently — the sender still sees them as sent. Idempotent.");

        group.MapDelete("phones/{phoneId:guid}/apps/messages/blocks/{number}", async (
                Guid phoneId,
                string number,
                [FromQuery] Guid characterId,
                [FromQuery] string? pin,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(number, out var parsed, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(
                    new UnblockNumberCommand(new PhoneDeviceId(phoneId), new PhoneActor(new CharacterId(characterId), pin), parsed),
                    cancellationToken);

                return result switch
                {
                    UnblockNumberResult.Unblocked => Results.NoContent(),
                    UnblockNumberResult.NotBlocked => Results.NoContent(),
                    UnblockNumberResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .WithName("UnblockNumber")
            .WithDescription("Removes a number from the blocklist. Idempotent.");
    }

    /// <summary>
    /// Push happens here rather than in the handler because a Mediator handler can not depend on
    /// IHubContext (ARCHITECTURE.md §9e). It is best-effort by design: the hub is a delivery
    /// convenience and never the source of truth, so a client that missed a frame re-fetches.
    ///
    /// Driven off what the handler actually appended, so a queued, undeliverable or blocked
    /// recipient is never notified — in the blocked case, learning of the message at all would
    /// defeat the block.
    /// </summary>
    private static async Task NotifyRecipientsAsync(
        PhoneHubNotifier notifier,
        SendMessageResult.Sent sent,
        string body,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in sent.Deliveries)
        {
            await notifier.NotifyMessageReceivedAsync(
                delivery.PhoneId.Value,
                delivery.ThreadId.Value,
                new MessageDto(sent.MessageId.Value, sent.From.Value, body, sent.SentAt, IsOutbound: false),
                cancellationToken);
        }
    }
}
