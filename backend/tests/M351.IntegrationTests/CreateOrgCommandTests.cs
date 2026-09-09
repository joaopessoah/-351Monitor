using M351.Api.Backoffice;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// CLI backoffice create-org: é o ÚNICO caminho de criação de tenant (sem signup self-service).
/// Aqui interessa o que a decisão de 09/09/2026 mudou: todo trial nasce com 10 licenças (N24),
/// --device-limit cobre o piloto negociado fora do padrão, e valor inválido não cria nada.
/// </summary>
[Collection(ApiCollection.Name)]
public class CreateOrgCommandTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<string?> OrgAsync(string slug) => await TestDb.ScalarAsync<string>(Cs,
        "SELECT plan || '/' || coalesce(device_limit::text, 'null') FROM organizations WHERE slug = @slug",
        ("slug", slug));

    private static string NovoSlug() => $"createorg-{Guid.NewGuid():N}"[..28];

    private static string NovoEmail() => $"dono-{Guid.NewGuid():N}@teste.com.br";

    [Fact]
    public async Task Padrao_NasceTrialCom10Licencas()
    {
        var slug = NovoSlug();
        var exit = await CreateOrgCommand.RunAsync(fixture.Services,
            ["--name", "Org Create Padrão", "--owner-email", NovoEmail(), "--slug", slug]);

        Assert.Equal(0, exit);
        Assert.Equal(10, CreateOrgCommand.TrialDeviceLimit); // a decisão comercial, explícita
        Assert.Equal("trial/10", await OrgAsync(slug));
    }

    [Fact]
    public async Task DeviceLimit_Informado_ValeNoLugarDoPadrao()
    {
        var slug = NovoSlug();
        var exit = await CreateOrgCommand.RunAsync(fixture.Services,
            ["--name", "Org Create Tres", "--owner-email", NovoEmail(), "--slug", slug, "--device-limit", "3"]);

        Assert.Equal(0, exit);
        Assert.Equal("trial/3", await OrgAsync(slug));
    }

    [Fact]
    public async Task DeviceLimit_Invalido_SaiComErro_ESemCriarNada()
    {
        var slug = NovoSlug();
        var email = NovoEmail();

        Assert.Equal(1, await CreateOrgCommand.RunAsync(fixture.Services,
            ["--name", "Org Create Zero", "--owner-email", email, "--slug", slug, "--device-limit", "0"]));
        Assert.Equal(1, await CreateOrgCommand.RunAsync(fixture.Services,
            ["--name", "Org Create Texto", "--owner-email", email, "--slug", slug, "--device-limit", "dez"]));

        Assert.Null(await OrgAsync(slug));
    }
}
