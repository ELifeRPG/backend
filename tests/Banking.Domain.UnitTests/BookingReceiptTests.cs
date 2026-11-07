using ELifeRPG.Banking.Domain;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Banking.Domain.UnitTests;

public sealed class BookingReceiptTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Character = Guid.NewGuid();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("k", true)]
    public void IsValidKey_NeedsOneCharacter(string? key, bool expected)
    {
        Assert.Equal(expected, BookingReceipt.IsValidKey(key));
    }

    [Fact]
    public void IsValidKey_AllowsUpToTheMaximumLength()
    {
        Assert.True(BookingReceipt.IsValidKey(new string('k', BookingReceipt.MaxKeyLength)));
        Assert.False(BookingReceipt.IsValidKey(new string('k', BookingReceipt.MaxKeyLength + 1)));
    }

    [Fact]
    public void BuildKey_ScopesTheKeyToTheGameServer()
    {
        var gameServerId = new GameServerId(Guid.NewGuid());

        Assert.Equal($"{gameServerId.Value}:atm-1-42", BookingReceipt.BuildKey(gameServerId, "atm-1-42"));
        Assert.NotEqual(
            BookingReceipt.BuildKey(new GameServerId(Guid.NewGuid()), "atm-1-42"),
            BookingReceipt.BuildKey(new GameServerId(Guid.NewGuid()), "atm-1-42"));
    }

    [Fact]
    public void ComputeFingerprint_IsTheSameForTheSameRequest()
    {
        Assert.Equal(
            BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10m, "atm-1"),
            BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10.00m, "atm-1"));
    }

    [Fact]
    public void ComputeFingerprint_ChangesWithEveryField()
    {
        var baseline = BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10m, "atm-1");

        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("withdraw", Account, Character, 10m, "atm-1"));
        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("deposit", Guid.NewGuid(), Character, 10m, "atm-1"));
        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("deposit", Account, Guid.NewGuid(), 10m, "atm-1"));
        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10.01m, "atm-1"));
        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10m, "atm-2"));
        Assert.NotEqual(baseline, BookingReceipt.ComputeFingerprint("deposit", Account, Character, 10m, null));
    }

    [Fact]
    public void IsExpired_AfterTheRetention()
    {
        var bookedAt = DateTimeOffset.UtcNow;
        var receipt = new BookingReceipt
        {
            Id = "x",
            GameServerId = new GameServerId(Guid.NewGuid()),
            Key = "x",
            Fingerprint = "x",
            TransactionId = new BankTransactionId(Guid.NewGuid()),
            Amount = 1m,
            Fee = 0m,
            NewBalance = 1m,
            BookedAt = bookedAt,
        };

        Assert.False(receipt.IsExpired(bookedAt + BookingReceipt.Retention));
        Assert.True(receipt.IsExpired(bookedAt + BookingReceipt.Retention + TimeSpan.FromTicks(1)));
    }
}
