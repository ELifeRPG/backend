using System.Collections.Concurrent;
using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Infrastructure.Common;

/// <summary>
/// In-memory, lost on restart — same tradeoff src/Bridge/Bridge.Host/PlayerSessionTracker.cs
/// already makes. Worst case on a Central API restart, a revoked-but-not-yet-expired token briefly
/// works again, still bounded by its own original TTL (~5 min).
/// </summary>
public sealed class InMemoryTokenRevocationStore : ITokenRevocationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new();

    public void Revoke(string jti, DateTimeOffset expiresAt) => _revoked[jti] = expiresAt;

    public bool IsRevoked(string jti)
    {
        if (!_revoked.TryGetValue(jti, out var expiresAt))
        {
            return false;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            _revoked.TryRemove(jti, out _);
            return false;
        }

        return true;
    }
}
