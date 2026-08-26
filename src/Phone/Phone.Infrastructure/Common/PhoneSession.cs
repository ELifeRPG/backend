using Marten;

namespace ELifeRPG.Phone.Infrastructure.Common;

/// <summary>
/// One Marten session shared by every Phone repository in a DI scope.
///
/// The other modules give each repository its own session, which works because none of their
/// commands writes to two aggregates at once. Phone's do, routinely: installing a SIM appends to
/// both the device stream and the SIM stream, and a send appends to the sender's thread and every
/// recipient's. Separate sessions would commit those separately, and a device claiming a SIM that
/// is not installed — or a message in the sender's history but nobody's inbox — is exactly the
/// state this module must never reach.
///
/// The consequence to keep in mind: this is a unit of work. Calling SaveChangesAsync on any
/// repository commits everything pending in the scope, not just that repository's writes.
///
/// A secondary Marten store gets no DI-injected scoped session of its own, which is why this wrapper
/// exists rather than registering IDocumentSession directly.
/// </summary>
public interface IPhoneSession
{
    IDocumentSession Session { get; }
}

public sealed class PhoneSession(IPhoneStore store) : IPhoneSession, IAsyncDisposable
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
