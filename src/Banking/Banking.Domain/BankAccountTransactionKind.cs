namespace ELifeRPG.Banking.Domain;

public enum BankAccountTransactionKind
{
    Deposited = 1,
    Withdrawn = 2,
    TransferredOut = 3,
    TransferredIn = 4,
}
