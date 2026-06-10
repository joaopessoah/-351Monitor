using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Entities;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// SUITE DE ISOLAMENTO MULTI-TENANT (gate de CI - Secao 11.1 / Principio 4):
/// dois tenants populados; autenticado no tenant A, TODO endpoint do portal com IDs do
/// tenant B responde 404 (nunca 403) e nenhuma listagem vaza recursos de B.
/// </summary>
[Collection(ApiCollection.Name)]
public class TenantIsolationTests(ApiTestFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private string _tokenA = null!;

    private TestUser _ownerA = null!;
    private TestUser _userB = null!;
    private Device _deviceA = null!;
    private Device _deviceB = null!;
    private EnrollmentKey _keyB = null!;

    public async Task InitializeAsync()
    {
        _client = fixture.CreateApiClient();

        var orgA = await fixture.CreateOrganizationAsync("Tenant A");
        var orgB = await fixture.CreateOrganizationAsync("Tenant B");

        // tenant A: owner com MFA (obrigatoria para Owner) e recursos proprios
        _ownerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Owner, mfaEnabled: true);
        _deviceA = await fixture.CreateDeviceAsync(orgA.Id, "NB-TENANT-A");
        await fixture.CreateEnrollmentKeyAsync(orgA.Id, "chave-a");

        // tenant B: usuarios e recursos que JAMAIS podem aparecer para A
        await fixture.CreateUserAsync(orgB.Id, UserRole.Owner, mfaEnabled: true);
        _userB = await fixture.CreateUserAsync(orgB.Id, UserRole.Viewer);
        _deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-TENANT-B");
        _keyB = await fixture.CreateEnrollmentKeyAsync(orgB.Id, "chave-b");

        _tokenA = await AuthClient.LoginAsync(_client, _ownerA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, _tokenA, body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task SanidadeRecursoProprio_TenantA_Acessa_SeuDevice()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/devices/{_deviceA.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/users/{_userB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{_userB.Id}", new { role = "admin" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{_userB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDeviceDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/devices/{_deviceB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteEnrollmentKeyDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Delete, $"/api/v1/enrollment-keys/{_keyB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListagemDeUsuarios_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var emails = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("email").GetString())
            .ToList();

        Assert.Contains(_ownerA.Email, emails);
        Assert.DoesNotContain(_userB.Email, emails);
    }

    [Fact]
    public async Task ListagemDeDevices_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/devices?page_size=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(_deviceA.Id, ids);
        Assert.DoesNotContain(_deviceB.Id, ids);
    }

    [Fact]
    public async Task ListagemDeEnrollmentKeys_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/enrollment-keys");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(k => k.GetProperty("id").GetGuid())
            .ToList();

        Assert.DoesNotContain(_keyB.Id, ids);
    }

    [Fact]
    public async Task RespostaCruzada_NuncaEh403_SempreEh404()
    {
        // a distincao importa: 403 confirmaria a existencia do recurso de outro tenant
        var alvos = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, $"/api/v1/users/{_userB.Id}"),
            (HttpMethod.Get, $"/api/v1/devices/{_deviceB.Id}"),
            (HttpMethod.Delete, $"/api/v1/enrollment-keys/{_keyB.Id}"),
        };

        foreach (var (method, url) in alvos)
        {
            var response = await SendAsync(method, url);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
