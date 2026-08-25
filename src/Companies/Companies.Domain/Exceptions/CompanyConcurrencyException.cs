namespace ELifeRPG.Companies.Domain.Exceptions;

public sealed class CompanyConcurrencyException(string message) : InvalidOperationException(message);
