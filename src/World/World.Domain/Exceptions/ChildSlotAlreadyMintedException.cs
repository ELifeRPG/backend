namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised by <c>MartenItemInstanceRepository.SaveChangesAsync</c> when the partial unique index on
/// <c>(ContainerInstanceId, Slot)</c> (see <c>World.Infrastructure/ServiceCollectionExtensions.cs</c>)
/// rejects an insert — two concurrent acks for the same parent+slot both saw no existing child (via
/// <c>IItemInstanceRepository.FindChildrenAsync</c>) and both tried to mint one. The Bridge is
/// store-and-forward with Polly retries, so a client-side timeout followed by a retry while the first
/// request is still executing is exactly this shape; a plain read-then-write with no DB constraint
/// cannot close it. Like the other domain guard exceptions in this namespace, this is meant to be
/// caught by an Application handler (<c>AcknowledgeSpawnsHandler</c>) and reconciled — re-verify every
/// speculatively-minted child from the failed call against a fresh read, eject the losers, and report
/// the winner's id — rather than propagate as a 500.
/// </summary>
public sealed class ChildSlotAlreadyMintedException()
    : InvalidOperationException(
        "A concurrent ack already minted a child for at least one (parent, slot) pair in this batch.");
