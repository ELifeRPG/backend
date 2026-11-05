namespace ELifeRPG.Banking.Domain.Exceptions;

/// <summary>A newly generated account number collided with an existing one on the unique index.</summary>
public sealed class BankAccountNumberTakenException(string message) : InvalidOperationException(message);
