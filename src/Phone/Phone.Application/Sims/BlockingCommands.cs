using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Sims;

public union BlockNumberResult(
    BlockNumberResult.Blocked,
    BlockNumberResult.AlreadyBlocked,
    BlockNumberResult.CannotBlockOwnNumber,
    BlockNumberResult.SimNotFound,
    BlockNumberResult.NotSimOwner,
    BlockNumberResult.SimDeactivated)
{
    public record Blocked;

    public record AlreadyBlocked;

    public record CannotBlockOwnNumber;

    public record SimNotFound;

    public record NotSimOwner;

    public record SimDeactivated;
}

/// <summary>
/// Blocking is a platform concern rather than a Messages one: it is one number refusing another, and
/// a later Calls app has to honour the same list. It also works on a SIM that is not currently
/// seated in any handset — you can shut someone out without putting the card in a phone first.
/// </summary>
public sealed record BlockNumberCommand(SimCardId SimCardId, CharacterId ActingCharacterId, PhoneNumber Number)
    : IRequest<BlockNumberResult>;

public sealed class BlockNumberHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<BlockNumberCommand, BlockNumberResult>
{
    public async ValueTask<BlockNumberResult> Handle(BlockNumberCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new BlockNumberResult.SimNotFound();
        }

        if (sim.RegisteredTo != request.ActingCharacterId)
        {
            return new BlockNumberResult.NotSimOwner();
        }

        if (sim.Status == SimCardStatus.Deactivated)
        {
            return new BlockNumberResult.SimDeactivated();
        }

        if (request.Number == sim.Number)
        {
            return new BlockNumberResult.CannotBlockOwnNumber();
        }

        if (sim.IsBlocked(request.Number))
        {
            return new BlockNumberResult.AlreadyBlocked();
        }

        simCardRepository.Append(request.SimCardId, sim.Block(request.Number));
        await simCardRepository.SaveChangesAsync(cancellationToken);

        return new BlockNumberResult.Blocked();
    }
}

public union UnblockNumberResult(
    UnblockNumberResult.Unblocked,
    UnblockNumberResult.NotBlocked,
    UnblockNumberResult.SimNotFound,
    UnblockNumberResult.NotSimOwner)
{
    public record Unblocked;

    public record NotBlocked;

    public record SimNotFound;

    public record NotSimOwner;
}

public sealed record UnblockNumberCommand(SimCardId SimCardId, CharacterId ActingCharacterId, PhoneNumber Number)
    : IRequest<UnblockNumberResult>;

public sealed class UnblockNumberHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<UnblockNumberCommand, UnblockNumberResult>
{
    public async ValueTask<UnblockNumberResult> Handle(UnblockNumberCommand request, CancellationToken cancellationToken)
    {
        var sim = await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken);
        if (sim is null)
        {
            return new UnblockNumberResult.SimNotFound();
        }

        if (sim.RegisteredTo != request.ActingCharacterId)
        {
            return new UnblockNumberResult.NotSimOwner();
        }

        if (!sim.IsBlocked(request.Number))
        {
            return new UnblockNumberResult.NotBlocked();
        }

        simCardRepository.Append(request.SimCardId, sim.Unblock(request.Number));
        await simCardRepository.SaveChangesAsync(cancellationToken);

        return new UnblockNumberResult.Unblocked();
    }
}
