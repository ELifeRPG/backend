namespace ELifeRPG.Accounts.Application.Common;

public interface ITokenRevocationStore
{
    void Revoke(string jti, DateTimeOffset expiresAt);

    bool IsRevoked(string jti);
}
