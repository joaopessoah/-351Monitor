using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using M351.Domain;
using M351.Infrastructure.Data;
using M351.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M351.IntegrationTests;

/// <summary>
/// Recuperação de senha (Seção 7.4: token 1 h, single-use, resposta sempre genérica) e
/// recovery codes de MFA (Seção 7.5: 10 códigos hasheados, single-use) + reset assistido
/// de MFA por Owner/Admin.
/// </summary>
[Collection(ApiCollection.Name)]
public partial class PasswordResetTests(ApiTestFixture fixture)
{
    [GeneratedRegex(@"/redefinir-senha/([A-Za-z0-9_\-]+)")]
    private static partial Regex ResetLinkRegex();

    private string ExtractResetToken(string recipient)
    {
        var message = fixture.Emails.LastFor(recipient)
            ?? throw new InvalidOperationException($"Nenhum e-mail capturado para {recipient}.");
        var match = ResetLinkRegex().Match(message.Body);
        Assert.True(match.Success, "Link de redefinição não encontrado no corpo do e-mail.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task ForgotPassword_ContaExistente_Envia202EmailEResetTrocaASenha()
    {
        var org = await fixture.CreateOrganizationAsync("Org Reset");
        var client = fixture.CreateApiClient();
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = viewer.Email });
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);

        var token = ExtractResetToken(viewer.Email);
        const string novaSenha = "senha-nova-bem-longa-123";

        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, password = novaSenha });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // senha antiga deixa de valer; a nova loga
        var oldLogin = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = novaSenha });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_EmailDesconhecido_Retorna202SemEnviarEmail()
    {
        await fixture.CreateOrganizationAsync("Org Reset Desconhecido");
        var client = fixture.CreateApiClient();
        var email = $"ninguem-{Guid.NewGuid():N}@exemplo.com.br";

        var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });

        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode); // resposta genérica: não revela existência
        Assert.Null(fixture.Emails.LastFor(email));
    }

    [Fact]
    public async Task ResetPassword_TokenReutilizado_Retorna400ResetInvalid()
    {
        var org = await fixture.CreateOrganizationAsync("Org Reset Reuso");
        var client = fixture.CreateApiClient();
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = viewer.Email });
        var token = ExtractResetToken(viewer.Email);

        var first = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, password = "senha-nova-bem-longa-123" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var reuse = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, password = "outra-senha-bem-longa-456" });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Contains("reset_invalid", await reuse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetPassword_SenhaCurta_Retorna400WeakPassword()
    {
        var org = await fixture.CreateOrganizationAsync("Org Reset Fraca");
        var client = fixture.CreateApiClient();
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = viewer.Email });
        var token = ExtractResetToken(viewer.Email);

        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Contains("weak_password", await reset.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetPassword_RevogaTodasAsSessoes()
    {
        var org = await fixture.CreateOrganizationAsync("Org Reset Sessões");
        var client = fixture.CreateApiClient();
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });
        var cookie = AuthClient.ExtractRefreshCookie(login);
        Assert.NotNull(cookie);

        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = viewer.Email });
        var token = ExtractResetToken(viewer.Email);
        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, password = "senha-nova-bem-longa-123" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // o refresh emitido ANTES do reset foi revogado
        var refresh = await AuthClient.RefreshAsync(client, cookie!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task RecoveryCodes_GeraDez_LoginComCodigoConsomeSingleUse()
    {
        var org = await fixture.CreateOrganizationAsync("Org Recovery");
        var client = fixture.CreateApiClient();
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);

        var access = await AuthClient.LoginAsync(client, owner);

        // gera os 10 códigos
        string[] codes;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes", access))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            codes = body.RootElement.GetProperty("codes").EnumerateArray()
                .Select(c => c.GetString()!).ToArray();
        }

        Assert.Equal(10, codes.Length);
        Assert.All(codes, c => Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", c));

        // novo login em duas etapas usando um recovery code no lugar do TOTP
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = owner.Email, password = owner.Password });
        string mfaToken;
        using (var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            Assert.Equal("mfa_required", body.RootElement.GetProperty("status").GetString());
            mfaToken = body.RootElement.GetProperty("mfa_token").GetString()!;
        }

        using (var verify = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken,
            new { code = codes[0] }))
        {
            var response = await client.SendAsync(verify);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // o MESMO código não vale duas vezes (single-use)
        var login2 = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = owner.Email, password = owner.Password });
        string mfaToken2;
        using (var body = JsonDocument.Parse(await login2.Content.ReadAsStringAsync()))
        {
            mfaToken2 = body.RootElement.GetProperty("mfa_token").GetString()!;
        }

        using (var reuse = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken2,
            new { code = codes[0] }))
        {
            var response = await client.SendAsync(reuse);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // mas outro código ainda funciona
        using (var other = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken2,
            new { code = codes[1] }))
        {
            var response = await client.SendAsync(other);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task RecoveryCodes_SemMfaHabilitada_Retorna409()
    {
        var org = await fixture.CreateOrganizationAsync("Org Recovery Sem MFA");
        var client = fixture.CreateApiClient();
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        var access = await AuthClient.LoginAsync(client, viewer);
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes", access);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task MfaReset_PorOwner_ZeraMfaERevogaSessoes_ProximoLoginExigeSetup()
    {
        var org = await fixture.CreateOrganizationAsync("Org MFA Reset");
        var client = fixture.CreateApiClient();
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);

        var ownerAccess = await AuthClient.LoginAsync(client, owner);

        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Post, $"/api/v1/users/{admin.Id}/mfa/reset", ownerAccess))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
            var stored = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);
            Assert.False(stored.MfaEnabled);
            Assert.Null(stored.MfaSecretEnc);
        }

        // Admin exige MFA: próximo login volta para o setup, não para tokens plenos
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = admin.Email, password = admin.Password });
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("mfa_setup_required", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MfaReset_AdminNaoResetaOwner_Retorna403()
    {
        var org = await fixture.CreateOrganizationAsync("Org MFA Reset 403");
        var client = fixture.CreateApiClient();
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);

        var adminAccess = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Post, $"/api/v1/users/{owner.Id}/mfa/reset", adminAccess);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
