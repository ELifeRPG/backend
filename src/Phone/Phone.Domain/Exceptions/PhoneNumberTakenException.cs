namespace ELifeRPG.Phone.Domain.Exceptions;

/// <summary>
/// The database rejected a phone because its number is already issued. Surfaced as a domain exception
/// so Phone.Application can react without referencing Marten or Npgsql — the same translation
/// MartenShopListingRepository does for a purchase conflict.
/// </summary>
public class PhoneNumberTakenException(string message) : Exception(message);
