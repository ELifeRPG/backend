using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>
/// One Marten session shared by every World repository in a DI scope — copies
/// <c>Phone.Infrastructure.Common.PhoneSession</c>'s shape verbatim.
///
/// Other modules give each repository its own session, which works because none of their commands
/// writes to two documents at once. World's do, routinely: granting N items writes N
/// <see cref="ItemInstance"/> rows in one call (task 3), and a container move can touch the moved
/// instance plus every descendant whose root fields need rewriting. Separate sessions would commit
/// those separately, and a grant that mints seven of ten rows before failing is exactly the kind of
/// state this module must never reach.
///
/// The consequence to keep in mind: this is a unit of work. Calling SaveChangesAsync on any
/// repository commits everything pending in the scope, not just that repository's writes.
///
/// A secondary Marten store gets no DI-injected scoped session of its own, which is why this wrapper
/// exists rather than registering IDocumentSession directly.
/// </summary>
public interface IWorldSession
{
    IDocumentSession Session { get; }
}

public sealed class WorldSession(IWorldStore store) : IWorldSession, IAsyncDisposable
{
    private readonly Lazy<IDocumentSession> _session = new(store.LightweightSession);

    public IDocumentSession Session => _session.Value;

    public async ValueTask DisposeAsync()
    {
        if (_session.IsValueCreated)
        {
            await _session.Value.DisposeAsync();
        }
    }
}
