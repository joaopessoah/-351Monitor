using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M351.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InviteTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task FluxoCompleto_OwnerConvidaViewer_AceiteDefineSenhaEEntregaTokens()
    {
        var org = await fixture.CreateOrganizationAsync("Org Convites");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);

        var inviteEmail = $"convidado-{Guid.NewGuid():N}@teste.com.br";

        // 1) Owner convida viewer - e-mail com link de 7 dias e disparado
        using var inviteRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/users/invitations", ownerToken,
            new { email = inviteEmail, role = "viewer", display_name = "Convidado Teste" });
        var invite = await client.SendAsync(inviteRequest);
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        var token = fixture.Emails.ExtractInviteToken(inviteEmail);

        // 2) aceite define a senha (>= 12 chars) e entrega tokens (viewer: MFA opcional)
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token, password = "senha-segura-12chars", display_name = "Convidado Teste" });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        using (var body = JsonDocument.Parse(await accept.Content.ReadAsStringAsync()))
        {
            Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("access_token").GetString()));
        }

        // 3) convite e single-use
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token, password = "outra-senha-12chars" });
        Assert.Equal(HttpStatusCode.Gone, reuse.StatusCode);

        // 4) aceite auditado
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        var audit = await db.AuditLog.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == org.Id && a.Action == AuditActions.InviteAccept);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task ConviteExpirado_Retorna410()
    {
        var org = await fixture.CreateOrganizationAsync("Org Convite Expirado");
        var (_, token, _) = await fixture.CreateInvitationAsync(
            org.Id, UserRole.Viewer, DateTimeOffset.UtcNow.AddMinutes(-1));

        var client = fixture.CreateApiClient();
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token, password = "senha-segura-12chars" });

        Assert.Equal(HttpStatusCode.Gone, accept.StatusCode);
        Assert.Contains("invite_expired", await accept.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConviteComSenhaCurta_Retorna400()
    {
        var org = await fixture.CreateOrganizationAsync("Org Senha Curta");
        var (_, token, _) = await fixture.CreateInvitationAsync(
            org.Id, UserRole.Viewer, DateTimeOffset.UtcNow.AddDays(7));

        var client = fixture.CreateApiClient();
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token, password = "curta123456" }); // 11 chars < 12

        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
        Assert.Contains("weak_password", await accept.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConviteDeAdmin_AceiteExigeSetupDeMfa()
    {
        var org = await fixture.CreateOrganizationAsync("Org Admin MFA");
        var (_, token, _) = await fixture.CreateInvitationAsync(
            org.Id, UserRole.Admin, DateTimeOffset.UtcNow.AddDays(7));

        var client = fixture.CreateApiClient();
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token, password = "senha-segura-12chars" });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        string mfaToken;
        using (var body = JsonDocument.Parse(await accept.Content.ReadAsStringAsync()))
        {
            // papel exige MFA: o aceite NAO entrega tokens plenos
            Assert.Equal("mfa_setup_required", body.RootElement.GetProperty("status").GetString());
            mfaToken = body.RootElement.GetProperty("mfa_token").GetString()!;
        }

        // setup + verify concluem o fluxo e entregam tokens plenos
        string secret;
        using (var setupRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/setup", mfaToken))
        {
            var setup = await client.SendAsync(setupRequest);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            using var setupBody = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
            secret = setupBody.RootElement.GetProperty("secret").GetString()!;
        }

        using var verifyRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken,
            new { code = AuthClient.ComputeTotp(secret) });
        var verify = await client.SendAsync(verifyRequest);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        using var verifyBody = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.Equal("ok", verifyBody.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ConviteInvalido_Retorna404()
    {
        var client = fixture.CreateApiClient();
        var accept = await client.PostAsJsonAsync("/api/v1/auth/invite/accept",
            new { token = "token-inexistente", password = "senha-segura-12chars" });
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Fact]
    public async Task PreviewDoConvite_RetornaOrgPapelEmailEMfaRequired()
    {
        var org = await fixture.CreateOrganizationAsync("Org Preview Convite");
        var (invitation, token, _) = await fixture.CreateInvitationAsync(
            org.Id, UserRole.Admin, DateTimeOffset.UtcNow.AddDays(7));

        var client = fixture.CreateApiClient();
        var preview = await client.GetAsync($"/api/v1/auth/invite/{token}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(invitation.Email, body.RootElement.GetProperty("email").GetString());
        Assert.Equal("admin", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("Org Preview Convite", body.RootElement.GetProperty("organization_name").GetString());
        Assert.True(body.RootElement.GetProperty("mfa_required").GetBoolean()); // admin exige MFA
    }

    [Fact]
    public async Task PreviewDoConvite_Inexistente404_Expirado410()
    {
        var org = await fixture.CreateOrganizationAsync("Org Preview Invalido");
        var (_, expiredToken, _) = await fixture.CreateInvitationAsync(
            org.Id, UserRole.Viewer, DateTimeOffset.UtcNow.AddMinutes(-1));

        var client = fixture.CreateApiClient();

        var notFound = await client.GetAsync("/api/v1/auth/invite/token-inexistente");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var gone = await client.GetAsync($"/api/v1/auth/invite/{expiredToken}");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Contains("invite_expired", await gone.Content.ReadAsStringAsync());
    }
}
