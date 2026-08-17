namespace ELifeRPG.Shared.Kernel;

/// <summary>Owned by the Accounts module; lives here so other modules can reference it without depending on Accounts.Domain (see ARCHITECTURE.md §9e).</summary>
[StronglyTypedId]
public partial struct AccountId;
