namespace ELifeRPG.Companies.Domain.Exceptions;

public sealed class ApplicationNotFoundException(string message) : InvalidOperationException(message);
