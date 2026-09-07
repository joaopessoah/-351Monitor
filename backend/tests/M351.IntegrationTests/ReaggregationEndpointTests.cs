using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// POST /api/v1/reaggregation (F6, decisão 7 do spec): reagregação retroativa sob demanda até
/// 366 dias, para o histórico refletir a classificação vigente e o balde "sem classificação"
/// deixar de nascer zerado. Admin+, auditada, com a janela validada nas duas pontas.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReaggregationEndpointTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, string Token, Guid TenantId)> SetupAsync(string prefix, UserRole role)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var user = await fixture.CreateUserAsync(org.Id, role, mfaEnabled: role != UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, user);
        return (client, token, org.Id);
    }

    [Fact]
    public async Task Reaggregation_JanelaAcimaDoTeto_400()
    {
        var (client, token, _) = await SetupAsync("Reag", UserRole.Admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 400 });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Reaggregation_JanelaZero_400()
    {
        var (client, token, _) = await SetupAsync("ReagZ", UserRole.Admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Reaggregation_Viewer_403()
    {
        var (client, token, _) = await SetupAsync("ReagV", UserRole.Viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 30 });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Reaggregation_Admin_Aceita365Dias_EAudita()
    {
        var (client, token, tenantId) = await SetupAsync("ReagOk", UserRole.Admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 365 });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);

        using var json = JsonDocument.Parse(body);
        Assert.Equal(365, json.RootElement.GetProperty("days").GetInt32());
        // organização sem intervalo algum: nada a enfileirar, e isso é resposta válida
        Assert.Equal(0, json.RootElement.GetProperty("enqueued").GetInt32());

        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'reaggregate'", ("t", tenantId));
        Assert.Equal(1, audited);
    }

    [Fact]
    public async Task Reaggregation_SemCorpo_UsaJanelaDefaultDe30Dias()
    {
        var (client, token, _) = await SetupAsync("ReagDef", UserRole.Admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);

        using var json = JsonDocument.Parse(body);
        Assert.Equal(30, json.RootElement.GetProperty("days").GetInt32());
    }
}
