namespace ELifeRPG.Shared.Kernel;

/// <summary>Owned by the Companies module; lives here so other modules can reference it without depending on Companies.Domain (see ARCHITECTURE.md §9e).</summary>
[StronglyTypedId]
public partial struct CompanyId;
