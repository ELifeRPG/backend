using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Sims;

public union SuspendSimResult(
    SuspendSimResult.Suspended,
    SuspendSimResult.AlreadySuspended,
    SuspendSimResult.SimNotFound,
    SuspendSimResult.SimDeactivated)
{
    public record Suspended;

    public record AlreadySuspended;

    public record SimNotFound;

    public record SimDeactivated;
}

/// <summary>
/// External control over a number: staff today under `phone:enforce`, an in-game Police/State
/// faction later without the domain changing at all.
///
/// Deliberately has no acting-character ownership check — the point of an enforcement action is that
/// the owner does not consent to it. A suspended SIM can neither send nor receive, and messages
/// addressed to it are dropped rather than queued, since holding them for later would make the lock
/// a delay instead of a block. Nothing is lost: contacts, threads and the blocklist all survive, and
/// a restore brings the number back whole.
/// </summary>
public sealed record SuspendSimCommand(SimCardId SimCardId, string Reason) : IRequest<SuspendSimResult>;

public sealed class SuspendSimHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<SuspendSimCommand, SuspendSimResult>
{
    public async ValueTask<SuspendSimResult> Handle(SuspendSimCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new SuspendSimResult.SimNotFound();
        }

        if (sim.Status == SimCardStatus.Deactivated)
        {
            return new SuspendSimResult.SimDeactivated();
        }

        if (sim.Status == SimCardStatus.Suspended)
        {
            return new SuspendSimResult.AlreadySuspended();
        }

        simCardRepository.Append(request.SimCardId, sim.Suspend(request.Reason));
        await simCardRepository.SaveChangesAsync(cancellationToken);

        return new SuspendSimResult.Suspended();
    }
}

public union RestoreSimResult(
    RestoreSimResult.Restored,
    RestoreSimResult.NotSuspended,
    RestoreSimResult.SimNotFound)
{
    public record Restored;

    /// <summary>Covers a deactivated SIM too: deactivation is terminal, and a restore must not resurrect a retired number.</summary>
    public record NotSuspended;

    public record SimNotFound;
}

public sealed record RestoreSimCommand(SimCardId SimCardId) : IRequest<RestoreSimResult>;

public sealed class RestoreSimHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<RestoreSimCommand, RestoreSimResult>
{
    public async ValueTask<RestoreSimResult> Handle(RestoreSimCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new RestoreSimResult.SimNotFound();
        }

        if (sim.Status != SimCardStatus.Suspended)
        {
            return new RestoreSimResult.NotSuspended();
        }

        simCardRepository.Append(request.SimCardId, sim.Restore());
        await simCardRepository.SaveChangesAsync(cancellationToken);

        return new RestoreSimResult.Restored();
    }
}
