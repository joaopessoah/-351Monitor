using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// GET/PATCH /api/v1/me/email-prefs (F5): preferências de e-mail do PRÓPRIO usuário.
///
/// O gate de plano do jornada_weekly já é coberto em JornadaWeeklyReportTests; aqui ficam os
/// caminhos que faltavam: os defaults de linha ausente (sem materializar linha nenhuma), a
/// persistência de weekly_digest e fleet_alerts, campo ausente que não muda o outro, corpo
/// inválido, e o isolamento, que nesta rota é POR USUÁRIO (a preferência é individual) além
/// de por tenant.
/// </summary>
[Collection(ApiCollection.Name)]
public class EmailPrefsEndpointTests(ApiTestFixture fixture)
{
    private const string Url = "/api/v1/me/email-prefs";

    private string Cs => fixture.Database.ConnectionString;

    private static async Task<JsonDocument> GetPrefsAsync(HttpClient client, string token)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, Url, token);
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"esperado 200, veio {response.StatusCode}: {raw}");
        return JsonDocument.Parse(raw);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, Url, token, body);
        return await client.SendAsync(request);
    }

    private async Task<long> LinhasAsync(Guid userId) => await TestDb.ScalarAsync<long>(Cs,
        "SELECT count(*) FROM user_email_prefs WHERE user_id = @u", ("u", userId));

    [Fact]
    public async Task Get_SemLinha_RetornaDefaults_SemMaterializarNada()
    {
        var org = await fixture.CreateOrganizationAsync("Org Prefs Default");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        using var body = await GetPrefsAsync(client, token);
        Assert.True(body.RootElement.GetProperty("weekly_digest").GetBoolean());
        Assert.True(body.RootElement.GetProperty("fleet_alerts").GetBoolean());
        Assert.False(body.RootElement.GetProperty("jornada_weekly").GetBoolean());

        // ler preferência não pode criar linha (o default vive no código, não no banco)
        Assert.Equal(0L, await LinhasAsync(admin.Id));
    }

    [Fact]
    public async Task Patch_DesligaDigestEAlertas_PersisteComOTenantCerto()
    {
        var org = await fixture.CreateOrganizationAsync("Org Prefs Patch");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        var response = await PatchAsync(client, token, new { weekly_digest = false, fleet_alerts = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("weekly_digest").GetBoolean());
            Assert.False(body.RootElement.GetProperty("fleet_alerts").GetBoolean());
            Assert.False(body.RootElement.GetProperty("jornada_weekly").GetBoolean());
        }

        var row = await TestDb.RowAsync(Cs, """
            SELECT tenant_id, weekly_digest, fleet_alerts, jornada_weekly
            FROM user_email_prefs WHERE user_id = @u
            """, ("u", admin.Id));
        Assert.NotNull(row);
        Assert.Equal(org.Id, (Guid)row!["tenant_id"]!);
        Assert.False((bool)row["weekly_digest"]!);
        Assert.False((bool)row["fleet_alerts"]!);
        Assert.False((bool)row["jornada_weekly"]!);

        // o GET seguinte reflete o que foi gravado
        using var depois = await GetPrefsAsync(client, token);
        Assert.False(depois.RootElement.GetProperty("weekly_digest").GetBoolean());
        Assert.False(depois.RootElement.GetProperty("fleet_alerts").GetBoolean());
    }

    [Fact]
    public async Task Patch_CampoAusente_NaoMudaAOutraPreferencia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Prefs Parcial");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, owner);

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { weekly_digest = false, fleet_alerts = false })).StatusCode);

        // PATCH que mexe SÓ no digest: fleet_alerts continua desligado
        var response = await PatchAsync(client, token, new { weekly_digest = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("weekly_digest").GetBoolean());
        Assert.False(body.RootElement.GetProperty("fleet_alerts").GetBoolean());
    }

    [Fact]
    public async Task Patch_CorpoQueNaoEObjeto_Retorna400_ESemAuth_Retorna401()
    {
        var org = await fixture.CreateOrganizationAsync("Org Prefs Corpo");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var response = await PatchAsync(client, token, new[] { "weekly_digest" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await LinhasAsync(viewer.Id));

        // rota autenticada: anônimo não lê nem escreve preferência de ninguém
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Prefs_SaoIndividuais_NaoVazamEntreUsuariosNemEntreTenants()
    {
        // preferência de notificação é do usuário: o Viewer mexe na PRÓPRIA (sem gate de papel),
        // e a escolha de um jamais aparece para o colega nem para outro tenant
        var orgA = await fixture.CreateOrganizationAsync("Org Prefs Iso A");
        var orgB = await fixture.CreateOrganizationAsync("Org Prefs Iso B");
        var viewerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Viewer);
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var adminB = await fixture.CreateUserAsync(orgB.Id, UserRole.Admin, mfaEnabled: true);

        var client = fixture.CreateApiClient();
        var tokenViewerA = await AuthClient.LoginAsync(client, viewerA);
        var tokenAdminA = await AuthClient.LoginAsync(client, adminA);
        var tokenAdminB = await AuthClient.LoginAsync(client, adminB);

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, tokenViewerA, new { weekly_digest = false })).StatusCode);

        using (var colega = await GetPrefsAsync(client, tokenAdminA))
        {
            Assert.True(colega.RootElement.GetProperty("weekly_digest").GetBoolean());
        }

        using (var outroTenant = await GetPrefsAsync(client, tokenAdminB))
        {
            Assert.True(outroTenant.RootElement.GetProperty("weekly_digest").GetBoolean());
        }

        // só o autor da mudança tem linha, e ela é dele
        Assert.Equal(1L, await LinhasAsync(viewerA.Id));
        Assert.Equal(0L, await LinhasAsync(adminA.Id));
        Assert.Equal(0L, await LinhasAsync(adminB.Id));
    }
}
