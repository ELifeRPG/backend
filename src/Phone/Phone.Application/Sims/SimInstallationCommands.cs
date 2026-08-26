using ELifeRPG.Phone.Application.Apps.Messages;
using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Sims;

public union InstallSimResult(
    InstallSimResult.Installed,
    InstallSimResult.SimNotFound,
    InstallSimResult.NotSimOwner,
    InstallSimResult.SimDeactivated,
    InstallSimResult.SimAlreadyInstalled,
    InstallSimResult.DeviceNotFound,
    InstallSimResult.NotDeviceOwner,
    InstallSimResult.ModelNotFound,
    InstallSimResult.NoFreeSimSlot)
{
    public record Installed;

    public record SimNotFound;

    public record NotSimOwner;

    public record SimDeactivated;

    public record SimAlreadyInstalled;

    public record DeviceNotFound;

    public record NotDeviceOwner;

    public record ModelNotFound;

    public record NoFreeSimSlot;
}

/// <summary>
/// Seats a SIM in a handset, which is how contacts and message history move between devices — they
/// live on the SIM, so they simply travel with it.
///
/// Both ownerships are checked against the same acting character: a SIM only works in a handset
/// bound to its own owner, so neither half is worth stealing. A suspended SIM may still be seated —
/// the lock is on the network, not the slot — it just cannot then be used.
/// </summary>
public sealed record InstallSimCommand(PhoneDeviceId DeviceId, SimCardId SimCardId, CharacterId ActingCharacterId)
    : IRequest<InstallSimResult>;

public sealed class InstallSimHandler(
    IPhoneDeviceRepository deviceRepository,
    ISimCardRepository simCardRepository,
    IPhoneModelRepository modelRepository,
    IMediator mediator)
    : IRequestHandler<InstallSimCommand, InstallSimResult>
{
    public async ValueTask<InstallSimResult> Handle(InstallSimCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new InstallSimResult.SimNotFound();
        }

        if (sim.RegisteredTo != request.ActingCharacterId)
        {
            return new InstallSimResult.NotSimOwner();
        }

        if (sim.Status == SimCardStatus.Deactivated)
        {
            return new InstallSimResult.SimDeactivated();
        }

        if (sim.InstalledIn is not null)
        {
            return new InstallSimResult.SimAlreadyInstalled();
        }

        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return new InstallSimResult.DeviceNotFound();
        }

        if (device.BoundCharacterId != request.ActingCharacterId)
        {
            return new InstallSimResult.NotDeviceOwner();
        }

        var model = await modelRepository.FindByIdAsync(device.ModelId, cancellationToken);
        if (model is null)
        {
            return new InstallSimResult.ModelNotFound();
        }

        if (device.InstalledSims.Count >= model.SimSlots)
        {
            return new InstallSimResult.NoFreeSimSlot();
        }

        // Both appends land on the shared PhoneSession, so they commit as one — a device holding a
        // SIM that does not know it is seated would be a state nothing could recover from.
        deviceRepository.Append(request.DeviceId, device.InstallSim(request.SimCardId, model));
        simCardRepository.Append(request.SimCardId, sim.InstallInto(request.DeviceId));
        await deviceRepository.SaveChangesAsync(cancellationToken);

        // The other moment a number becomes reachable. No-op unless the handset is also powered on.
        await mediator.Send(new FlushPendingDeliveriesCommand(request.SimCardId), cancellationToken);

        return new InstallSimResult.Installed();
    }
}

public union EjectSimResult(
    EjectSimResult.Ejected,
    EjectSimResult.SimNotFound,
    EjectSimResult.NotSimOwner,
    EjectSimResult.SimNotInThisDevice)
{
    public record Ejected;

    public record SimNotFound;

    public record NotSimOwner;

    public record SimNotInThisDevice;
}

public sealed record EjectSimCommand(PhoneDeviceId DeviceId, SimCardId SimCardId, CharacterId ActingCharacterId)
    : IRequest<EjectSimResult>;

public sealed class EjectSimHandler(IPhoneDeviceRepository deviceRepository, ISimCardRepository simCardRepository)
    : IRequestHandler<EjectSimCommand, EjectSimResult>
{
    public async ValueTask<EjectSimResult> Handle(EjectSimCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new EjectSimResult.SimNotFound();
        }

        if (sim.RegisteredTo != request.ActingCharacterId)
        {
            return new EjectSimResult.NotSimOwner();
        }

        if (sim.InstalledIn != request.DeviceId)
        {
            return new EjectSimResult.SimNotInThisDevice();
        }

        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || !device.InstalledSims.Contains(request.SimCardId))
        {
            // The SIM says it is seated here but the handset disagrees — a torn write that the
            // shared session is meant to make impossible. Report it as "not in this device" rather
            // than half-ejecting and deepening the inconsistency.
            return new EjectSimResult.SimNotInThisDevice();
        }

        deviceRepository.Append(request.DeviceId, device.EjectSim(request.SimCardId));
        simCardRepository.Append(request.SimCardId, sim.Eject());
        await deviceRepository.SaveChangesAsync(cancellationToken);

        return new EjectSimResult.Ejected();
    }
}
