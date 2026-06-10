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
public class AuthTests(ApiTestFixture fixture)
{
    private async Task<(Guid TenantId, HttpClient Client)> NovoTenantAsync()
    {
        var org = await fixture.CreateOrganizationAsync("Org Auth");
        return (org.Id, fixture.CreateApiClient());
    }

    [Fact]
    public async Task Login_ViewerComSenhaCorreta_RetornaTokensECookieDeRefresh()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(900, body.RootElement.GetProperty("expires_in").GetInt32()); // JWT 15 min (N23)
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("access_token").GetString()));
        Assert.False(string.IsNullOrEmpty(AuthClient.ExtractRefreshCookie(response)));
    }

    [Fact]
    public async Task Login_SenhaErrada_Retorna401Generico()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = "senha-totalmente-errada" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("E-mail ou senha inválidos", text);
    }

    [Fact]
    public async Task Login_Apos10Falhas_BloqueiaPor15Minutos()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        for (var i = 0; i < 10; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = viewer.Email, password = "errada-" + i });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        // mesmo com a senha CORRETA, a conta esta bloqueada (N22)
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var text = await locked.Content.ReadAsStringAsync();
        Assert.Contains("account_locked", text);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        var stored = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == viewer.Id);
        Assert.NotNull(stored.LockedUntil);
        Assert.True(stored.LockedUntil > DateTimeOffset.UtcNow.AddMinutes(13));
        Assert.True(stored.LockedUntil <= DateTimeOffset.UtcNow.AddMinutes(15).AddSeconds(5));
    }

    [Fact]
    public async Task Refresh_RotacaoSingleUse_NegaReusoDoTokenAntigo()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });
        var cookie1 = AuthClient.ExtractRefreshCookie(login);
        Assert.NotNull(cookie1);

        // 1a renovacao: ok, emite cookie novo (rotacao)
        var refresh1 = await AuthClient.RefreshAsync(client, cookie1!);
        Assert.Equal(HttpStatusCode.OK, refresh1.StatusCode);
        var cookie2 = AuthClient.ExtractRefreshCookie(refresh1);
        Assert.NotNull(cookie2);
        Assert.NotEqual(cookie1, cookie2);

        // reuso do token JA ROTACIONADO: negado
        var reuse = await AuthClient.RefreshAsync(client, cookie1!);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // o token novo continua valido (revogacao nao e em cascata - refresh simples, sem familias)
        var refresh2 = await AuthClient.RefreshAsync(client, cookie2!);
        Assert.Equal(HttpStatusCode.OK, refresh2.StatusCode);
    }

    [Fact]
    public async Task Login_OwnerSemMfa_RetornaMfaSetupRequired_E_SetupCompletoEmiteTokens()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var owner = await fixture.CreateUserAsync(tenantId, UserRole.Owner, mfaEnabled: false);

        // 1) login: NAO entrega tokens plenos - exige configurar MFA (obrigatoria p/ Owner/Admin)
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = owner.Email, password = owner.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string? mfaToken;
        using (var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            Assert.Equal("mfa_setup_required", body.RootElement.GetProperty("status").GetString());
            Assert.False(body.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String);
            mfaToken = body.RootElement.GetProperty("mfa_token").GetString();
        }

        Assert.NotNull(mfaToken);

        // o token temporario de MFA NAO autoriza endpoints normais
        using (var blocked = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/devices", mfaToken!))
        {
            var blockedResponse = await client.SendAsync(blocked);
            Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);
        }

        // 2) setup: segredo + otpauth://
        string secret;
        using (var setupRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/setup", mfaToken!))
        {
            var setup = await client.SendAsync(setupRequest);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            using var setupBody = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
            secret = setupBody.RootElement.GetProperty("secret").GetString()!;
            var uri = setupBody.RootElement.GetProperty("otpauth_uri").GetString()!;
            Assert.StartsWith("otpauth://totp/", uri);
            Assert.Contains(secret, uri);
        }

        // 3) verify com TOTP: agora sim tokens plenos
        using (var verifyRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken!,
            new { code = AuthClient.ComputeTotp(secret) }))
        {
            var verify = await client.SendAsync(verifyRequest);
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            using var verifyBody = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
            Assert.Equal("ok", verifyBody.RootElement.GetProperty("status").GetString());
            var access = verifyBody.RootElement.GetProperty("access_token").GetString()!;

            using var listRequest = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/users", access);
            var list = await client.SendAsync(listRequest);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }
    }

    [Fact]
    public async Task Login_OwnerComMfa_ExigeCodigo_E_CodigoErradoFalha()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var owner = await fixture.CreateUserAsync(tenantId, UserRole.Owner, mfaEnabled: true);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = owner.Email, password = owner.Password });
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("mfa_required", body.RootElement.GetProperty("status").GetString());
        var mfaToken = body.RootElement.GetProperty("mfa_token").GetString()!;

        using (var wrongRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken,
            new { code = "000000" }))
        {
            var wrong = await client.SendAsync(wrongRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var rightRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/mfa/verify", mfaToken,
            new { code = AuthClient.ComputeTotp(owner.MfaSecretBase32!) }))
        {
            var right = await client.SendAsync(rightRequest);
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }
    }

    [Fact]
    public async Task Login_Sucesso_GravaAuditoria()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        await AuthClient.LoginAsync(client, viewer);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        var entry = await db.AuditLog.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.Action == AuditActions.Login && a.ActorUserId == viewer.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(entry);
    }

    [Fact]
    public async Task Viewer_NaoAcessaEndpointsDeAdmin()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/users", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // mas endpoints de Viewer funcionam (lista de devices vazia e funcional - F0)
        using var devicesRequest = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/devices", token);
        var devices = await client.SendAsync(devicesRequest);
        Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
    }

    [Fact]
    public async Task Logout_RevogaRefresh()
    {
        var (tenantId, client) = await NovoTenantAsync();
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = viewer.Email, password = viewer.Password });
        var cookie = AuthClient.ExtractRefreshCookie(login);
        string access;
        using (var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            access = body.RootElement.GetProperty("access_token").GetString()!;
        }

        using var logoutRequest = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/auth/logout", access);
        logoutRequest.Headers.Add("Cookie", $"m351_refresh={cookie}");
        var logout = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await AuthClient.RefreshAsync(client, cookie!);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Healthz_RetornaOk()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }
}
