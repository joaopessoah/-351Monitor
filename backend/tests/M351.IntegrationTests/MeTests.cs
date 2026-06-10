using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

[Collection(ApiCollection.Name)]
public class MeTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Me_RetornaPerfilPapelEOrganizacaoDoToken()
    {
        var org = await fixture.CreateOrganizationAsync("Org Me");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/me", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = body.RootElement.GetProperty("user");
        Assert.Equal(viewer.Id.ToString(), user.GetProperty("id").GetString());
        Assert.Equal(viewer.Email, user.GetProperty("email").GetString());
        Assert.Equal("viewer", user.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(user.GetProperty("display_name").GetString()));

        var organization = body.RootElement.GetProperty("organization");
        Assert.Equal(org.Id.ToString(), organization.GetProperty("id").GetString());
        Assert.Equal("Org Me", organization.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(organization.GetProperty("slug").GetString()));
        Assert.Equal("America/Sao_Paulo", organization.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task Me_SemToken_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
