namespace ELifeRPG.Banking.Domain.Exceptions;

public sealed class InsufficientBalanceException(string message) : InvalidOperationException(message);
