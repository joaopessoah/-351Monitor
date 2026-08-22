using System.Net;
using M351.Api.Backoffice;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// CLI backoffice set-org-plan (F5): é o único caminho que LIGA as features pagas para um
/// cliente que assinou (alertas de saúde de frota e relatório de jornada semanal por e-mail
/// são exclusivos do Pro). Exercita o comando REAL, no mesmo padrão de AgentUpdateManifestTests.
///
/// O que importa aqui é a validação de entrada (plano fora da lista e organização inexistente
/// não podem escrever nada), a idempotência, o efeito no gate de plano de verdade, e o que o
/// comando NÃO faz: mexer em device_limit, que é régua comercial decidida caso a caso.
/// </summary>
[Collection(ApiCollection.Name)]
public class SetOrgPlanCommandTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<string?> PlanoAsync(Guid tenantId) => await TestDb.ScalarAsync<string>(Cs,
        "SELECT plan FROM organizations WHERE id = @id", ("id", tenantId));

    private async Task<string?> LimiteAsync(Guid tenantId) => await TestDb.ScalarAsync<string>(Cs,
        "SELECT device_limit::text FROM organizations WHERE id = @id", ("id", tenantId));

    [Fact]
    public async Task Pro_AtualizaOPlano_AbreOGateDaJornadaSemanal_ENaoMexeNoLimite()
    {
        var org = await fixture.CreateOrganizationAsync("Org Plano Pro");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        // antes do comando, o plano é trial e ligar a jornada semanal é 403
        Assert.Equal("trial", await PlanoAsync(org.Id));
        using (var antes = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/me/email-prefs", token, new { jornada_weekly = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(antes)).StatusCode);
        }

        // caixa alta e espaços não impedem o comando de reconhecer o plano
        var exit = await SetOrgPlanCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--plan", "PRO"]);
        Assert.Equal(0, exit);
        Assert.Equal("pro", await PlanoAsync(org.Id));

        // device_limit é régua comercial do contrato: o comando não pode tocar nele
        Assert.Equal("25", await LimiteAsync(org.Id));

        // e o gate abriu de verdade para o cliente que assinou
        using (var depois = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/me/email-prefs", token, new { jornada_weekly = true }))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(depois)).StatusCode);
        }
    }

    [Fact]
    public async Task MesmoPlano_EIdempotente()
    {
        var org = await fixture.CreateOrganizationAsync("Org Plano Idem");

        Assert.Equal(0, await SetOrgPlanCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--plan", "essencial"]));
        Assert.Equal("essencial", await PlanoAsync(org.Id));

        // repetir não é erro nem mudança
        Assert.Equal(0, await SetOrgPlanCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--plan", "essencial"]));
        Assert.Equal("essencial", await PlanoAsync(org.Id));
    }

    [Fact]
    public async Task PlanoForaDaLista_SaiComErro_ESemMudarNada()
    {
        var org = await fixture.CreateOrganizationAsync("Org Plano Invalido");

        Assert.Equal(1, await SetOrgPlanCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug, "--plan", "ouro"]));
        Assert.Equal("trial", await PlanoAsync(org.Id));

        // argumentos faltando também não escrevem nada
        Assert.Equal(1, await SetOrgPlanCommand.RunAsync(fixture.Services, []));
        Assert.Equal(1, await SetOrgPlanCommand.RunAsync(fixture.Services, ["--org-slug", org.Slug]));
        Assert.Equal(1, await SetOrgPlanCommand.RunAsync(fixture.Services, ["--plan", "pro"]));
        Assert.Equal("trial", await PlanoAsync(org.Id));
    }

    [Fact]
    public async Task SlugInexistente_SaiComErro_ESemAfetarOutrasOrgs()
    {
        var org = await fixture.CreateOrganizationAsync("Org Plano Slug");

        var exit = await SetOrgPlanCommand.RunAsync(fixture.Services,
            ["--org-slug", $"nao-existe-{Guid.NewGuid():N}"[..30], "--plan", "pro"]);
        Assert.Equal(1, exit);

        // nenhuma outra organização virou Pro por tabela (o comando é por slug, um a um)
        Assert.Equal("trial", await PlanoAsync(org.Id));
    }
}
