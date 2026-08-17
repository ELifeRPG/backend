namespace ELifeRPG.Accounts.Domain.Exceptions;

public sealed class WhitelistApplicationStatusException(string message) : InvalidOperationException(message);
