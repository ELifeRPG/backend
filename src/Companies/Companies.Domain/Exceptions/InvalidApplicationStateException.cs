namespace ELifeRPG.Companies.Domain.Exceptions;

public sealed class InvalidApplicationStateException(string message) : InvalidOperationException(message);
