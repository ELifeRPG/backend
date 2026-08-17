using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Tokens;

public sealed record RevokeTokenCommand(string Jti, DateTimeOffset ExpiresAt) : IRequest<Unit>;

public sealed class RevokeTokenHandler(ITokenRevocationStore revocationStore) : IRequestHandler<RevokeTokenCommand, Unit>
{
    public ValueTask<Unit> Handle(RevokeTokenCommand request, CancellationToken cancellationToken)
    {
        revocationStore.Revoke(request.Jti, request.ExpiresAt);
        return ValueTask.FromResult(Unit.Value);
    }
}
