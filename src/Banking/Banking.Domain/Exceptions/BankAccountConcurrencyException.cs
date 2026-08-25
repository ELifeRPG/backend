namespace ELifeRPG.Banking.Domain.Exceptions;

public sealed class BankAccountConcurrencyException(string message) : InvalidOperationException(message);
