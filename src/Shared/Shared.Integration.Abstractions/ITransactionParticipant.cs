namespace ELifeRPG.Shared.Integration.Abstractions;

public interface ITransactionParticipant<out TRepository>
{
    TRepository EnlistIn(CrossModuleSessionHandle handle);
}
