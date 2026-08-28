namespace ELifeRPG.Shared.Integration.Abstractions;

/// <summary>
/// One shared database transaction spanning multiple modules' Marten sessions. Obtain module-scoped
/// repositories via <see cref="Enlist{TRepository}"/>, append/save through them as normal, then call
/// CommitAsync once. Disposing without committing rolls back — there is no partial-success state.
/// </summary>
public interface ICrossModuleTransaction : IAsyncDisposable
{
    CrossModuleSessionHandle Handle { get; }

    /// <summary>
    /// Resolves the registered <see cref="ITransactionParticipant{TRepository}"/> for
    /// <typeparamref name="TRepository"/> and enlists it in this transaction, returning a repository
    /// bound to the shared connection. Replaces the per-module I&lt;X&gt;RepositoryFactory pattern:
    /// one abstraction rather than one interface plus one implementation per participating module.
    /// The returned repository is owned by this transaction and must never be disposed by the caller.
    /// </summary>
    TRepository Enlist<TRepository>() where TRepository : notnull;

    Task CommitAsync(CancellationToken cancellationToken);
}
