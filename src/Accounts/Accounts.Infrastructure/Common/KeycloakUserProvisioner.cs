using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using Microsoft.Extensions.Options;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class KeycloakUserProvisioner(HttpClient httpClient, IOptions<KeycloakOptions> options) : IKeycloakUserProvisioner
{
    private readonly KeycloakOptions _options = options.Value;

    public async ValueTask DisableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await SetUserEnabledAsync(keycloakUserId, enabled: false, cancellationToken);

    public async ValueTask EnableUserAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await SetUserEnabledAsync(keycloakUserId, enabled: true, cancellationToken);

    private async ValueTask SetUserEnabledAsync(KeycloakUserId keycloakUserId, bool enabled, CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"admin/realms/{_options.Realm}/users/{keycloakUserId.Value}")
        {
            Content = JsonContent.Create(new { enabled }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static readonly string[] BuiltinRolePrefixesToExclude = ["default-roles-"];
    private static readonly string[] BuiltinRoleNamesToExclude = ["offline_access", "uma_authorization"];

    public async ValueTask<IReadOnlyList<KeycloakRealmRole>> ListRealmRolesAsync(CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"admin/realms/{_options.Realm}/roles");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var roles = await response.Content.ReadFromJsonAsync<List<KeycloakRoleRepresentation>>(cancellationToken: cancellationToken) ?? [];
        return roles.Where(IsAssignableAppRole).Select(r => new KeycloakRealmRole(r.Name, r.Description)).ToList();
    }

    public async ValueTask<IReadOnlyList<string>?> ListUserRealmRolesAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"admin/realms/{_options.Realm}/users/{keycloakUserId.Value}/role-mappings/realm");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var roles = await response.Content.ReadFromJsonAsync<List<KeycloakRoleRepresentation>>(cancellationToken: cancellationToken) ?? [];
        return roles.Where(IsAssignableAppRole).Select(r => r.Name).ToList();
    }

    public async ValueTask<bool> AssignRealmRoleAsync(KeycloakUserId keycloakUserId, string roleName, CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);
        var role = await FindRoleRepresentationAsync(roleName, adminToken, cancellationToken);
        if (role is null || !IsAssignableAppRole(role))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"admin/realms/{_options.Realm}/users/{keycloakUserId.Value}/role-mappings/realm")
        {
            Content = JsonContent.Create(new[] { role }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async ValueTask<bool> RemoveRealmRoleAsync(KeycloakUserId keycloakUserId, string roleName, CancellationToken cancellationToken)
    {
        var adminToken = await GetAdminTokenAsync(cancellationToken);
        var role = await FindRoleRepresentationAsync(roleName, adminToken, cancellationToken);
        if (role is null)
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"admin/realms/{_options.Realm}/users/{keycloakUserId.Value}/role-mappings/realm")
        {
            Content = JsonContent.Create(new[] { role }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async ValueTask<KeycloakRoleRepresentation?> FindRoleRepresentationAsync(string roleName, string adminToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"admin/realms/{_options.Realm}/roles/{Uri.EscapeDataString(roleName)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KeycloakRoleRepresentation>(cancellationToken: cancellationToken);
    }

    private bool IsAssignableAppRole(KeycloakRoleRepresentation role)
        => !BuiltinRoleNamesToExclude.Contains(role.Name) && !BuiltinRolePrefixesToExclude.Any(role.Name.StartsWith);

    private async ValueTask<string> GetAdminTokenAsync(CancellationToken cancellationToken)
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

    private sealed record KeycloakUserRepresentation([property: JsonPropertyName("id")] string Id);

    private sealed record KeycloakTokenResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record KeycloakRoleRepresentation(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description);
}
