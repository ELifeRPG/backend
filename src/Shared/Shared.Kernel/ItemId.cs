namespace ELifeRPG.Shared.Kernel;

/// <summary>Owned by the Items module; lives here so other modules (Shops) can reference it without depending on Items.Domain.</summary>
[StronglyTypedId]
public partial struct ItemId;
