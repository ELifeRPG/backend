namespace ELifeRPG.Shared.Integration.Abstractions;

/// <summary>
/// One shared database transaction spanning multiple modules' Marten sessions. Obtain module-scoped
/// repositories via each module's own "I&lt;X&gt;RepositoryFactory.CreateFor(Handle)", append/save
/// through them as normal, then call CommitAsync once. Disposing without committing rolls back —
/// there is no partial-success state. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface ICrossModuleTransaction : IAsyncDisposable
{
    CrossModuleSessionHandle Handle { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
