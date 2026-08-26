namespace ELifeRPG.Phone.Domain.Exceptions;

public class ContactLimitReachedException(string message) : Exception(message);
