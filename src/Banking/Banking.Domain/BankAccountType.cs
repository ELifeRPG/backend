namespace ELifeRPG.Banking.Domain;

/// <summary>Matches the legacy app's BankAccountType (Personal/Corporate) — see ARCHITECTURE.md §9e.</summary>
public enum BankAccountType
{
    Personal = 1,
    Corporate = 2,
}
