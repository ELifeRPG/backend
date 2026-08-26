using ELifeRPG.Phone.Api;
using ELifeRPG.Phone.Api.Apps.Messages;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Api.Devices;
using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages;
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
    private static void MapMessages(RouteGroupBuilder group)
    {
        group.MapGet("sim-cards/{simCardId:guid}/threads", async (
                Guid simCardId, [FromQuery] Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ThreadsQuery(new SimCardId(simCardId), new CharacterId(characterId)), cancellationToken);

                return result switch
                {
                    ThreadsResult.Threads threads => Results.Ok(threads.Entries.Select(MessageThreadSummaryDto.Create)),
                    ThreadsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .Produces<IEnumerable<MessageThreadSummaryDto>>()
            .WithName("ListThreads")
            .WithDescription("Lists a SIM's conversations, newest first. Bodies are omitted; open a thread for those.");

        group.MapGet("sim-cards/{simCardId:guid}/threads/{threadId:guid}", async (
                Guid simCardId, Guid threadId, [FromQuery] Guid characterId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new ThreadQuery(new SimCardId(simCardId), new CharacterId(characterId), new MessageThreadId(threadId)),
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

        group.MapPost("sim-cards/{simCardId:guid}/messages", async (
                Guid simCardId,
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
                    new SendMessageCommand(new SimCardId(simCardId), new CharacterId(request.CharacterId), recipients, request.Body),
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
                        title: $"This handset allows at most {tooMany.MaxParticipants} group participants",
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

        group.MapPost("sim-cards/{simCardId:guid}/threads/{threadId:guid}/read", async (
                Guid simCardId, Guid threadId, [FromBody] ActingCharacterRequestDto request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new MarkThreadReadCommand(new SimCardId(simCardId), new CharacterId(request.CharacterId), new MessageThreadId(threadId)),
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
                delivery.SimCardId.Value,
                delivery.ThreadId.Value,
                new MessageDto(sent.MessageId.Value, sent.From.Value, body, sent.SentAt, IsOutbound: false),
                cancellationToken);
        }
    }
}
