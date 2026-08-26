using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Messages;

public sealed record FlushPendingDeliveriesResult(int Delivered, int StillPending);

/// <summary>
/// Delivers whatever piled up while a SIM was loose, its handset was off, or the Messages app was
/// uninstalled. Fired after powering on and after seating a SIM, which are exactly the moments a
/// number becomes reachable again.
///
/// Safe to run at any time: a SIM that is still unreachable simply leaves everything queued, and
/// each delivery is removed from the queue in the same commit that appends it to the thread.
/// </summary>
public sealed record FlushPendingDeliveriesCommand(SimCardId SimCardId) : IRequest<FlushPendingDeliveriesResult>;

public sealed class FlushPendingDeliveriesHandler(
    ISimCardRepository simCardRepository,
    IPhoneDeviceRepository deviceRepository,
    IPhoneModelRepository modelRepository,
    IMessageThreadRepository threadRepository)
    : IRequestHandler<FlushPendingDeliveriesCommand, FlushPendingDeliveriesResult>
{
    public async ValueTask<FlushPendingDeliveriesResult> Handle(FlushPendingDeliveriesCommand request, CancellationToken cancellationToken)
    {
        var pending = await threadRepository.FindPendingForSimAsync(request.SimCardId, cancellationToken);
        if (pending.Count == 0)
        {
            return new FlushPendingDeliveriesResult(0, 0);
        }

        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);

        // A suspended SIM keeps its backlog rather than losing it — the lock stops delivery, and a
        // restore is meant to hand the number back whole.
        if (sim is null || sim.Status != SimCardStatus.Active || sim.InstalledIn is not { } deviceId)
        {
            return new FlushPendingDeliveriesResult(0, pending.Count);
        }

        var device = await deviceRepository.FindByIdAsync(deviceId, cancellationToken);
        if (device is null || !device.IsPoweredOn || !device.HasApp(AppKey.Messages))
        {
            return new FlushPendingDeliveriesResult(0, pending.Count);
        }

        var model = await modelRepository.FindByIdAsync(device.ModelId, cancellationToken);
        if (model is null)
        {
            return new FlushPendingDeliveriesResult(0, pending.Count);
        }

        foreach (var delivery in pending)
        {
            var thread = await MessageThreads.FindOrStartAsync(
                threadRepository, sim.Id, sim.Number, delivery.Participants, model, cancellationToken);

            threadRepository.Append(
                thread.Id,
                thread.RecordInbound(delivery.MessageId, delivery.From, delivery.Body, delivery.SentAt, model));

            threadRepository.DeletePending(delivery.Id);
        }

        // One commit for the appends and the dequeues together, so a message can never be both
        // delivered and still waiting — nor dropped without ever arriving.
        await threadRepository.SaveChangesAsync(cancellationToken);

        return new FlushPendingDeliveriesResult(pending.Count, 0);
    }
}
