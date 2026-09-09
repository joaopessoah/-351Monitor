using System.Net;
using M351.Api.Backoffice;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// CLI backoffice set-org-limit (09/09/2026): o teto de dispositivos deixa de ser UPDATE à mão.
/// Exercita o comando REAL no padrão de SetOrgPlanCommandTests: validação de entrada (nada é
/// escrito com argumento inválido ou slug inexistente), idempotência, --unlimited, e o efeito no
/// gate de enroll de verdade — subir o teto libera a máquina que estava barrada.
/// </summary>
[Collection(ApiCollection.Name)]
public class SetOrgLimitCommandTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<string?> LimiteAsync(Guid tenantId) => await TestDb.ScalarAsync<string>(Cs,
        "SELECT coalesce(device_limit::text, 'null') FROM organizations WHERE id = @id", ("id", tenantId));

    [Fact]
    public async Task SubirOTeto_LiberaOEnrollQueEstavaBarrado_ENaoMexeNoPlano()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Limite Sobe", deviceLimit: 1);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);

        await AgentClient.EnrollAsync(client, fullKey); // ocupa a única licença
        var barrado = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, barrado.StatusCode);

        var exit = await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--device-limit", "2"]);
        Assert.Equal(0, exit);
        Assert.Equal("2", await LimiteAsync(org.Id));

        var liberado = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Created, liberado.StatusCode);

        // plano é decisão separada (set-org-plan): continua trial
        Assert.Equal("trial", await TestDb.ScalarAsync<string>(Cs,
            "SELECT plan FROM organizations WHERE id = @id", ("id", org.Id)));
    }

    [Fact]
    public async Task Unlimited_ZeraOTeto_EMesmoValorEIdempotente()
    {
        var org = await fixture.CreateOrganizationAsync("Org Limite Unl");

        Assert.Equal(0, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--unlimited"]));
        Assert.Equal("null", await LimiteAsync(org.Id));

        // repetir não é erro nem mudança
        Assert.Equal(0, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--unlimited"]));
        Assert.Equal("null", await LimiteAsync(org.Id));

        Assert.Equal(0, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--device-limit", "10"]));
        Assert.Equal("10", await LimiteAsync(org.Id));
    }

    [Fact]
    public async Task EntradaInvalida_SaiComErro_ESemMudarNada()
    {
        var org = await fixture.CreateOrganizationAsync("Org Limite Inv");
        var antes = await LimiteAsync(org.Id);

        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, []));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug]));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--device-limit", "0"]));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--device-limit", "dez"]));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--device-limit", "5", "--unlimited"]));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services, ["--device-limit", "5"]));
        Assert.Equal(1, await SetOrgLimitCommand.RunAsync(fixture.Services,
            ["--org-slug", $"nao-existe-{Guid.NewGuid():N}"[..30], "--device-limit", "5"]));

        Assert.Equal(antes, await LimiteAsync(org.Id));
    }
}
