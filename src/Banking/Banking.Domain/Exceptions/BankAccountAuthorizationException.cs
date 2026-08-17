namespace ELifeRPG.Banking.Domain.Exceptions;

public sealed class BankAccountAuthorizationException(string message) : InvalidOperationException(message);
