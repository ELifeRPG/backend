using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Microsoft.Extensions.Options;

namespace ELifeRPG.Accounts.Infrastructure.Common;

/// <summary>
/// Talks to the game-account linking endpoints the keycloak-spi-reforger provider adds to the realm.
///
/// Both endpoints are trusted-caller only: they require a service-account token carrying
/// <c>accounts:bohemia-gameaccount:manage</c>. That scope permits minting a PIN for an arbitrary
/// Bohemia ID, which is enough to claim someone else's game identity, so it is granted to
/// <c>account-service</c> and must never reach a public or user-facing client.
/// </summary>
public sealed class KeycloakBohemiaGameAccountLinker(HttpClient httpClient, IOptions<KeycloakOptions> options)
    : IBohemiaGameAccountLinker
{
    private readonly KeycloakOptions _options = options.Value;

    public async ValueTask<string?> MintLinkPinAsync(GameId bohemiaId, CancellationToken cancellationToken)
    {
        var token = await GetServiceAccountTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"realms/{_options.Realm}/bohemia-gameaccount/pin")
        {
            Content = JsonContent.Create(new { bohemiaId = bohemiaId.Value.ToString() }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        // The SPI refuses to mint for an already-bound Bohemia ID. That is its first line of
        // defence against binding one game identity to two accounts, and it means the caller's
        // "unlinked" view is stale rather than that anything went wrong.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var minted = await response.Content.ReadFromJsonAsync<MintPinResponse>(cancellationToken: cancellationToken);
        return minted?.Pin ?? throw new InvalidOperationException("Keycloak did not return a link PIN.");
    }

    public async ValueTask<KeycloakUserId?> FindKeycloakUserIdAsync(GameId bohemiaId, CancellationToken cancellationToken)
    {
        var token = await GetServiceAccountTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"realms/{_options.Realm}/bohemia-gameaccount/status?bohemiaId={Uri.EscapeDataString(bohemiaId.Value.ToString())}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var status = await response.Content.ReadFromJsonAsync<LinkStatusResponse>(cancellationToken: cancellationToken);
        return status is { Linked: true, KeycloakUserId: { } id } ? new KeycloakUserId(Guid.Parse(id)) : null;
    }

    private async ValueTask<string> GetServiceAccountTokenAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"realms/{_options.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ProvisioningClientId,
                ["client_secret"] = _options.ProvisioningClientSecret,
                ["grant_type"] = "client_credentials",
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(cancellationToken: cancellationToken);
        return token?.AccessToken ?? throw new InvalidOperationException("Keycloak did not return an access token.");
    }

    private sealed record MintPinResponse(
        [property: JsonPropertyName("pin")] string Pin,
        [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

    private sealed record LinkStatusResponse(
        [property: JsonPropertyName("linked")] bool Linked,
        [property: JsonPropertyName("keycloakUserId")] string? KeycloakUserId);

    private sealed record KeycloakTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
