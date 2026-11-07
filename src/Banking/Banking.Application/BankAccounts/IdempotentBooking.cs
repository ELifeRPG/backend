using ELifeRPG.Banking.Application.Common;

namespace ELifeRPG.Banking.Application.BankAccounts;

/// <summary>
/// The <c>Idempotency-Key</c> side of one deposit, withdraw or transfer request: where its receipt lives
/// and what the request looked like. The three handlers share the same steps:
/// <list type="number">
/// <item>Before booking, <see cref="LookUpAsync"/>. A live receipt with the same fingerprint is answered
/// from the receipt; a different fingerprint is a reused key.</item>
/// <item>After booking, <see cref="Record"/> in the same session, so the receipt and the events commit
/// together.</item>
/// <item>When SaveChangesAsync fails with a concurrency conflict, look up again: a racing request with
/// the same key may have committed, and its answer is this request's answer too.</item>
/// </list>
/// Internal callers that pass no key get a plain, non-idempotent booking. The HTTP endpoints always
/// require one.
/// </summary>
internal sealed record IdempotentBooking(string ReceiptId, GameServerId GameServerId, string Key, string Fingerprint)
{
    public static IdempotentBooking? For(string? key, GameServerId gameServerId, string operation, params object?[] fields)
        => key is null
            ? null
            : new(BookingReceipt.BuildKey(gameServerId, key), gameServerId, key, BookingReceipt.ComputeFingerprint(operation, fields));

    /// <summary>The live receipt for this key, if any, and whether an expired one is in its place.</summary>
    public async ValueTask<(BookingReceipt? Live, bool ExpiredExists)> LookUpAsync(
        IBankAccountRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var receipt = await repository.FindReceiptAsync(ReceiptId, cancellationToken);
        if (receipt is null)
        {
            return (null, false);
        }

        return receipt.IsExpired(timeProvider.GetUtcNow()) ? (null, true) : (receipt, false);
    }

    public bool Matches(BookingReceipt receipt) => receipt.Fingerprint == Fingerprint;

    public void Record(
        IBankAccountRepository repository,
        TimeProvider timeProvider,
        bool replacesExpired,
        BankTransactionId transactionId,
        decimal amount,
        decimal fee,
        decimal newBalance)
        => repository.AddReceipt(
            new BookingReceipt
            {
                Id = ReceiptId,
                GameServerId = GameServerId,
                Key = Key,
                Fingerprint = Fingerprint,
                TransactionId = transactionId,
                Amount = amount,
                Fee = fee,
                NewBalance = newBalance,
                BookedAt = timeProvider.GetUtcNow(),
            },
            replacesExpired);
}
