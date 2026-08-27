namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised by <see cref="ELifeRPG.World.Domain.Items.ItemAttributes"/> when a caller supplies more
/// keys than <see cref="ELifeRPG.World.Domain.Items.ItemAttributes.MaxKeys"/> allows, or a key/value
/// past its length cap. The bag is validated on construction so an oversized attribute set can never
/// reach storage in the first place.
/// </summary>
public sealed class AttributeLimitExceededException(string message) : InvalidOperationException(message);
