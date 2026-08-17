using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Minimal Keycloak admin client used only to clean up test data after integration tests run —
/// not a production component. Mirrors Accounts.IntegrationTests/KeycloakTestClient.cs.
/// </summary>
internal sealed class KeycloakTestClient
{
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://keycloak:8080/") };

    public async Task DeleteUserAsync(string username)
    {
        var adminToken = await GetAdminTokenAsync();

        using var lookupRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"admin/realms/eliferpg/users?username={Uri.EscapeDataString(username)}&exact=true");
        lookupRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var lookupResponse = await _httpClient.SendAsync(lookupRequest);
        lookupResponse.EnsureSuccessStatusCode();

        var users = await lookupResponse.Content.ReadFromJsonAsync<List<KeycloakUserRepresentation>>();
        var user = users?.SingleOrDefault();
        if (user is null)
        {
            return;
        }

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"admin/realms/eliferpg/users/{user.Id}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var deleteResponse = await _httpClient.SendAsync(deleteRequest);
        deleteResponse.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAdminTokenAsync()
    {
        using var response = await _httpClient.PostAsync(
            "realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "admin-cli",
                ["username"] = "admin",
                ["password"] = "admin",
                ["grant_type"] = "password",
            }));

        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>();
        return token?.AccessToken ?? throw new InvalidOperationException("Keycloak did not return an access token.");
    }

    private sealed record KeycloakUserRepresentation([property: JsonPropertyName("id")] string Id);

    private sealed record KeycloakTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
