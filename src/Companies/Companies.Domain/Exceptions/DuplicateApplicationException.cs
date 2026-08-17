namespace ELifeRPG.Companies.Domain.Exceptions;

/// <summary>Thrown by Company.SubmitApplication when the character already has an open
/// (Pending/InProgress) application to this company. Reapplying after a Denied application is allowed.</summary>
public sealed class DuplicateApplicationException(string message) : InvalidOperationException(message);
