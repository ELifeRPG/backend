namespace ELifeRPG.Accounts.Domain;

public static class KeycloakUsername
{
    public static string For(GameId bohemiaId) => $"bohemia_{bohemiaId}";
}
