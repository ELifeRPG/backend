namespace ELifeRPG.Companies.Domain.Exceptions;

/// <summary>
/// Deliberate improvement over the legacy domain: legacy's Company.AddMembership has no guard
/// against adding the same character twice. This codebase enforces uniqueness instead of silently
/// allowing duplicate membership rows.
/// </summary>
public sealed class AlreadyMemberException(string message) : InvalidOperationException(message);
