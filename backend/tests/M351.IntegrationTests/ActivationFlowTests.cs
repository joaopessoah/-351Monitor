using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Funil de ativação (F5): reenvio de convite (novo token 7 dias, o link antigo morre) e
/// checklist de primeiros passos da Seção 8.3 passo 4 (dispensável, estado da ORG no servidor).
/// </summary>
[Collection(ApiCollection.Name)]
public class ActivationFlowTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, Guid TenantId, string AdminToken)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, admin));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token, object? body = null)
        => AuthClient.AuthorizedRequest(method, url, token, body);

    [Fact]
    public async Task ResendInvitation_GeraTokenNovoEInvalidaOAnterior()
    {
        var (client, _, adminToken) = await SetupAsync("Org Reenvio");
        var email = $"convidada-{Guid.NewGuid():N}@exemplo.com.br";

        // convite original
        Guid userId;
        using (var invite = Authorized(HttpMethod.Post, "/api/v1/users/invitations", adminToken,
            new { email, role = "viewer", display_name = "Convidada" }))
        {
            var response = await client.SendAsync(invite);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            userId = body.RootElement.GetProperty("user_id").GetGuid();
        }

        var oldToken = fixture.Emails.ExtractInviteToken(email);

        // reenvio: token novo, e-mail novo
        using (var resend = Authorized(HttpMethod.Post, $"/api/v1/users/{userId}/invitations/resend", adminToken))
        {
            var response = await client.SendAsync(resend);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var newToken = fixture.Emails.ExtractInviteToken(email);
        Assert.NotEqual(oldToken, newToken);

        // o link ANTIGO morreu; o novo funciona
        var oldPreview = await client.GetAsync($"/api/v1/auth/invite/{oldToken}");
        Assert.Equal(HttpStatusCode.NotFound, oldPreview.StatusCode);

        var newPreview = await client.GetAsync($"/api/v1/auth/invite/{newToken}");
        Assert.Equal(HttpStatusCode.OK, newPreview.StatusCode);

        // aceitar com o token novo ativa a conta
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token = newToken, password = "senha-da-convidada-123", display_name = "Convidada" });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
    }

    [Fact]
    public async Task ResendInvitation_UsuarioJaAtivo_Retorna409()
    {
        var (client, tenantId, adminToken) = await SetupAsync("Org Reenvio Ativo");
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        using var resend = Authorized(HttpMethod.Post, $"/api/v1/users/{viewer.Id}/invitations/resend", adminToken);
        var response = await client.SendAsync(resend);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ChecklistOnboarding_DismissERestore_PersistemNaOrgEValemNoMe()
    {
        var (client, _, adminToken) = await SetupAsync("Org Checklist");

        async Task<JsonElement?> DismissedAtAsync()
        {
            using var me = Authorized(HttpMethod.Get, "/api/v1/me", adminToken);
            var response = await client.SendAsync(me);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var org = body.RootElement.GetProperty("organization");
            return org.TryGetProperty("onboarding_checklist_dismissed_at", out var el)
                ? el.Clone()
                : null;
        }

        var initial = await DismissedAtAsync();
        Assert.True(initial is null || initial.Value.ValueKind == JsonValueKind.Null);

        using (var dismiss = Authorized(HttpMethod.Post, "/api/v1/organization/onboarding-checklist/dismiss", adminToken))
        {
            var response = await client.SendAsync(dismiss);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var afterDismiss = await DismissedAtAsync();
        Assert.Equal(JsonValueKind.String, afterDismiss!.Value.ValueKind);

        using (var restore = Authorized(HttpMethod.Delete, "/api/v1/organization/onboarding-checklist/dismiss", adminToken))
        {
            var response = await client.SendAsync(restore);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var afterRestore = await DismissedAtAsync();
        Assert.True(afterRestore is null || afterRestore.Value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ChecklistOnboarding_ViewerNaoDispensa_Retorna403()
    {
        var (client, tenantId, _) = await SetupAsync("Org Checklist 403");
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);
        var viewerToken = await AuthClient.LoginAsync(client, viewer);

        using var dismiss = Authorized(HttpMethod.Post, "/api/v1/organization/onboarding-checklist/dismiss", viewerToken);
        var response = await client.SendAsync(dismiss);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
