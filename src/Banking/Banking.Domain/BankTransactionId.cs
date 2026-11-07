namespace ELifeRPG.Banking.Domain;

/// <summary>
/// Identifies one booking. Both legs of a transfer — the source's TransferredOut and the target's
/// TransferredIn — carry the same id, because they are one booking.
/// </summary>
[StronglyTypedId]
public partial struct BankTransactionId;
