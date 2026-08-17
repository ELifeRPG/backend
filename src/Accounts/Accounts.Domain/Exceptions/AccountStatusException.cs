namespace ELifeRPG.Accounts.Domain.Exceptions;

public sealed class AccountStatusException(string message) : InvalidOperationException(message);
