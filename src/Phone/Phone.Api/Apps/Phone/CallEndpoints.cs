using ELifeRPG.Phone.Api.Apps.Phone;
using ELifeRPG.Phone.Api;
using ELifeRPG.Phone.Api.Common;
using ELifeRPG.Phone.Application.Apps.Phone;
using ELifeRPG.Phone.Domain.Apps.Phone;
using ELifeRPG.Phone.Domain.Devices;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static partial class PhoneModule
{
    /// <summary>
    /// The dialer, under the prefix <see cref="AppKey.Phone"/> owns. Signalling only: the backend
    /// says who is ringing whom and what has become of it, and the mod carries the audio through the
    /// game's own voice. That is the whole reason a call needs a server — two clients cannot agree on
    /// "answered", and neither can resolve a number held on another gameserver.
    /// </summary>
    private static void MapCalls(RouteGroupBuilder group)
    {
        group.MapPost("phones/{phoneId:guid}/apps/phone/calls", async (
                Guid phoneId,
                [FromBody] PlaceCallRequestDto request,
                IMediator mediator,
                PhoneHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                if (!PhoneNumberBinding.TryParse(request.To, out var to, out var problem))
                {
                    return problem!;
                }

                var result = await mediator.Send(new PlaceCallCommand(new PhoneDeviceId(phoneId), to), cancellationToken);

                // Pushed outside the switch for the reason the send path does it: the await is what
                // an expression over the union cannot carry. Only the callee is told — the caller is
                // holding the response.
                if (result is PlaceCallResult.Ringing ringing)
                {
                    var dto = PhoneCallDto.Create(ringing.Call);
                    await notifier.NotifyIncomingCallAsync(
                        ringing.Call.CalleePhoneId.Value, dto, cancellationToken);

                    return Results.Ok(new PlaceCallResponseDto(dto.CallId, dto.To, "Ringing", dto.ExpiresAt));
                }

                return result switch
                {

                    // Deliberately indistinguishable from a blocked caller — see PlaceCallResult.
                    PlaceCallResult.Unanswered unanswered => Results.Ok(
                        new PlaceCallResponseDto(null, unanswered.To.Value, "Unanswered", null)),

                    PlaceCallResult.Undeliverable undeliverable => Results.Ok(
                        new PlaceCallResponseDto(null, undeliverable.To.Value, "Undeliverable", null)),

                    PlaceCallResult.Busy busy => Results.Ok(
                        new PlaceCallResponseDto(null, busy.To.Value, "Busy", null)),

                    PlaceCallResult.CallingSelf => Results.Problem(
                        title: "A phone can not call itself", statusCode: StatusCodes.Status400BadRequest),

                    PlaceCallResult.AlreadyOnACall already => Results.Problem(
                        title: $"This phone is already on call {already.CallId.Value}",
                        statusCode: StatusCodes.Status409Conflict),

                    PlaceCallResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<PlaceCallResponseDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("PlaceCall")
            .WithDescription("Dials a number. An unreachable number and one that simply did not ring are reported the same way on purpose, so the caller can not use this to detect that they have been blocked. A call that rings is yours to answer, reject or hang up by its id; one that did not leaves nothing to act on.");

        group.MapGet("phones/{phoneId:guid}/apps/phone/calls/ringing", async (
                Guid phoneId, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RingingCallsQuery(new PhoneDeviceId(phoneId)), cancellationToken);

                return result switch
                {
                    RingingCallsResult.Calls calls => Results.Ok(calls.Entries.Select(PhoneCallDto.Create)),
                    RingingCallsResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(ReadPolicy)
            .RequireRateLimiting(RingPollRateLimitPolicy)
            .Produces<IEnumerable<PhoneCallDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("ListRingingCalls")
            .WithDescription("What is ringing this phone. The dialer's delivery path, meant to be polled every second or two — and the one phone endpoint that is rate limited, per gameserver, because it is driven by a timer rather than by a player. A ring whose window has closed is reported as nothing rather than as still ringing, and becomes a missed call here.");

        group.MapPost("phones/{phoneId:guid}/apps/phone/calls/{callId:guid}/answer", async (
                Guid phoneId, Guid callId, IMediator mediator, PhoneHubNotifier notifier, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new AnswerCallCommand(new PhoneDeviceId(phoneId), new PhoneCallId(callId)), cancellationToken);

                if (result is AnswerCallResult.Answered answered)
                {
                    return await PushAndRespondAsync(notifier, answered.Call, cancellationToken);
                }

                return result switch
                {
                    AnswerCallResult.CallNotFound => CallNotFound(),
                    AnswerCallResult.NotRinging notRinging => NotRinging(notRinging.State),
                    AnswerCallResult.NotTheCallee => NotTheCallee(),
                    AnswerCallResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<CallStateResponseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("AnswerCall")
            .WithDescription("Answers a ringing call. Only the phone being rung may. A call whose ring window closed between the poll and this is already missed, and answering it is a 409 rather than a surprise connection.");

        group.MapPost("phones/{phoneId:guid}/apps/phone/calls/{callId:guid}/reject", async (
                Guid phoneId, Guid callId, IMediator mediator, PhoneHubNotifier notifier, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RejectCallCommand(new PhoneDeviceId(phoneId), new PhoneCallId(callId)), cancellationToken);

                if (result is RejectCallResult.Rejected rejected)
                {
                    return await PushAndRespondAsync(notifier, rejected.Call, cancellationToken);
                }

                return result switch
                {
                    RejectCallResult.CallNotFound => CallNotFound(),
                    RejectCallResult.NotRinging notRinging => NotRinging(notRinging.State),
                    RejectCallResult.NotTheCallee => NotTheCallee(),
                    RejectCallResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<CallStateResponseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("RejectCall")
            .WithDescription("Declines a ringing call. Recorded as rejected rather than missed: the callee decided, and a call log that conflated the two would lose the only interesting thing about it.");

        group.MapPost("phones/{phoneId:guid}/apps/phone/calls/{callId:guid}/end", async (
                Guid phoneId, Guid callId, IMediator mediator, PhoneHubNotifier notifier, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new EndCallCommand(new PhoneDeviceId(phoneId), new PhoneCallId(callId)), cancellationToken);

                if (result is EndCallResult.Ended ended)
                {
                    return await PushAndRespondAsync(notifier, ended.Call, cancellationToken);
                }

                return result switch
                {
                    EndCallResult.CallNotFound => CallNotFound(),
                    EndCallResult.AlreadyOver over => NotRinging(over.State),
                    EndCallResult.NotAParticipant => Results.Problem(
                        title: "This phone is not on that call", statusCode: StatusCodes.Status403Forbidden),
                    EndCallResult.AccessDenied denied => PhoneAccessProblem.ToResult(denied.Reason),
                };
            })
            .RequireAuthorization(WritePolicy)
            .Produces<CallStateResponseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithName("EndCall")
            .WithDescription("Puts the phone down, from either end. What that means depends on whether it had been answered: hanging up on a call still ringing leaves the other side a missed call, and hanging up on an answered one ends it — the response says which.");
    }

    /// <summary>
    /// Both legs are told, because either may have been the one who acted and each needs the other's
    /// half of the transition — the callee needs to know the caller hung up as much as the reverse.
    /// </summary>
    private static async Task<IResult> PushAndRespondAsync(
        PhoneHubNotifier notifier, PhoneCall call, CancellationToken cancellationToken)
    {
        var dto = PhoneCallDto.Create(call);

        await notifier.NotifyCallStateChangedAsync(call.CalleePhoneId.Value, dto, cancellationToken);
        await notifier.NotifyCallStateChangedAsync(call.CallerPhoneId.Value, dto, cancellationToken);

        return Results.Ok(new CallStateResponseDto(dto.CallId, dto.State));
    }

    private static IResult CallNotFound() => Results.Problem(
        title: "Call not found", statusCode: StatusCodes.Status404NotFound);

    private static IResult NotRinging(CallState state) => Results.Problem(
        title: $"Call is {state} and can no longer be acted on", statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// 403 rather than 404: the call exists and the caller may well be on it, just not as the party
    /// being rung. Hiding it behind a 404 would also tell a prober less than it would cost in
    /// confusion for the legitimate case.
    /// </summary>
    private static IResult NotTheCallee() => Results.Problem(
        title: "Only the phone being called may answer or reject it", statusCode: StatusCodes.Status403Forbidden);
}
