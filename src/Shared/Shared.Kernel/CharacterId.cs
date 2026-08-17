namespace ELifeRPG.Shared.Kernel;

/// <summary>Owned by the Characters module; lives here so other modules can reference it without depending on Characters.Domain (see ARCHITECTURE.md §9e).</summary>
[StronglyTypedId]
public partial struct CharacterId;
